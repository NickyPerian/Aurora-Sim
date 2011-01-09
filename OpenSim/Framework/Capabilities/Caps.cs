/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
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
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Services.Interfaces;
using OpenMetaverse.StructuredData;

namespace OpenSim.Framework.Capabilities
{
    public class Caps
    {
        private static readonly ILog m_log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private CapsHandlers m_capsHandlers;

        private static readonly string m_seedRequestPath = "0000/";
        private IScene m_Scene;
        public ulong RegionHandle
        {
            get { return m_Scene.RegionInfo.RegionHandle; }
        }
        private UUID m_agentID;

        public OSDMap RequestMap = new OSDMap();
        
        public void Initialize(IScene scene, IHttpServer httpServer, UUID agent, string CapsPath)
        {
            m_Scene = scene;

            string Protocol = "http://";
            if (httpServer.UseSSL)
                Protocol = "https://";
            string m_httpBase = Protocol + scene.RegionInfo.ExternalHostName + ":" + httpServer.Port;

            m_agentID = agent;
            m_capsHandlers = new CapsHandlers(httpServer, m_httpBase);
            RegisterHandlers(CapsPath);
        }

        public void RegisterHandlers(string capsObjectPath)
        {
            string capsBase = "/CAPS/" + capsObjectPath + m_seedRequestPath;
            // the root of all evil
            AddStreamHandler("SEED",
                new RestStreamHandler("POST", capsBase, CapsRequest));
        }

        /// <summary>
        /// Register a handler.  This allows modules to register handlers.
        /// </summary>
        /// <param name="capName"></param>
        /// <param name="handler"></param>
        public void AddStreamHandler(string method, IRequestHandler handler)
        {
            m_capsHandlers[method] = handler;
        }

        /// <summary>
        /// Remove all CAPS service handlers.
        ///
        /// </summary>
        /// <param name="httpListener"></param>
        /// <param name="path"></param>
        /// <param name="restMethod"></param>
        public void Close()
        {
            foreach (string capsName in m_capsHandlers.Caps)
            {
                m_capsHandlers.Remove(capsName);
            }
        }

        /// <summary>
        /// Construct a client response detailing all the capabilities this server can provide.
        /// </summary>
        /// <param name="request"></param>
        /// <param name="path"></param>
        /// <param name="param"></param>
        /// <param name="httpRequest">HTTP request header object</param>
        /// <param name="httpResponse">HTTP response header object</param>
        /// <returns></returns>
        private string CapsRequest(string request, string path, string param,
                                  OSHttpRequest httpRequest, OSHttpResponse httpResponse)
        {
            m_log.Debug("[RegionCaps]: Seed Caps Request in region " + m_Scene.RegionInfo.RegionName + " @ " + path);

            if (!m_Scene.CheckClient(m_agentID, httpRequest.RemoteIPEndPoint))
            {
                m_log.Error("[RegionCaps]: Unauthorized CAPS client");
                return string.Empty;
            }
            if (request != "")
            {
                OSD osdRequest = OSDParser.DeserializeLLSDXml(request);
                if (osdRequest is OSDMap)
                    RequestMap = (OSDMap)osdRequest;
            }

            return OSDParser.SerializeLLSDXmlString(m_capsHandlers.CapsDetails);
        }
    }
}
