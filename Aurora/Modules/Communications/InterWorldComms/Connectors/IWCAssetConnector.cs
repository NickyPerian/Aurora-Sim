/*
 * Copyright (c) Contributors, http://aurora-sim.org/
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
using System.Linq;
using System.Text;
using OpenSim.Services.Connectors;
using OpenSim.Services.AssetService;
using OpenSim.Services.Interfaces;
using OpenMetaverse;
using Nini.Config;
using Aurora.Simulation.Base;
using OpenSim.Framework;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;

namespace Aurora.Modules
{
    public class IWCAssetConnector : IAssetService, IService
    {
        protected IAssetService m_localService;
        protected AssetServicesConnector m_remoteService;
        protected IRegistryCore m_registry;

        #region IService Members

        public string Name
        {
            get { return GetType().Name; }
        }

        public IAssetService InnerService
        {
            get
            {
                //If we are getting URls for an IWC connection, we don't want to be calling other things, as they are calling us about only our info
                //If we arn't, its ar region we are serving, so give it everything we know
                if (m_registry.RequestModuleInterface<InterWorldCommunications>().IsGettingUrlsForIWCConnection)
                    return m_localService;
                else
                    return this;
            }
        }

        public void Initialize(IConfigSource config, IRegistryCore registry)
        {
            IConfig handlerConfig = config.Configs["Handlers"];
            if (handlerConfig.GetString("AssetHandler", "") != Name)
                return;
            
            string localAssetHandler = handlerConfig.GetString("LocalAssetHandler", "AssetService");
            List<IAssetService> services = Aurora.Framework.AuroraModuleLoader.PickupModules<IAssetService>();
            foreach(IAssetService s in services)
                if(s.GetType().Name == localAssetHandler)
                    m_localService = s;

            if(m_localService == null)
                m_localService = new AssetService();
            m_localService.Configure(config, registry);
            m_remoteService = new AssetServicesConnector();
            m_remoteService.Initialize(config, registry);
            registry.RegisterModuleInterface<IAssetService>(this);
            m_registry = registry;
        }

        public void Configure (IConfigSource config, IRegistryCore registry)
        {
        }

        public void Start(IConfigSource config, IRegistryCore registry)
        {
            if (m_localService != null)
                m_localService.Start(config, registry);
        }

        public void FinishedStartup()
        {
            if (m_localService != null)
                m_localService.FinishedStartup();
        }

        #endregion

        #region IAssetService Members

        public AssetBase Get(string id)
        {
            AssetBase asset = m_localService.Get(id);
            if (asset == null)
                asset = m_remoteService.Get(id);
            return asset;
        }

        public bool GetExists(string id)
        {
            bool exists = m_localService.GetExists(id);
            if (!exists)
                exists = m_remoteService.GetExists(id);
            return exists;
        }

        public byte[] GetData(string id)
        {
            byte[] asset = m_localService.GetData(id);
            if (asset == null)
                asset = m_remoteService.GetData(id);
            return asset;
        }

        public AssetBase GetCached(string id)
        {
            AssetBase asset = m_localService.GetCached(id);
            if (asset == null)
                asset = m_remoteService.GetCached(id);
            return asset;
        }

        public bool Get(string id, object sender, AssetRetrieved handler)
        {
            bool asset = m_localService.Get(id, sender, handler);
            if (!asset)
                asset = m_remoteService.Get(id, sender, handler);
            return asset;
        }

        public UUID Store(AssetBase asset)
        {
            UUID retVal = m_localService.Store(asset);
            //m_remoteService.Store(asset);
            return retVal;
        }

        public bool UpdateContent(UUID id, byte[] data)
        {
            bool asset = m_localService.UpdateContent(id, data);
            if (!asset)
                asset = m_remoteService.UpdateContent(id, data);
            return asset;
        }

        public bool Delete(UUID id)
        {
            bool asset = m_localService.Delete(id);
            if (!asset)
                asset = m_remoteService.Delete(id);
            return asset;
        }

        #endregion
    }
}
