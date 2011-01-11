﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using log4net;
using Nini.Config;
using Aurora.Simulation.Base;
using OpenSim.Services.Interfaces;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using Aurora.DataManager;
using Aurora.Framework;
using Aurora.Services.DataService;

namespace OpenSim.Server.Handlers
{
    public class EventQueueHandler : IService
    {
        #region IService Members

        public string Name
        {
            get { return GetType().Name; }
        }

        public void Initialize(IConfigSource config, IRegistryCore registry)
        {
        }

        public void PostInitialize(IConfigSource config, IRegistryCore registry)
        {
        }

        public void Start(IConfigSource config, IRegistryCore registry)
        {
        }

        public void PostStart(IConfigSource config, IRegistryCore registry)
        {
            IConfig handlerConfig = config.Configs["Handlers"];
            if (handlerConfig.GetString("EventQueueInHandler", "") != Name)
                return;
            IHttpServer server = registry.RequestModuleInterface<ISimulationBase>().GetHttpServer((uint)handlerConfig.GetInt("EventQueueInHandlerPort"));
            IEventQueueService service = registry.RequestModuleInterface<IEventQueueService>();
            server.AddStreamHandler(new EQMEventPoster(service));
        }

        public void AddNewRegistry(IConfigSource config, IRegistryCore registry)
        {
        }

        #endregion
    }

    public class EQMEventPoster : BaseStreamHandler
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private IEventQueueService m_eventQueueService;

        public EQMEventPoster(IEventQueueService handler) :
            base("POST", "/CAPS/EQMPOSTER")
        {
            m_eventQueueService = handler;
        }

        public override byte[] Handle(string path, Stream requestData,
                OSHttpRequest httpRequest, OSHttpResponse httpResponse)
        {
            StreamReader sr = new StreamReader(requestData);
            string body = sr.ReadToEnd();
            sr.Close();
            body = body.Trim();

            OSDMap request = WebUtils.GetOSDMap(body);
            if (request == null)
                return null;

            return ProcessEnqueueEQMMessage(request);
        }

        private byte[] ProcessEnqueueEQMMessage(OSDMap request)
        {
            UUID agentID = request["AgentID"].AsUUID();
            ulong regionHandle = request["RegionHandle"].AsULong();
            UUID password = request["Password"].AsUUID();
            OSDMap Event = (OSDMap)OSDParser.DeserializeLLSDXml(request["Event"].AsString());

            OSDMap response = new OSDMap();
            if (!m_eventQueueService.AuthenticateRequest(agentID, password, regionHandle))
            {
                m_log.Error("[EventQueueHandler]: Failed to authenticate EventQueueMessage for user " +
                    agentID + " calling with password " + password + " in region " + regionHandle);
                response["success"] = false;
            }
            else
            {
                bool enqueueResult = m_eventQueueService.Enqueue(Event, agentID, regionHandle);
                response["success"] = enqueueResult;
            }

            UTF8Encoding encoding = new UTF8Encoding();
            return encoding.GetBytes(OSDParser.SerializeJsonString(response));
        }
    }
}
