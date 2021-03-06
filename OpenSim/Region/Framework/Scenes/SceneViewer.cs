/*
 * Copyright (c) Contributors, http://aurora-sim.org/, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the Aurora-Sim Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */
//#define UseRemovingEntityUpdates
#define UseDictionaryForEntityUpdates
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Reflection;
using System.Linq;
using System.Text;
using Timer = System.Timers.Timer;
using System.Timers;
using System.Threading;
using OpenMetaverse;
using log4net;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using Mischel.Collections;
using Nini.Config;

namespace OpenSim.Region.Framework.Scenes
{
    public class SceneViewer : ISceneViewer
    {
        #region Declares

        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        
        private const double MINVIEWDSTEP = 16;
        private const double MINVIEWDSTEPSQ = MINVIEWDSTEP * MINVIEWDSTEP;

        protected IScenePresence m_presence;
        protected IScene m_scene;
        /// <summary>
        /// Have we sent all of the objects in the sim that the client can see for the first time?
        /// </summary>
        protected bool m_SentInitialObjects = false;
        protected volatile bool m_queueing = false;
        protected volatile bool m_inUse = false;
        protected Prioritizer m_prioritizer;
        protected Culler m_culler;
        protected bool m_forceCullCheck = false;
        private object m_presenceUpdatesToSendLock = new object ();
        private object m_presenceAnimationsToSendLock = new object ();
        private object m_objectPropertiesToSendLock = new object ();
        private object m_objectUpdatesToSendLock = new object ();
#if UseRemovingEntityUpdates
        private OrderedDictionary/*<UUID, EntityUpdate>*/ m_presenceUpdatesToSend = new OrderedDictionary/*<UUID, EntityUpdate>*/ ();
#elif UseDictionaryForEntityUpdates
        private Dictionary<UUID, EntityUpdate> m_presenceUpdatesToSend = new Dictionary<UUID, EntityUpdate> ();
#else
        private Queue<EntityUpdate> m_presenceUpdatesToSend = new Queue<EntityUpdate>();
#endif
        private Queue<AnimationGroup> m_presenceAnimationsToSend = new Queue<AnimationGroup>/*<UUID, AnimationGroup>*/ ();
        private OrderedDictionary/*<UUID, EntityUpdate>*/ m_objectUpdatesToSend = new OrderedDictionary/*<UUID, EntityUpdate>*/ ();
        private OrderedDictionary/*<UUID, ISceneChildEntity>*/ m_objectPropertiesToSend = new OrderedDictionary/*<UUID, ISceneChildEntity>*/ ();
        /*private List<UUID> m_EntitiesInPacketQueue = new List<UUID>();
        private List<UUID> m_AnimationsInPacketQueue = new List<UUID>();
        private List<UUID> m_PropertiesInPacketQueue = new List<UUID>();*/
        private HashSet<ISceneEntity> lastGrpsInView = new HashSet<ISceneEntity> ();
        private HashSet<IScenePresence> lastPresencesInView = new HashSet<IScenePresence> ();
        private Dictionary<UUID, IScenePresence> lastPresencesDInView = new Dictionary<UUID, IScenePresence> ();
        private Vector3 m_lastUpdatePos;
        private int m_numberOfLoops = 0;
        private Timer m_drawDistanceChangedTimer;
        private object m_drawDistanceTimerLock = new object ();
        private const int NUMBER_OF_LOOPS_TO_WAIT = 30;

        private const float PresenceSendPercentage = 0.60f;
        private const float PrimSendPercentage = 0.40f;

        public IPrioritizer Prioritizer
        {
            get { return m_prioritizer; }
        }

        public ICuller Culler
        {
            get { return m_culler; }
        }

        #endregion

        #region Constructor

        public SceneViewer (IScenePresence presence)
        {
            m_presence = presence;
            m_scene = presence.Scene;
            m_presence.OnSignificantClientMovement += SignificantClientMovement;
            m_presence.Scene.EventManager.OnMakeChildAgent += EventManager_OnMakeChildAgent;
            m_scene.EventManager.OnClosingClient += EventManager_OnClosingClient;
            m_presence.Scene.AuroraEventManager.RegisterEventHandler ("DrawDistanceChanged", AuroraEventManager_OnGenericEvent);
            m_presence.Scene.AuroraEventManager.RegisterEventHandler ("SignficantCameraMovement", AuroraEventManager_OnGenericEvent);
            m_prioritizer = new Prioritizer (presence.Scene);
            m_culler = new Culler (presence.Scene);
        }

        void EventManager_OnClosingClient (IClientAPI client)
        {
            if (lastPresencesDInView.ContainsKey (client.AgentId))
            {
                lastPresencesInView.Remove (lastPresencesDInView[client.AgentId]);
                lastPresencesDInView.Remove (client.AgentId);
            }
        }

        void EventManager_OnMakeChildAgent (IScenePresence presence, Services.Interfaces.GridRegion destination)
        {
            RemoveAvatarFromView (presence);
        }

        object AuroraEventManager_OnGenericEvent (string FunctionName, object parameters)
        {
            if (m_culler != null && m_culler.UseCulling && FunctionName == "DrawDistanceChanged")
            {
                IScenePresence sp = (IScenePresence)parameters;
                if (sp.UUID != m_presence.UUID)
                    return null; //Only want our av

                //Draw Distance chagned, force a cull check
                m_forceCullCheck = true;
                //Don't do this immediately as the viewer may keep changing the draw distance
                lock (m_drawDistanceTimerLock)
                {
                    if (m_drawDistanceChangedTimer != null)
                        m_drawDistanceChangedTimer.Stop (); //Stop any old timers
                    m_drawDistanceChangedTimer = new Timer (); //Fire this again in 3 seconds so that we do send prims to children agents
                    m_drawDistanceChangedTimer.Interval = 3000;
                    m_drawDistanceChangedTimer.Elapsed += m_drawDistanceChangedTimer_Elapsed;
                    m_drawDistanceChangedTimer.Start ();
                }
                //SignificantClientMovement (m_presence.ControllingClient);
            }
            else if (FunctionName == "SignficantCameraMovement")
            {
                //Camera chagned, do a cull check
                m_forceCullCheck = true;
                //Don't do this immediately as the viewer may keep changing the camera quickly
                lock (m_drawDistanceTimerLock)
                {
                    if (m_drawDistanceChangedTimer != null)
                        m_drawDistanceChangedTimer.Stop (); //Stop any old timers
                    m_drawDistanceChangedTimer = new Timer (); //Fire this again in 3 seconds so that we do send prims to children agents
                    m_drawDistanceChangedTimer.Interval = 3000;
                    m_drawDistanceChangedTimer.Elapsed += m_drawDistanceChangedTimer_Elapsed;
                    m_drawDistanceChangedTimer.Start ();
                }
            }
            return null;
        }

        void m_drawDistanceChangedTimer_Elapsed (object sender, ElapsedEventArgs e)
        {
            lock(m_drawDistanceTimerLock)
                m_drawDistanceChangedTimer.Stop ();
            if(m_presence != null)
                SignificantClientMovement ();
        }

        #endregion

        #region Enqueue/Remove updates for entities

        public void QueuePresenceForUpdate (IScenePresence presence, PrimUpdateFlags flags)
        {
            if (m_culler != null && !m_culler.ShowEntityToClient (m_presence, presence, m_scene))
            {
                //They are out of view and they changed, we need to update them when they do come in view
                lastPresencesInView.Remove (presence);
                lastPresencesDInView.Remove (presence.UUID);
                return; // if 2 far ignore
            }
            //Is this really necessary? -7/21
            //Very much so... the client cannot get a terse update before a full update -7/25
            if (!lastPresencesDInView.ContainsKey (presence.UUID))
                return;//Only send updates if they are in view
            QueuePresenceForUpdateInternal (presence, flags);
        }

        public void QueuePresenceForFullUpdate (IScenePresence presence, bool forced)
        {
            if(!forced && m_culler != null && !m_culler.ShowEntityToClient(m_presence, presence, m_scene))
            {
                //They are out of view and they changed, we need to update them when they do come in view
                lastPresencesInView.Remove (presence);
                lastPresencesDInView.Remove (presence.UUID);
                return; // if 2 far ignore
            }
            if (!lastPresencesDInView.ContainsKey (presence.UUID))
            {
                lastPresencesInView.Add (presence);
                lastPresencesDInView.Add (presence.UUID, presence);
            }
            else if(!forced)//Only send one full update please!
                return;

            SendFullUpdateForPresence (presence);
            AddPresenceUpdate (presence, PrimUpdateFlags.ForcedFullUpdate);
        }

        private void QueuePresenceForUpdateInternal (IScenePresence presence, PrimUpdateFlags flags)
        {
            if (m_presence.DrawDistance != 0 &&
                (!(m_presence.DrawDistance > m_presence.Scene.RegionInfo.RegionSizeX &&
                    m_presence.DrawDistance > m_presence.Scene.RegionInfo.RegionSizeY)) &&
                    !lastPresencesDInView.ContainsKey (presence.UUID))
            {
                //The presence just entered our view, we need to send a full update
                lastPresencesInView.Add (presence);
                lastPresencesDInView.Add (presence.UUID, presence);
                SendFullUpdateForPresence (presence);
                return;
            }

            AddPresenceUpdate (presence, flags);
        }

        private void AddPresenceUpdate (IScenePresence presence, PrimUpdateFlags flags)
        {
            lock (m_presenceUpdatesToSendLock)
            {
#if UseRemovingEntityUpdates
                EntityUpdate o = (EntityUpdate)m_presenceUpdatesToSend[presence.UUID];
                if (o == null)
                    o = new EntityUpdate (presence, flags);
                else
                {
                    if ((o.Flags & flags) == o.Flags)
                        return; //Same, leave it alone!
                    o.Flags |= flags;
                    return;//All done, its updated
                }

                if (m_presence.UUID == presence.UUID) //Its us, set us first!
                    m_presenceUpdatesToSend.Insert (0, presence.UUID, o);
                else //Not us, set at the end
                    m_presenceUpdatesToSend.Insert (m_presenceUpdatesToSend.Count, presence.UUID, o);
#elif UseDictionaryForEntityUpdates
                EntityUpdate o = null;
                if(!m_presenceUpdatesToSend.TryGetValue(presence.UUID, out o))
                    o = new EntityUpdate (presence, flags);
                else
                {
                    if ((o.Flags & flags) == o.Flags)
                        return; //Same, leave it alone!
                    o.Flags |= flags;
                    return;//All done, its updated, no need to readd
                }

                m_presenceUpdatesToSend[presence.UUID] = o;
#else
                m_presenceUpdatesToSend.Enqueue (new EntityUpdate (presence, flags));
#endif
            }
        }

        public void QueuePresenceForAnimationUpdate(IScenePresence presence, AnimationGroup animation)
        {
            if (m_culler != null && !m_culler.ShowEntityToClient (m_presence, presence, m_scene))
            {
                //They are out of view and they changed, we need to update them when they do come in view
                lastPresencesInView.Remove (presence);
                lastPresencesDInView.Remove (presence.UUID);
                return; // if 2 far ignore
            }

            //Send a terse as well, since we are sending an animation
            QueuePresenceForUpdateInternal (presence, PrimUpdateFlags.TerseUpdate);

            lock (m_presenceAnimationsToSendLock)
                m_presenceAnimationsToSend.Enqueue(animation);
        }

        /// <summary>
        /// Add the objects to the queue for which we need to send an update to the client
        /// </summary>
        /// <param name="part"></param>
        public void QueuePartForUpdate (ISceneChildEntity part, PrimUpdateFlags flags)
        {
            if(m_presence == null)
                return;
            if (m_culler != null && !m_culler.ShowEntityToClient (m_presence, part.ParentEntity, m_scene))
            {
                //They are out of view and they changed, we need to update them when they do come in view
                lastGrpsInView.Remove (part.ParentEntity);
                return; // if 2 far ignore
            }
            if ((!(m_presence.DrawDistance > m_presence.Scene.RegionInfo.RegionSizeX &&
                    m_presence.DrawDistance > m_presence.Scene.RegionInfo.RegionSizeY)) && !lastGrpsInView.Contains (part.ParentEntity))
            {
                //This object entered our draw distance on its own, and we havn't seen it before
                flags = PrimUpdateFlags.ForcedFullUpdate;
                foreach(ISceneChildEntity child in part.ParentEntity.ChildrenEntities())
                {
                    EntityUpdate update = new EntityUpdate(child, flags);
                    QueueEntityUpdate(update);
                }
                lastGrpsInView.Add(part.ParentEntity);
                return;
            }

            EntityUpdate o = new EntityUpdate (part, flags);
            QueueEntityUpdate (o);
        }

        private void QueueEntityUpdate(EntityUpdate update)
        {
            lock (m_objectUpdatesToSendLock)
            {
                EntityUpdate o = (EntityUpdate)m_objectUpdatesToSend[update.Entity.UUID];
                if (o == null)
                    o = update;
                else
                {
                    if (o.Flags == update.Flags)
                        return; //Same, leave it alone!
                    o.Flags = o.Flags | update.Flags;
                    m_objectUpdatesToSend.Remove(update.Entity.UUID);
                }
                m_objectUpdatesToSend.Insert(m_objectUpdatesToSend.Count, o.Entity.UUID, o);
            }
        }

        public void QueuePartsForPropertiesUpdate(ISceneChildEntity[] entities)
        {
            lock (m_objectPropertiesToSendLock)
            {
                foreach (ISceneChildEntity entity in entities)
                {
                    if (m_culler != null && !m_culler.ShowEntityToClient (m_presence, entity.ParentEntity, m_scene))
                        continue; // if 2 far ignore

                    m_objectPropertiesToSend.Remove(entity.UUID);
                    //Insert at the end
                    m_objectPropertiesToSend.Insert(m_objectPropertiesToSend.Count, entity.UUID, entity);
                }
            }
        }

        public void RemoveAvatarFromView (IScenePresence sp)
        {
            lastPresencesInView.Remove (sp);
            lastPresencesDInView.Remove (sp.UUID);
        }

        #endregion

        #region Object Culling by draw distance

        /// <summary>
        /// When the client moves enough to trigger this, make sure that we have sent
        ///  the client all of the objects that have just entered their FOV in their draw distance.
        /// </summary>
        /// <param name="remote_client"></param>
        private void SignificantClientMovement ()
        {
            if (m_culler == null)
                return;

            if (!m_culler.UseCulling)
                return;

            if (!m_forceCullCheck && m_presence.DrawDistance > m_presence.Scene.RegionInfo.RegionSizeX &&
                    m_presence.DrawDistance > m_presence.Scene.RegionInfo.RegionSizeY)
            {
                m_forceCullCheck = false; //Make sure to reset it
                return;
            }

            if (m_presence.DrawDistance == 0)
                return;

            if (m_presence.DrawDistance < 32)
            {
                //If the draw distance is small, the client has gotten messed up or something and we can't do this...
                m_presence.DrawDistance = 32; //Force give them a draw distance
            }

            if (!m_presence.IsChildAgent || m_presence.Scene.RegionInfo.SeeIntoThisSimFromNeighbor)
            {
                Vector3 pos = m_presence.CameraPosition;
                float distsq = Vector3.DistanceSquared (pos, m_lastUpdatePos);
                distsq += 0.2f * m_presence.Velocity.LengthSquared ();
                if (distsq < MINVIEWDSTEPSQ && !m_forceCullCheck) //They havn't moved enough to trigger another update, so just quit
                    return;
                m_forceCullCheck = false;
                Util.FireAndForget (DoSignificantClientMovement);
            }
        }

        private void DoSignificantClientMovement (object o)
        {
            //Just return all the entities, its quicker to do the culling check rather than the position check
            ISceneEntity[] entities = m_presence.Scene.Entities.GetEntities ();
            PriorityQueue<EntityUpdate, double> m_entsqueue = new PriorityQueue<EntityUpdate, double> (entities.Length, DoubleComparer);

            // build a prioritized list of things we need to send

            HashSet<ISceneEntity> NewGrpsInView = new HashSet<ISceneEntity> ();

            foreach (ISceneEntity e in entities)
            {
                if (e != null)
                {
                    if (e.IsDeleted)
                        continue;

                    if (lastGrpsInView.Contains (e)) //If we've already sent it, don't send it again
                        continue;

                    if (m_culler != null)
                    {
                        if (!m_culler.ShowEntityToClient (m_presence, e, m_scene))
                            continue;
                        NewGrpsInView.Add (e);
                    }

                    //Send the root object first!
                    EntityUpdate rootupdate = new EntityUpdate (e.RootChild, PrimUpdateFlags.ForcedFullUpdate);
                    PriorityQueueItem<EntityUpdate, double> rootitem = new PriorityQueueItem<EntityUpdate, double> ();
                    rootitem.Value = rootupdate;
                    rootitem.Priority = m_prioritizer.GetUpdatePriority (m_presence, e.RootChild) - 10;
                    m_entsqueue.Enqueue (rootitem);

                    foreach (ISceneChildEntity child in e.ChildrenEntities ())
                    {
                        if (child == e.RootChild)
                            continue; //Already sent
                        EntityUpdate update = new EntityUpdate (child, PrimUpdateFlags.FullUpdate);
                        PriorityQueueItem<EntityUpdate, double> item = new PriorityQueueItem<EntityUpdate, double> ();
                        item.Value = update;
                        item.Priority = m_prioritizer.GetUpdatePriority (m_presence, child);
                        if (item.Priority >= rootitem.Priority + 10)
                            item.Priority = rootitem.Priority - 10;//Don't let it get sent first!
                        m_entsqueue.Enqueue (item);
                    }
                }
            }
            entities = null;
            lastGrpsInView.UnionWith (NewGrpsInView);
            NewGrpsInView.Clear ();

            // send them 
            SendQueued (m_entsqueue);

            HashSet<IScenePresence> NewPresencesInView = new HashSet<IScenePresence>();

            //Check for scenepresences as well
            List<IScenePresence> presences = m_presence.Scene.Entities.GetPresences ();
            foreach (IScenePresence presence in presences)
            {
                if (presence != null && presence.UUID != m_presence.UUID)
                {
                    //Check for culling here!
                    if (!m_culler.ShowEntityToClient (m_presence, presence, m_scene))
                        continue; // if 2 far ignore

                    NewPresencesInView.Add (presence);

                    if (lastPresencesDInView.ContainsKey (presence.UUID))
                        continue; //Don't resend the update

                    lastPresencesDInView.Add (presence.UUID, presence);
                    SendFullUpdateForPresence (presence);
                }
            }
            presences = null;
            lastPresencesInView.UnionWith(NewPresencesInView);
            NewPresencesInView.Clear();
        }

        public void SendPresenceFullUpdate (IScenePresence presence)
        {
            if (m_culler != null && !m_culler.ShowEntityToClient (m_presence, presence, m_scene))
                m_presence.ControllingClient.SendAvatarDataImmediate (presence);
            if(!lastPresencesDInView.ContainsKey(presence.UUID))
            {
                lastPresencesInView.Add(presence);
                lastPresencesDInView.Add(presence.UUID, presence);
            }
        }

        protected void SendFullUpdateForPresence (IScenePresence presence)
        {
            Util.FireAndForget (delegate (object o)
            {
                m_presence.ControllingClient.SendAvatarDataImmediate (presence);
                //Send the animations too
                presence.Animator.SendAnimPackToClient (m_presence.ControllingClient);
                //Send the presence of this agent to us
                IAvatarAppearanceModule module = presence.RequestModuleInterface<IAvatarAppearanceModule> ();
                if (module != null)
                    module.SendAppearanceToAgent (m_presence);
                //We need to send all attachments of this avatar as well
                IAttachmentsModule attmodule = m_presence.Scene.RequestModuleInterface<IAttachmentsModule> ();
                if (attmodule != null)
                {
                    ISceneEntity[] entities = attmodule.GetAttachmentsForAvatar (m_presence.UUID);
                    foreach (ISceneEntity entity in entities)
                    {
                        QueuePartForUpdate (entity.RootChild, PrimUpdateFlags.ForcedFullUpdate);
                        foreach (ISceneChildEntity child in entity.ChildrenEntities ())
                        {
                            if(!child.IsRoot)
                                QueuePartForUpdate (child, PrimUpdateFlags.ForcedFullUpdate);
                        }
                    }
                }
            });
        }

        #endregion

        #region SendPrimUpdates

        /// <summary>
        /// This method is called by the LLUDPServer and should never be called by anyone else
        /// It loops through the available updates and sends them out (no waiting)
        /// </summary>
        /// <param name="numUpdates">The number of updates to send</param>
        public void SendPrimUpdates (int numPrimUpdates, int numAvaUpdates)
        {
            if (m_numberOfLoops < NUMBER_OF_LOOPS_TO_WAIT) //Wait for the client to finish connecting fully before sending out bunches of updates
            {
                m_numberOfLoops++;
                return;
            }

            if (m_inUse || m_presence.IsInTransit)
                return;

            m_inUse = true;
            //This is for stats
            int AgentMS = Util.EnvironmentTickCount ();

            #region New client entering the Scene, requires all objects in the Scene

            ///If we havn't started processing this client yet, we need to send them ALL the prims that we have in this Scene (and deal with culling as well...)
            if (!m_SentInitialObjects && m_presence.DrawDistance != 0.0f)
            {
                //If they are not in this region, we check to make sure that we allow seeing into neighbors
                if (!m_presence.IsChildAgent || (m_presence.Scene.RegionInfo.SeeIntoThisSimFromNeighbor) && m_prioritizer != null)
                {
                    try
                    {
                        m_SentInitialObjects = true;
                        ISceneEntity[] allEntities = m_presence.Scene.Entities.GetEntities ();
                        PriorityQueue<EntityUpdate, double> m_entsqueue = new PriorityQueue<EntityUpdate, double> (allEntities.Length, DoubleComparer);
                        List<ISceneEntity> NewGrpsInView = new List<ISceneEntity> ();
                        // build a prioritized list of things we need to send

                        foreach (ISceneEntity e in allEntities)
                        {
                            if (e != null && e is SceneObjectGroup)
                            {
                                if (e.IsDeleted)
                                    continue;

                                if (lastGrpsInView.Contains (e))
                                    continue;

                                //Check for culling here!
                                if (m_culler != null)
                                {
                                    if (!m_culler.ShowEntityToClient (m_presence, e, m_scene))
                                        continue;
                                    NewGrpsInView.Add (e);
                                }

                                //Send the root object first!
                                EntityUpdate rootupdate = new EntityUpdate (e.RootChild, PrimUpdateFlags.FullUpdate);
                                PriorityQueueItem<EntityUpdate, double> rootitem = new PriorityQueueItem<EntityUpdate, double> ();
                                rootitem.Value = rootupdate;
                                rootitem.Priority = m_prioritizer.GetUpdatePriority (m_presence, e.RootChild) - 10;
                                m_entsqueue.Enqueue (rootitem);

                                foreach (ISceneChildEntity child in e.ChildrenEntities ())
                                {
                                    if (child == e.RootChild)
                                        continue; //Already sent
                                    EntityUpdate update = new EntityUpdate (child, PrimUpdateFlags.ForcedFullUpdate);
                                    PriorityQueueItem<EntityUpdate, double> item = new PriorityQueueItem<EntityUpdate, double> ();
                                    item.Value = update;
                                    item.Priority = m_prioritizer.GetUpdatePriority (m_presence, child);
                                    if (item.Priority >= rootitem.Priority + 10)
                                        item.Priority = rootitem.Priority - 10;//Don't let it get sent first!
                                    m_entsqueue.Enqueue (item);
                                }
                            }
                        }
                        //Merge the last seen lists
                        lastGrpsInView.UnionWith (NewGrpsInView);
                        NewGrpsInView.Clear ();
                        allEntities = null;
                        // send them 
                        SendQueued (m_entsqueue);
                    }
                    catch(Exception ex)
                    {
                        m_log.Warn ("[SceneViewer]: Exception occured in sending initial prims, " + ex.ToString ());
                        //An exception occured, don't fail to send all the prims to the client
                        m_SentInitialObjects = false;
                    }
                }
            }

            int presenceNumToSend = numAvaUpdates;
            List<EntityUpdate> updates = new List<EntityUpdate> ();
            lock (m_presenceUpdatesToSendLock)
            {
                //Send the numUpdates of them if that many
                // if we don't have that many, we send as many as possible, then switch to objects
                if (m_presenceUpdatesToSend.Count != 0)
                {
                    try
                    {
#if UseDictionaryForEntityUpdates
                        Dictionary<UUID, EntityUpdate>.Enumerator e = m_presenceUpdatesToSend.GetEnumerator ();
                        e.MoveNext ();
                        List<UUID> entitiesToRemove = new List<UUID> ();
#endif
                        int count = m_presenceUpdatesToSend.Count > presenceNumToSend ? presenceNumToSend : m_presenceUpdatesToSend.Count;
                        for (int i = 0; i < count; i++)
                        {
#if UseRemovingEntityUpdates
                            EntityUpdate update = ((EntityUpdate)m_presenceUpdatesToSend[0]);
                            /*if (m_EntitiesInPacketQueue.Contains (update.Entity.UUID))
                            {
                                m_presenceUpdatesToSend.RemoveAt (0);
                                m_presenceUpdatesToSend.Insert (m_presenceUpdatesToSend.Count, update.Entity.UUID, update);
                                continue;
                            }
                            m_EntitiesInPacketQueue.Add (update.Entity.UUID);*/
                            m_presenceUpdatesToSend.RemoveAt (0);
                            if (update.Flags == PrimUpdateFlags.ForcedFullUpdate)
                                SendFullUpdateForPresence ((IScenePresence)update.Entity);
                            else
                                updates.Add (update);
#elif UseDictionaryForEntityUpdates
                            EntityUpdate update = e.Current.Value;
                            entitiesToRemove.Add (update.Entity.UUID);//Remove it later
                            if (update.Flags == PrimUpdateFlags.ForcedFullUpdate)
                                SendFullUpdateForPresence ((IScenePresence)update.Entity);
                            else
                                updates.Add (update);
                            e.MoveNext ();
#else
                            EntityUpdate update = m_presenceUpdatesToSend.Dequeue ();
                            if (update.Flags == PrimUpdateFlags.ForcedFullUpdate)
                                SendFullUpdateForPresence ((IScenePresence)update.Entity);
                            else
                                updates.Add (update);
#endif
                        }
#if UseDictionaryForEntityUpdates
                        foreach (UUID id in entitiesToRemove)
                        {
                            m_presenceUpdatesToSend.Remove (id);
                        }
#endif
                    }
                    catch (Exception ex)
                    {
                        m_log.WarnFormat ("[SceneViewer]: Exception while running presence loop: {0}", ex.ToString ());
                    }
                }
            }
            if (updates.Count != 0)
            {
                presenceNumToSend -= updates.Count;
                m_presence.ControllingClient.SendAvatarUpdate (updates);
            }
            updates.Clear ();

            List<AnimationGroup> animationsToSend = new List<AnimationGroup> ();
            lock (m_presenceAnimationsToSendLock)
            {
                //Send the numUpdates of them if that many
                // if we don't have that many, we send as many as possible, then switch to objects
                if (m_presenceAnimationsToSend.Count != 0 && presenceNumToSend > 0)
                {
                    try
                    {
                        int count = m_presenceAnimationsToSend.Count > presenceNumToSend ? presenceNumToSend : m_presenceAnimationsToSend.Count;
                        for (int i = 0; i < count; i++)
                        {
                            AnimationGroup update = m_presenceAnimationsToSend.Dequeue();
                            /*if (m_AnimationsInPacketQueue.Contains (update.AvatarID))
                            {
                                m_presenceAnimationsToSend.RemoveAt (0);
                                m_presenceAnimationsToSend.Insert (m_presenceAnimationsToSend.Count, update.AvatarID, update);
                                continue;
                            }
                            m_AnimationsInPacketQueue.Add (update.AvatarID);*/
                            animationsToSend.Add (update);
                        }
                    }
                    catch (Exception ex)
                    {
                        m_log.WarnFormat ("[SceneViewer]: Exception while running presence loop: {0}", ex.ToString ());
                    }
                }
            }
            foreach (AnimationGroup update in animationsToSend)
            {
                m_presence.ControllingClient.SendAnimations (update);
            }
            animationsToSend.Clear ();

            int primsNumToSend = numPrimUpdates;

            List<IEntity> entities = new List<IEntity> ();
            lock (m_objectPropertiesToSendLock)
            {
                //Send the numUpdates of them if that many
                // if we don't have that many, we send as many as possible, then switch to objects
                if (m_objectPropertiesToSend.Count != 0)
                {
                    try
                    {
                        int count = m_objectPropertiesToSend.Count > primsNumToSend ? primsNumToSend : m_objectPropertiesToSend.Count;
                        for (int i = 0; i < count; i++)
                        {
                            ISceneChildEntity entity = ((ISceneChildEntity)m_objectPropertiesToSend[0]);
                            /*if (m_PropertiesInPacketQueue.Contains (entity.UUID))
                            {
                                m_objectPropertiesToSend.RemoveAt (0);
                                m_objectPropertiesToSend.Insert (m_objectPropertiesToSend.Count, entity.UUID, entity);
                                continue;
                            }
                            m_PropertiesInPacketQueue.Add (entity.UUID);*/
                            m_objectPropertiesToSend.RemoveAt (0);
                            entities.Add (entity);
                        }
                    }
                    catch (Exception ex)
                    {
                        m_log.WarnFormat ("[SceneViewer]: Exception while running presence loop: {0}", ex.ToString ());
                    }
                }
            }
            if (entities.Count > 0)
            {
                primsNumToSend -= entities.Count;
                m_presence.ControllingClient.SendObjectPropertiesReply (entities);
            }

            updates = new List<EntityUpdate> ();
            lock (m_objectUpdatesToSendLock)
            {
                if (m_objectUpdatesToSend.Count != 0)
                {
                    try
                    {
                        int count = m_objectUpdatesToSend.Count > primsNumToSend ? primsNumToSend : m_objectUpdatesToSend.Count;
                        for (int i = 0; i < count; i++)
                        {
                            EntityUpdate update = ((EntityUpdate)m_objectUpdatesToSend[0]);
                            /*if (m_EntitiesInPacketQueue.Contains (update.Entity.UUID))
                            {
                                m_objectUpdatesToSend.RemoveAt (0);
                                m_objectUpdatesToSend.Insert (m_objectUpdatesToSend.Count, update.Entity.UUID, update);
                                continue;
                            }
                            m_EntitiesInPacketQueue.Add (update.Entity.UUID);*/

                            //Fix the CRC for this update
                            //Increment the CRC code so that the client won't be sent a cached update for this
                            if (update.Flags != PrimUpdateFlags.PrimFlags)
                                ((ISceneChildEntity)update.Entity).CRC++;

                            updates.Add (update);
                            m_objectUpdatesToSend.RemoveAt (0);
                        }
                    }
                    catch (Exception ex)
                    {
                        m_log.WarnFormat ("[SceneViewer]: Exception while running object loop: {0}", ex.ToString ());
                    }
                }
            }
            m_presence.ControllingClient.SendPrimUpdate (updates);

            //Add the time to the stats tracker
            IAgentUpdateMonitor reporter = (IAgentUpdateMonitor)m_presence.Scene.RequestModuleInterface<IMonitorModule> ().GetMonitor (m_presence.Scene.RegionInfo.RegionID.ToString (), MonitorModuleHelper.AgentUpdateCount);
            if (reporter != null)
                reporter.AddAgentTime (Util.EnvironmentTickCountSubtract (AgentMS));

            m_inUse = false;
        }

        /// <summary>
        /// Once the packet has been sent, allow newer updates to be sent for the given entity
        /// </summary>
        /// <param name="ID"></param>
        public void FinishedEntityPacketSend(IEnumerable<EntityUpdate> updates)
        {
            /*foreach (EntityUpdate update in updates)
            {
                m_EntitiesInPacketQueue.Remove(update.Entity.UUID);
            }*/
        }

        /// <summary>
        /// Once the packet has been sent, allow newer updates to be sent for the given entity
        /// </summary>
        /// <param name="ID"></param>
        public void FinishedPropertyPacketSend(IEnumerable<IEntity> updates)
        {
            /*foreach (IEntity update in updates)
            {
                m_PropertiesInPacketQueue.Remove(update.UUID);
            }*/
        }

        /// <summary>
        /// Once the packet has been sent, allow newer animations to be sent for the given entity
        /// </summary>
        /// <param name="ID"></param>
        public void FinishedAnimationPacketSend(AnimationGroup update)
        {
            //m_AnimationsInPacketQueue.Remove(update.AvatarID);
        }

        private static int DoubleComparer (double x, double y)
        {
            return y.CompareTo (x);
        }

        private void SendQueued (PriorityQueue<EntityUpdate, double> m_entsqueue)
        {
            PriorityQueueItem<EntityUpdate, double> up;
            List<EntityUpdate> updates = new List<EntityUpdate> ();
            //Enqueue them all
            while (m_entsqueue.TryDequeue (out up))
            {
                updates.Add (up.Value);
            }
            //Priorities are backwards, gotta flip them around
            updates.Reverse ();
            foreach (EntityUpdate update in updates)
                QueueEntityUpdate (update);

            m_lastUpdatePos = (m_presence.IsChildAgent) ?
                m_presence.AbsolutePosition :
                m_presence.CameraPosition;
        }

        #endregion

        #endregion

        #region Reset and Close

        /// <summary>
        /// The client has left this region and went into a child region
        /// </summary>
        public void Reset ()
        {
            if(m_culler == null)
                return;
            //Don't reset the prim... the client is just in a child region now, we don't want to resent them all the prims
            //Reset the culler so that it doesn't cache too much
            m_culler.Reset ();
            //Gotta remove this so that if the client comes back, we don't have any issues with sending them another update
            lastPresencesInView.Remove (m_presence);
            lastPresencesDInView.Remove (m_presence.UUID);
        }

        /// <summary>
        /// Reset all lists that have to deal with what updates the viewer has
        /// </summary>
        public void Close ()
        {
            if (m_presence == null)
                return;
            m_SentInitialObjects = false;
            m_prioritizer = null;
            m_culler = null;
            m_inUse = false;
            m_queueing = false;
            m_objectUpdatesToSend.Clear ();
            m_presenceUpdatesToSend.Clear ();
            m_presence.OnSignificantClientMovement -= SignificantClientMovement;
            m_presence.Scene.EventManager.OnMakeChildAgent -= EventManager_OnMakeChildAgent;
            m_scene.EventManager.OnClosingClient -= EventManager_OnClosingClient;
            m_presence.Scene.AuroraEventManager.UnregisterEventHandler ("DrawDistanceChanged", AuroraEventManager_OnGenericEvent);
            m_presence.Scene.AuroraEventManager.UnregisterEventHandler ("SignficantCameraMovement", AuroraEventManager_OnGenericEvent);
            m_presence = null;
        }

        #endregion
    }
}
