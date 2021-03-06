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

using System;
using System.Collections.Generic;
using System.Reflection;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Framework.Capabilities;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using Aurora.Framework;
using OpenSim.Services.Interfaces;
using ChatSessionMember = OpenSim.Framework.ChatSessionMember;

namespace OpenSim.Region.OptionalModules.Avatar.XmlRpcGroups
{
    public class GroupsMessagingModule : ISharedRegionModule, IGroupsMessagingModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private List<IScene> m_sceneList = new List<IScene> ();

        private IMessageTransferModule m_msgTransferModule = null;
        private IGroupsModule m_groupsModule = null;

        private IGroupsServicesConnector m_groupData = null;
        
        // Config Options
        private bool m_groupMessagingEnabled = false;
        private bool m_debugEnabled = true;

        #region IRegionModuleBase Members

        public void Initialise(IConfigSource config)
        {
            IConfig groupsConfig = config.Configs["Groups"];

            if (groupsConfig == null)
                // Do not run this module by default.
                return;
            else
            {
                // if groups aren't enabled, we're not needed.
                // if we're not specified as the connector to use, then we're not wanted
                if ((groupsConfig.GetBoolean("Enabled", false) == false)
                     || (groupsConfig.GetString("MessagingModule", "Default") != Name))
                {
                    m_groupMessagingEnabled = false;
                    return;
                }

                m_groupMessagingEnabled = groupsConfig.GetBoolean("MessagingEnabled", true);
                if (!m_groupMessagingEnabled)
                    return;

                //m_log.Info("[GROUPS-MESSAGING]: Initializing GroupsMessagingModule");

                m_debugEnabled = groupsConfig.GetBoolean("DebugEnabled", true);
            }
        }

        public void AddRegion (IScene scene)
        {
            if (!m_groupMessagingEnabled)
                return;

            scene.RegisterModuleInterface<IGroupsMessagingModule>(this);
        }

        public void RegionLoaded (IScene scene)
        {
            if (!m_groupMessagingEnabled)
                return;

            m_groupData = scene.RequestModuleInterface<IGroupsServicesConnector>();
            m_groupsModule = scene.RequestModuleInterface<IGroupsModule>();

            // No groups module, no groups messaging
            if (m_groupData == null)
            {
                m_log.Error("[GROUPS-MESSAGING]: Could not get IGroupsServicesConnector, GroupsMessagingModule is now disabled.");
                Close();
                m_groupMessagingEnabled = false;
                return;
            }

            m_msgTransferModule = scene.RequestModuleInterface<IMessageTransferModule>();

            // No message transfer module, no groups messaging
            if (m_msgTransferModule == null)
            {
                m_log.Error("[GROUPS-MESSAGING]: Could not get MessageTransferModule");
                Close();
                m_groupMessagingEnabled = false;
                return;
            }


            m_sceneList.Add(scene);

            scene.EventManager.OnNewClient += OnNewClient;
            scene.EventManager.OnClosingClient += OnClosingClient;
            scene.EventManager.OnIncomingInstantMessage += OnGridInstantMessage;
            scene.EventManager.OnClientLogin += OnClientLogin;
            scene.EventManager.OnChatSessionRequest += OnChatSessionRequest;
        }

        public void RemoveRegion (IScene scene)
        {
            if (!m_groupMessagingEnabled)
                return;

            if (m_debugEnabled) m_log.DebugFormat("[GROUPS-MESSAGING]: {0} called", System.Reflection.MethodBase.GetCurrentMethod().Name);

            m_sceneList.Remove(scene);
            scene.EventManager.OnNewClient -= OnNewClient;
            scene.EventManager.OnClosingClient -= OnClosingClient;
            scene.EventManager.OnIncomingInstantMessage -= OnGridInstantMessage;
            scene.EventManager.OnClientLogin -= OnClientLogin;
            scene.EventManager.OnChatSessionRequest -= OnChatSessionRequest;
        }

        public void Close()
        {
            if (!m_groupMessagingEnabled)
                return;

            if (m_debugEnabled) m_log.Debug("[GROUPS-MESSAGING]: Shutting down GroupsMessagingModule module.");

            foreach (IScene scene in m_sceneList)
            {
                scene.EventManager.OnNewClient -= OnNewClient;
                scene.EventManager.OnIncomingInstantMessage -= OnGridInstantMessage;
            }

            m_sceneList.Clear();

            m_groupData = null;
            m_msgTransferModule = null;
        }

        public Type ReplaceableInterface 
        {
            get { return null; }
        }

        public string Name
        {
            get { return "GroupsMessagingModule"; }
        }

        #endregion

        #region ISharedRegionModule Members

        public void PostInitialise()
        {
            // NoOp
        }

        #endregion

        public void SendMessageToGroup(GridInstantMessage im, UUID groupID)
        {
            if (m_debugEnabled) 
                m_log.DebugFormat("[GROUPS-MESSAGING]: {0} called", System.Reflection.MethodBase.GetCurrentMethod().Name);

            // Copy Message

            GridInstantMessage msg = new GridInstantMessage();
            msg.imSessionID = groupID;
            msg.fromAgentName = im.fromAgentName;
            msg.message = im.message;
            msg.dialog = (byte)InstantMessageDialog.SessionSend;
            msg.offline = 0;
            msg.ParentEstateID = 0;
            msg.Position = Vector3.Zero;
            msg.RegionID = UUID.Zero;
            msg.binaryBucket = im.binaryBucket;
            msg.timestamp = (uint)Util.UnixTimeSinceEpoch();

            msg.fromAgentID = im.fromAgentID;
            msg.fromGroup = true;

            Util.FireAndForget(SendInstantMessages, msg);
        }

        private void SendInstantMessages(object message)
        {
            GridInstantMessage im = message as GridInstantMessage;
            ChatSession session = m_groupData.GetSession(im.imSessionID);
            if(session == null)
                return;
            List<UUID> agentsToSendTo = new List<UUID>();
            foreach(ChatSessionMember member in session.Members)
            {
                if(member.HasBeenAdded)
                    agentsToSendTo.Add(member.AvatarKey);
                else
                {
                    IClientAPI client = GetActiveClient(member.AvatarKey);
                    if(client != null)
                        client.Scene.RequestModuleInterface<IEventQueueService>().ChatterboxInvitation(
                        session.SessionID
                        , session.Name
                        , im.fromAgentID
                        , im.message
                        , member.AvatarKey
                        , im.fromAgentName
                        , im.dialog
                        , im.timestamp
                        , im.offline == 1
                        , (int)im.ParentEstateID
                        , im.Position
                        , 1
                        , im.imSessionID
                        , true
                        , OpenMetaverse.Utils.StringToBytes(session.Name)
                        , client.Scene.RegionInfo.RegionHandle
                        );
                    else
                        agentsToSendTo.Add(member.AvatarKey);//Forward it on, the other sim should take care of it
                }
            }
            m_msgTransferModule.SendInstantMessages(im, agentsToSendTo);
        }
        
        #region SimGridEventHandlers

        void OnClientLogin(IClientAPI client)
        {
            if (m_debugEnabled) m_log.DebugFormat("[GROUPS-MESSAGING]: OnInstantMessage registered for {0}", client.Name);

            
        }

        private void OnNewClient(IClientAPI client)
        {
            if (m_debugEnabled) m_log.DebugFormat("[GROUPS-MESSAGING]: OnInstantMessage registered for {0}", client.Name);

            client.OnInstantMessage += OnInstantMessage;
        }

        private void OnClosingClient(IClientAPI client)
        {
            if (m_debugEnabled) m_log.DebugFormat("[GROUPS-MESSAGING]: OnInstantMessage unregistered for {0}", client.Name);

            client.OnInstantMessage -= OnInstantMessage;
        }

        private void OnGridInstantMessage(GridInstantMessage msg)
        {
            // The instant message module will only deliver messages of dialog types:
            // MessageFromAgent, StartTyping, StopTyping, MessageFromObject
            //
            // Any other message type will not be delivered to a client by the 
            // Instant Message Module


            if (m_debugEnabled)
            {
                m_log.DebugFormat("[GROUPS-MESSAGING]: {0} called", System.Reflection.MethodBase.GetCurrentMethod().Name);

                DebugGridInstantMessage(msg);
            }

            // Incoming message from a group
            if ((msg.fromGroup == true) && 
                ((msg.dialog == (byte)InstantMessageDialog.SessionSend)
                 || (msg.dialog == (byte)InstantMessageDialog.SessionAdd)
                 || (msg.dialog == (byte)InstantMessageDialog.SessionDrop)
                 || (msg.dialog == 212)
                 || (msg.dialog == 213)))
            {
                ProcessMessageFromGroupSession(msg);
            }
        }

        private void ProcessMessageFromGroupSession(GridInstantMessage msg)
        {
            if (m_debugEnabled) m_log.DebugFormat("[GROUPS-MESSAGING]: Session message from {0} going to agent {1}", msg.fromAgentName, msg.toAgentID);

            UUID AgentID = msg.toAgentID;
            UUID GroupID = msg.imSessionID;

            switch (msg.dialog)
            {
                case (byte)InstantMessageDialog.SessionAdd:
                    ChatSession chatSession = m_groupData.GetSession(msg.imSessionID);
                    if(chatSession != null)
                    {
                        chatSession.Members.Add(new ChatSessionMember()
                        {
                            AvatarKey = AgentID,
                            CanVoiceChat = false,
                            HasBeenAdded = true,
                            IsModerator = GetIsModerator(AgentID, GroupID),
                            MuteVoice = false,
                            MuteText = false
                        });
                    }
                    break;

                case (byte)InstantMessageDialog.SessionDrop:
                case (byte)212:
                    DropMemberFromSession(GetActiveClient(AgentID), msg, false);
                    break;

                case (byte)213://Special for muting/unmuting a user
                    IClientAPI client = GetActiveClient(AgentID);
                    IEventQueueService eq = client.Scene.RequestModuleInterface<IEventQueueService>();
                    ChatSessionMember thismember = m_groupData.FindMember(msg.imSessionID, AgentID);
                    if(thismember == null)
                        return;
                    string[] brokenMessage = msg.message.Split(',');
                    bool mutedText = false, mutedVoice = false;
                    bool.TryParse(brokenMessage[0], out mutedText);
                    bool.TryParse(brokenMessage[1], out mutedVoice);
                    thismember.MuteText = mutedText;
                    thismember.MuteVoice = mutedVoice;
                    MuteUser(msg.imSessionID, eq, AgentID, thismember, false);
                    break;

                case (byte)InstantMessageDialog.SessionSend:
                    EnsureGroupChatIsStarted(msg.imSessionID);//Make sure one exists
                    ChatSession session = m_groupData.GetSession(msg.imSessionID);
                    if(session != null)
                    {
                        ChatSessionMember member = m_groupData.FindMember(msg.imSessionID, AgentID);
                        if(member.AvatarKey == AgentID && !member.MuteText)
                        {
                            IClientAPI msgclient = GetActiveClient(msg.toAgentID);
                            if(msgclient != null)
                            {
                                if(!member.HasBeenAdded)
                                    msgclient.Scene.RequestModuleInterface<IEventQueueService>().ChatterboxInvitation(
                                        session.SessionID
                                        , session.Name
                                        , msg.fromAgentID
                                        , msg.message
                                        , member.AvatarKey
                                        , msg.fromAgentName
                                        , msg.dialog
                                        , msg.timestamp
                                        , msg.offline == 1
                                        , (int)msg.ParentEstateID
                                        , msg.Position
                                        , 1
                                        , msg.imSessionID
                                        , true
                                        , OpenMetaverse.Utils.StringToBytes(session.Name)
                                        , msgclient.Scene.RegionInfo.RegionHandle
                                        );
                                // Deliver locally, directly
                                if(m_debugEnabled)
                                    m_log.DebugFormat("[GROUPS-MESSAGING]: Delivering to {0} locally", msgclient.Name);
                                msgclient.SendInstantMessage(msg);
                            }
                        }
                    }
                    break;

                default:
                    m_log.WarnFormat("[GROUPS-MESSAGING]: I don't know how to proccess a {0} message.", ((InstantMessageDialog)msg.dialog).ToString());
                    break;
            }
        }

        private string OnChatSessionRequest (UUID Agent, OSDMap rm)
        {
            string method = rm["method"].AsString();

            UUID sessionid = UUID.Parse(rm["session-id"].AsString());

            IClientAPI SP = GetActiveClient(Agent);
            IEventQueueService eq = SP.Scene.RequestModuleInterface<IEventQueueService>();

            if(method == "accept invitation")
            {
                //They would like added to the group conversation
                List<OpenMetaverse.Messages.Linden.ChatterBoxSessionAgentListUpdatesMessage.AgentUpdatesBlock> Us = new List<OpenMetaverse.Messages.Linden.ChatterBoxSessionAgentListUpdatesMessage.AgentUpdatesBlock>();
                List<OpenMetaverse.Messages.Linden.ChatterBoxSessionAgentListUpdatesMessage.AgentUpdatesBlock> NotUsAgents = new List<OpenMetaverse.Messages.Linden.ChatterBoxSessionAgentListUpdatesMessage.AgentUpdatesBlock>();

                ChatSession session = m_groupData.GetSession(sessionid);
                if(session != null)
                {
                    ChatSessionMember thismember = m_groupData.FindMember(sessionid, Agent);
                    if(thismember == null)
                        return "";//No user with that session
                    //Tell all the other members about the incoming member
                    thismember.HasBeenAdded = true;
                    foreach(ChatSessionMember sessionMember in session.Members)
                    {
                        OpenMetaverse.Messages.Linden.ChatterBoxSessionAgentListUpdatesMessage.AgentUpdatesBlock block = new OpenMetaverse.Messages.Linden.ChatterBoxSessionAgentListUpdatesMessage.AgentUpdatesBlock();
                        block.AgentID = sessionMember.AvatarKey;
                        block.CanVoiceChat = sessionMember.CanVoiceChat;
                        block.IsModerator = sessionMember.IsModerator;
                        block.MuteText = sessionMember.MuteText;
                        block.MuteVoice = sessionMember.MuteVoice;
                        block.Transition = "ENTER";
                        if(Agent == sessionMember.AvatarKey)
                            Us.Add(block);
                        if(sessionMember.HasBeenAdded) // Don't add not joined yet agents. They don't want to be here.
                            NotUsAgents.Add(block);
                    }
                    foreach(ChatSessionMember member in session.Members)
                    {
                        if(member.AvatarKey == thismember.AvatarKey)
                        {
                            //Tell 'us' about all the other agents in the group
                            eq.ChatterBoxSessionAgentListUpdates(session.SessionID, NotUsAgents.ToArray(), member.AvatarKey, "ENTER", SP.Scene.RegionInfo.RegionHandle);
                        }
                        else
                        {
                            //Tell 'other' agents about the new agent ('us')
                            IClientAPI otherAgent = GetActiveClient(member.AvatarKey);
                            if(otherAgent != null)//Local, so we can send it directly
                                eq.ChatterBoxSessionAgentListUpdates(session.SessionID, Us.ToArray(), member.AvatarKey, "ENTER", otherAgent.Scene.RegionInfo.RegionHandle);
                            else
                            {
                                ISyncMessagePosterService amps = m_sceneList[0].RequestModuleInterface<ISyncMessagePosterService>();
                                if(amps != null)
                                {
                                    OSDMap message = new OSDMap();
                                    message["Method"] = "GroupSessionAgentUpdate";
                                    message["AgentID"] = thismember.AvatarKey;
                                    message["Message"] = ChatterBoxSessionAgentListUpdates(session.SessionID, Us.ToArray(), "ENTER");
                                    amps.Post(message, SP.Scene.RegionInfo.RegionHandle);
                                }
                            }
                        }
                    }
                    return "Accepted";
                }
                else
                    return ""; //not this type of session
            }
            else if(method == "mute update")
            {
                //Check if the user is a moderator
                if(!GetIsModerator(Agent, sessionid))
                    return "";

                OSDMap parameters = (OSDMap)rm["params"];
                UUID AgentID = parameters["agent_id"].AsUUID();
                OSDMap muteInfoMap = (OSDMap)parameters["mute_info"];

                ChatSessionMember thismember = m_groupData.FindMember(sessionid, AgentID);
                if(muteInfoMap.ContainsKey("text"))
                    thismember.MuteText = muteInfoMap["text"].AsBoolean();
                if(muteInfoMap.ContainsKey("voice"))
                    thismember.MuteVoice = muteInfoMap["voice"].AsBoolean();
                MuteUser(sessionid, eq, AgentID, thismember, true);

                return "Accepted";
            }
            else
            {
                m_log.Warn("ChatSessionRequest : " + method);
                return "";
            }
        }

        private void MuteUser (UUID sessionid, IEventQueueService eq, UUID AgentID, ChatSessionMember thismember, bool forward)
        {
            OpenMetaverse.Messages.Linden.ChatterBoxSessionAgentListUpdatesMessage.AgentUpdatesBlock block = new OpenMetaverse.Messages.Linden.ChatterBoxSessionAgentListUpdatesMessage.AgentUpdatesBlock();
            block.AgentID = thismember.AvatarKey;
            block.CanVoiceChat = thismember.CanVoiceChat;
            block.IsModerator = thismember.IsModerator;
            block.MuteText = thismember.MuteText;
            block.MuteVoice = thismember.MuteVoice;
            block.Transition = "ENTER";

            // Send an update to the affected user
            IClientAPI affectedUser = GetActiveClient(thismember.AvatarKey);
            if(affectedUser != null)
                eq.ChatterBoxSessionAgentListUpdates(sessionid, new OpenMetaverse.Messages.Linden.ChatterBoxSessionAgentListUpdatesMessage.AgentUpdatesBlock[] { block }, AgentID, "ENTER", affectedUser.Scene.RegionInfo.RegionHandle);
            else if(forward)
                SendMutedUserIM(thismember, sessionid);
        }

        private void SendMutedUserIM (ChatSessionMember member, UUID GroupID)
        {
            GridInstantMessage img = new GridInstantMessage() 
            {
                toAgentID = member.AvatarKey,
                fromGroup = true,
                imSessionID = GroupID,
                dialog = 213,//Special mute one
                message = member.MuteText + "," + member.MuteVoice
            };
            m_msgTransferModule.SendInstantMessage(img);
        }

        public void EnsureGroupChatIsStarted (UUID groupID)
        {
            ChatSession session = m_groupData.GetSession(groupID);
            if(session == null)
            {
                List<ChatSessionMember> members = new List<ChatSessionMember>();
                GroupRecord record = m_groupData.GetGroupRecord(UUID.Zero, groupID, "");
                UUID ownerID = record.FounderID;//Requires that the founder is still in the group
                foreach(GroupMembersData gmd in m_groupData.GetGroupMembers(ownerID, groupID))
                {
                    if((gmd.AgentPowers & (ulong)GroupPowers.JoinChat) == (ulong)GroupPowers.JoinChat)//Only if they can join
                    {
                        members.Add(new OpenSim.Framework.ChatSessionMember()
                        {
                            AvatarKey = gmd.AgentID
                        });
                    }
                }
                m_groupData.CreateSession(new ChatSession()
                {
                    Members = members,
                    Name = record.GroupName,
                    SessionID = groupID
                });
            }
        }

        #endregion


        #region ClientEvents
        private void OnInstantMessage(IClientAPI remoteClient, GridInstantMessage im)
        {
            if (m_debugEnabled)
            {
                m_log.DebugFormat("[GROUPS-MESSAGING]: {0} called", System.Reflection.MethodBase.GetCurrentMethod().Name);

                DebugGridInstantMessage(im);
            }

            // Start group IM session
            if ((im.dialog == (byte)InstantMessageDialog.SessionGroupStart))
            {
                if (m_debugEnabled) m_log.InfoFormat("[GROUPS-MESSAGING]: imSessionID({0}) toAgentID({1})", im.imSessionID, im.toAgentID);

                UUID GroupID = im.imSessionID;
                UUID AgentID = im.fromAgentID;

                GroupRecord groupInfo = m_groupData.GetGroupRecord(AgentID, GroupID, null);
    
                if (groupInfo != null)
                {
                    if(!m_groupsModule.GroupPermissionCheck(AgentID, GroupID, GroupPowers.JoinChat))
                        return;//They have to be able to join to create a group chat
                    //Create the session.
                    IEventQueueService queue = remoteClient.Scene.RequestModuleInterface<IEventQueueService>();
                    if(m_groupData.CreateSession(new ChatSession()
                    {
                        Members = new List<ChatSessionMember>(),
                        SessionID = GroupID,
                        Name = groupInfo.GroupName
                    }))
                    {
                        m_groupData.AddMemberToGroup(new ChatSessionMember()
                        {
                            AvatarKey = AgentID,
                            CanVoiceChat = false,
                            IsModerator = GetIsModerator(AgentID, GroupID),
                            MuteText = false,
                            MuteVoice = false,
                            HasBeenAdded = true
                        }, GroupID);

                        foreach(GroupMembersData gmd in m_groupData.GetGroupMembers(AgentID, GroupID))
                        {
                            if(gmd.AgentID != AgentID)//Add everyone else too
                            {
                                if((gmd.AgentPowers & (ulong)GroupPowers.JoinChat) == (ulong)GroupPowers.JoinChat)//Only if they can join
                                {
                                    m_groupData.AddMemberToGroup(new ChatSessionMember()
                                    {
                                        AvatarKey = gmd.AgentID,
                                        CanVoiceChat = false,
                                        IsModerator = GetIsModerator(gmd.AgentID, GroupID),
                                        MuteText = false,
                                        MuteVoice = false,
                                        HasBeenAdded = false
                                    }, GroupID);
                                }
                            }
                        }
                        //Tell us that it was made successfully
                        ChatterBoxSessionStartReplyViaCaps(remoteClient, groupInfo.GroupName, GroupID);
                    }
                    else
                    {
                        ChatSession thisSession = m_groupData.GetSession(GroupID);
                        //A session already exists
                        //Add us
                        m_groupData.AddMemberToGroup(new ChatSessionMember()
                        {
                            AvatarKey = AgentID,
                            CanVoiceChat = false,
                            IsModerator = GetIsModerator(AgentID, GroupID),
                            MuteText = false,
                            MuteVoice = false,
                            HasBeenAdded = true
                        }, GroupID);

                        //Tell us that we entered successfully
                        ChatterBoxSessionStartReplyViaCaps(remoteClient, groupInfo.GroupName, GroupID);
                        List<OpenMetaverse.Messages.Linden.ChatterBoxSessionAgentListUpdatesMessage.AgentUpdatesBlock> Us = new List<OpenMetaverse.Messages.Linden.ChatterBoxSessionAgentListUpdatesMessage.AgentUpdatesBlock>();
                        List<OpenMetaverse.Messages.Linden.ChatterBoxSessionAgentListUpdatesMessage.AgentUpdatesBlock> NotUsAgents = new List<OpenMetaverse.Messages.Linden.ChatterBoxSessionAgentListUpdatesMessage.AgentUpdatesBlock>();

                        foreach(ChatSessionMember sessionMember in thisSession.Members)
                        {
                            OpenMetaverse.Messages.Linden.ChatterBoxSessionAgentListUpdatesMessage.AgentUpdatesBlock block = new OpenMetaverse.Messages.Linden.ChatterBoxSessionAgentListUpdatesMessage.AgentUpdatesBlock();
                            block.AgentID = sessionMember.AvatarKey;
                            block.CanVoiceChat = sessionMember.CanVoiceChat;
                            block.IsModerator = sessionMember.IsModerator;
                            block.MuteText = sessionMember.MuteText;
                            block.MuteVoice = sessionMember.MuteVoice;
                            block.Transition = "ENTER";
                            if(AgentID == sessionMember.AvatarKey)
                                Us.Add(block);
                            if(sessionMember.HasBeenAdded) // Don't add not joined yet agents. They don't want to be here.
                                NotUsAgents.Add(block);
                        }
                        foreach(ChatSessionMember member in thisSession.Members)
                        {
                            if(member.AvatarKey == AgentID)
                            {
                                //Tell 'us' about all the other agents in the group
                                queue.ChatterBoxSessionAgentListUpdates(GroupID, NotUsAgents.ToArray(), member.AvatarKey, "ENTER", remoteClient.Scene.RegionInfo.RegionHandle);
                            }
                            else
                            {
                                //Tell 'other' agents about the new agent ('us')
                                IClientAPI otherAgent = GetActiveClient(member.AvatarKey);
                                if(otherAgent != null)//Local, so we can send it directly
                                    queue.ChatterBoxSessionAgentListUpdates(GroupID, Us.ToArray(), member.AvatarKey, "ENTER", otherAgent.Scene.RegionInfo.RegionHandle);
                                else
                                {
                                    ISyncMessagePosterService amps = m_sceneList[0].RequestModuleInterface<ISyncMessagePosterService>();
                                    if(amps != null)
                                    {
                                        OSDMap message = new OSDMap();
                                        message["Method"] = "GroupSessionAgentUpdate";
                                        message["AgentID"] = AgentID;
                                        message["Message"] = ChatterBoxSessionAgentListUpdates(GroupID, Us.ToArray(), "ENTER");
                                        amps.Post(message, remoteClient.Scene.RegionInfo.RegionHandle);
                                    }
                                }
                            }
                        }
                    }

                    //Tell us that we entered
                    OpenMetaverse.Messages.Linden.ChatterBoxSessionAgentListUpdatesMessage.AgentUpdatesBlock ourblock = new OpenMetaverse.Messages.Linden.ChatterBoxSessionAgentListUpdatesMessage.AgentUpdatesBlock();
                    ourblock.AgentID = AgentID;
                    ourblock.CanVoiceChat = true;
                    ourblock.IsModerator = true;
                    ourblock.MuteText = false;
                    ourblock.MuteVoice = false;
                    ourblock.Transition = "ENTER";
                    queue.ChatterBoxSessionAgentListUpdates(GroupID, new OpenMetaverse.Messages.Linden.ChatterBoxSessionAgentListUpdatesMessage.AgentUpdatesBlock[] { ourblock }, AgentID, "ENTER", remoteClient.Scene.RegionInfo.RegionHandle);
                }
            }
            // Send a message from locally connected client to a group
            else if ((im.dialog == (byte)InstantMessageDialog.SessionSend) && im.message != "")
            {
                UUID GroupID = im.imSessionID;
                UUID AgentID = im.fromAgentID;

                if (m_debugEnabled) 
                    m_log.DebugFormat("[GROUPS-MESSAGING]: Send message to session for group {0} with session ID {1}", GroupID, im.imSessionID.ToString());

                ChatSessionMember memeber = m_groupData.FindMember(im.imSessionID, AgentID);
                if(memeber == null || memeber.MuteText)
                    return;//Not in the chat or muted
                SendMessageToGroup(im, GroupID);
            }
            else if(im.dialog == (byte)InstantMessageDialog.SessionDrop)
                DropMemberFromSession(remoteClient, im, true);
            else if(im.dialog == (byte)212)//Forwarded sessionDrop
                DropMemberFromSession(remoteClient, im, false);
        }

        private bool GetIsModerator (UUID AgentID, UUID GroupID)
        {
            return m_groupsModule.GroupPermissionCheck(AgentID, GroupID, GroupPowers.ModerateChat);
        }

        private OSD ChatterBoxSessionAgentListUpdates (UUID sessionID, OpenMetaverse.Messages.Linden.ChatterBoxSessionAgentListUpdatesMessage.AgentUpdatesBlock[] agentUpdatesBlock, string Transition)
        {
            OSDMap body = new OSDMap();
            OSDMap agentUpdates = new OSDMap();
            OSDMap infoDetail = new OSDMap();
            OSDMap mutes = new OSDMap();

            foreach(OpenMetaverse.Messages.Linden.ChatterBoxSessionAgentListUpdatesMessage.AgentUpdatesBlock block in agentUpdatesBlock)
            {
                infoDetail = new OSDMap();
                mutes = new OSDMap();
                mutes.Add("text", OSD.FromBoolean(block.MuteText));
                mutes.Add("voice", OSD.FromBoolean(block.MuteVoice));
                infoDetail.Add("can_voice_chat", OSD.FromBoolean(block.CanVoiceChat));
                infoDetail.Add("is_moderator", OSD.FromBoolean(block.IsModerator));
                infoDetail.Add("mutes", mutes);
                OSDMap info = new OSDMap();
                info.Add("info", infoDetail);
                if(Transition != string.Empty)
                    info.Add("transition", OSD.FromString(Transition));
                agentUpdates.Add(block.AgentID.ToString(), info);
            }
            body.Add("agent_updates", agentUpdates);
            body.Add("session_id", OSD.FromUUID(sessionID));
            body.Add("updates", new OSD());

            OSDMap chatterBoxSessionAgentListUpdates = new OSDMap();
            chatterBoxSessionAgentListUpdates.Add("message", OSD.FromString("ChatterBoxSessionAgentListUpdates"));
            chatterBoxSessionAgentListUpdates.Add("body", body);

            return chatterBoxSessionAgentListUpdates;
        }

        /// <summary>
        /// Remove the member from this session
        /// </summary>
        /// <param name="client"></param>
        /// <param name="im"></param>
        public void DropMemberFromSession (IClientAPI client, GridInstantMessage im, bool forwardOn)
        {
            ChatSession session = m_groupData.GetSession(im.imSessionID);
            if(session == null)
                return;
            ChatSessionMember member = new ChatSessionMember() { AvatarKey = UUID.Zero };
            foreach(ChatSessionMember testmember in session.Members)
            {
                if(testmember.AvatarKey == im.fromAgentID)
                    member = testmember;
            }

            if(member.AvatarKey != UUID.Zero)
            {
                member.HasBeenAdded = false;
            }

            if(GetMemeberCount(session) == 0)
            {
                m_groupData.RemoveSession(session.SessionID);//Noone is left!
                return;
            }

            OpenMetaverse.Messages.Linden.ChatterBoxSessionAgentListUpdatesMessage.AgentUpdatesBlock block = new OpenMetaverse.Messages.Linden.ChatterBoxSessionAgentListUpdatesMessage.AgentUpdatesBlock();
            block.AgentID = member.AvatarKey;
            block.CanVoiceChat = member.CanVoiceChat;
            block.IsModerator = member.IsModerator;
            block.MuteText = member.MuteText;
            block.MuteVoice = member.MuteVoice;
            block.Transition = "LEAVE";
            List<UUID> usersToForwardTo = new List<UUID>();
            IEventQueueService eq = client.Scene.RequestModuleInterface<IEventQueueService>();
            foreach(ChatSessionMember sessionMember in session.Members)
            {
                IClientAPI user = GetActiveClient(sessionMember.AvatarKey);
                if(user != null)
                    eq.ChatterBoxSessionAgentListUpdates(session.SessionID, new OpenMetaverse.Messages.Linden.ChatterBoxSessionAgentListUpdatesMessage.AgentUpdatesBlock[] { block }, sessionMember.AvatarKey, "LEAVE", user.Scene.RegionInfo.RegionHandle);
                else
                    usersToForwardTo.Add(sessionMember.AvatarKey);
            }
            if(forwardOn)
            {
                im.dialog = 212;//Don't keep forwarding on other sims
                m_msgTransferModule.SendInstantMessages(im, usersToForwardTo);
            }
        }

        private int GetMemeberCount (ChatSession session)
        {
            int count = 0;
            foreach(ChatSessionMember member in session.Members)
            {
                if(member.HasBeenAdded)
                    count++;
            }
            return count;
        }

        #endregion

        void ChatterBoxSessionStartReplyViaCaps(IClientAPI remoteClient, string groupName, UUID groupID)
        {
            if (m_debugEnabled) m_log.DebugFormat("[GROUPS-MESSAGING]: {0} called", System.Reflection.MethodBase.GetCurrentMethod().Name);
            IEventQueueService queue = remoteClient.Scene.RequestModuleInterface<IEventQueueService>();

            if (queue != null)
            {
                queue.ChatterBoxSessionStartReply(groupName, groupID,
                    remoteClient.AgentId, remoteClient.Scene.RegionInfo.RegionHandle);
            }
        }

        private void DebugGridInstantMessage(GridInstantMessage im)
        {
            // Don't log any normal IMs (privacy!)
            if (m_debugEnabled && im.dialog != (byte)InstantMessageDialog.MessageFromAgent)
            {
                m_log.WarnFormat("[GROUPS-MESSAGING]: IM: fromGroup({0})", im.fromGroup ? "True" : "False");
                m_log.WarnFormat("[GROUPS-MESSAGING]: IM: Dialog({0})", ((InstantMessageDialog)im.dialog).ToString());
                m_log.WarnFormat("[GROUPS-MESSAGING]: IM: fromAgentID({0})", im.fromAgentID.ToString());
                m_log.WarnFormat("[GROUPS-MESSAGING]: IM: fromAgentName({0})", im.fromAgentName.ToString());
                m_log.WarnFormat("[GROUPS-MESSAGING]: IM: imSessionID({0})", im.imSessionID.ToString());
                m_log.WarnFormat("[GROUPS-MESSAGING]: IM: message({0})", im.message.ToString());
                m_log.WarnFormat("[GROUPS-MESSAGING]: IM: offline({0})", im.offline.ToString());
                m_log.WarnFormat("[GROUPS-MESSAGING]: IM: toAgentID({0})", im.toAgentID.ToString());
                m_log.WarnFormat("[GROUPS-MESSAGING]: IM: binaryBucket({0})", OpenMetaverse.Utils.BytesToHexString(im.binaryBucket, "BinaryBucket"));
            }
        }

        #region Client Tools

        /// <summary>
        /// Try to find an active IClientAPI reference for agentID giving preference to root connections
        /// </summary>
        private IClientAPI GetActiveClient(UUID agentID)
        {
            if (m_debugEnabled) m_log.WarnFormat("[GROUPS-MESSAGING]: Looking for local client {0}", agentID);

            IClientAPI child = null;

            // Try root avatar first
            foreach (IScene scene in m_sceneList)
            {
                IScenePresence user;
                if (scene.TryGetScenePresence (agentID, out user))
                {
                    if (!user.IsChildAgent)
                    {
                        if (m_debugEnabled) m_log.WarnFormat("[GROUPS-MESSAGING]: Found root agent for client : {0}", user.ControllingClient.Name);
                        return user.ControllingClient;
                    }
                    else
                    {
                        if (m_debugEnabled) m_log.WarnFormat("[GROUPS-MESSAGING]: Found child agent for client : {0}", user.ControllingClient.Name);
                        child = user.ControllingClient;
                    }
                }
            }

            // If we didn't find a root, then just return whichever child we found, or null if none
            if (child == null)
            {
                if (m_debugEnabled) m_log.WarnFormat("[GROUPS-MESSAGING]: Could not find local client for agent : {0}", agentID);
            }
            else
            {
                if (m_debugEnabled) m_log.WarnFormat("[GROUPS-MESSAGING]: Returning child agent for client : {0}", child.Name);
            }
            return child;
        }

        #endregion
    }
}
