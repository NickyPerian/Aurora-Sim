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

using Nini.Config;
using log4net;
using System;
using System.Reflection;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Serialization;
using System.Collections.Generic;
using Aurora.Simulation.Base;
using OpenSim.Services.Interfaces;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace OpenSim.Services
{
    public class AvatarServerPostHandler : BaseStreamHandler
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private IAvatarService m_AvatarService;
        protected string m_SessionID;
        protected IRegistryCore m_registry;

        public AvatarServerPostHandler (string url, IAvatarService service, string SessionID, IRegistryCore registry) :
            base("POST", url)
        {
            m_AvatarService = service;
            m_SessionID = SessionID;
            m_registry = registry;
        }

        public override byte[] Handle(string path, Stream requestData,
                OSHttpRequest httpRequest, OSHttpResponse httpResponse)
        {
            StreamReader sr = new StreamReader(requestData);
            string body = sr.ReadToEnd();
            sr.Close();
            body = body.Trim();

            //m_log.DebugFormat("[XXX]: query String: {0}", body);

            try
            {
                Dictionary<string, object> request =
                        WebUtils.ParseQueryString(body);

                if (!request.ContainsKey("METHOD"))
                    return FailureResult();

                string method = request["METHOD"].ToString();

                IGridRegistrationService urlModule =
                            m_registry.RequestModuleInterface<IGridRegistrationService>();
                switch (method)
                {
                    case "getavatar":
                        if (urlModule != null)
                            if (!urlModule.CheckThreatLevel(m_SessionID, method, ThreatLevel.Low))
                                return FailureResult();
                        return GetAvatar(request);
                    case "setavatar":
                        if (urlModule != null)
                            if (!urlModule.CheckThreatLevel(m_SessionID, method, ThreatLevel.High))
                                return FailureResult();
                        return SetAvatar(request);
                    case "resetavatar":
                        if (urlModule != null)
                            if (!urlModule.CheckThreatLevel(m_SessionID, method, ThreatLevel.High))
                                return FailureResult();
                        return ResetAvatar(request);
                    case "setitems":
                        if (urlModule != null)
                            if (!urlModule.CheckThreatLevel(m_SessionID, method, ThreatLevel.Medium))
                                return FailureResult();
                        return SetItems(request);
                    case "removeitems":
                        if (urlModule != null)
                            if (!urlModule.CheckThreatLevel(m_SessionID, method, ThreatLevel.Medium))
                                return FailureResult();
                        return RemoveItems(request);
                    case "cachewearabledata":
                        if (urlModule != null)
                            if (!urlModule.CheckThreatLevel(m_SessionID, method, ThreatLevel.Medium))
                                return FailureResult();
                        return CacheWearableData(request);
                }
                m_log.DebugFormat("[AVATAR HANDLER]: unknown method request: {0}", method);
            }
            catch (Exception e)
            {
                m_log.Debug("[AVATAR HANDLER]: Exception {0}" + e);
            }

            return FailureResult();
        }

        byte[] GetAvatar(Dictionary<string, object> request)
        {
            UUID user = UUID.Zero;

            if (!request.ContainsKey("UserID"))
                return FailureResult();

            if (UUID.TryParse(request["UserID"].ToString(), out user))
            {
                AvatarData avatar = m_AvatarService.GetAvatar(user);
                if (avatar == null)
                    return FailureResult();

                Dictionary<string, object> result = new Dictionary<string, object>();
                if (avatar == null)
                    result["result"] = "null";
                else
                    result["result"] = avatar.ToKeyValuePairs();

                string xmlString = WebUtils.BuildXmlResponse(result);

                UTF8Encoding encoding = new UTF8Encoding();
                return encoding.GetBytes(xmlString);
            }

            return FailureResult();
        }

        byte[] SetAvatar(Dictionary<string, object> request)
        {
            UUID user = UUID.Zero;

            if (!request.ContainsKey("UserID"))
                return FailureResult();

            if (!UUID.TryParse(request["UserID"].ToString(), out user))
                return FailureResult();

            AvatarData avatar = new AvatarData(request);
            if (m_AvatarService.SetAvatar(user, avatar))
                return SuccessResult();

            return FailureResult();
        }

        byte[] ResetAvatar(Dictionary<string, object> request)
        {
            UUID user = UUID.Zero;
            if (!request.ContainsKey("UserID"))
                return FailureResult();

            if (!UUID.TryParse(request["UserID"].ToString(), out user))
                return FailureResult();

            if (m_AvatarService.ResetAvatar(user))
                return SuccessResult();

            return FailureResult();
        }
        
        byte[] SetItems(Dictionary<string, object> request)
        {
            UUID user = UUID.Zero;
            string[] names, values;

            if (!request.ContainsKey("UserID") || !request.ContainsKey("Names") || !request.ContainsKey("Values"))
                return FailureResult();

            if (!UUID.TryParse(request["UserID"].ToString(), out user))
                return FailureResult();

            if (!(request["Names"] is List<string> || request["Values"] is List<string>))
                return FailureResult();

            List<string> _names = (List<string>)request["Names"];
            names = _names.ToArray();
            List<string> _values = (List<string>)request["Values"];
            values = _values.ToArray();
            
            if (m_AvatarService.SetItems(user, names, values))
                return SuccessResult();

            return FailureResult();
        }

        byte[] RemoveItems(Dictionary<string, object> request)
        {
            UUID user = UUID.Zero;
            string[] names;

            if (!request.ContainsKey("UserID") || !request.ContainsKey("Names"))
                return FailureResult();

            if (!UUID.TryParse(request["UserID"].ToString(), out user))
                return FailureResult();

            if (!(request["Names"] is List<string>))
                return FailureResult();

            List<string> _names = (List<string>)request["Names"];
            names = _names.ToArray();

            if (m_AvatarService.RemoveItems(user, names))
                return SuccessResult();

            return FailureResult();
        }

        byte[] CacheWearableData(Dictionary<string, object> request)
        {
            UUID user = UUID.Zero;

            if (!request.ContainsKey("UserID") || !request.ContainsKey("WEARABLES"))
                return FailureResult();

            if (!UUID.TryParse(request["UserID"].ToString(), out user))
                return FailureResult();

            AvatarWearable w = new AvatarWearable();
            OSDArray array = (OSDArray)OSDParser.DeserializeJson(request["WEARABLES"].ToString());
            w.Unpack(array);

            m_AvatarService.CacheWearableData(user, w);
            return SuccessResult();
        }
        
        private byte[] SuccessResult()
        {
            XmlDocument doc = new XmlDocument();

            XmlNode xmlnode = doc.CreateNode(XmlNodeType.XmlDeclaration,
                    "", "");

            doc.AppendChild(xmlnode);

            XmlElement rootElement = doc.CreateElement("", "ServerResponse",
                    "");

            doc.AppendChild(rootElement);

            XmlElement result = doc.CreateElement("", "result", "");
            result.AppendChild(doc.CreateTextNode("Success"));

            rootElement.AppendChild(result);

            return DocToBytes(doc);
        }

        private byte[] FailureResult()
        {
            XmlDocument doc = new XmlDocument();

            XmlNode xmlnode = doc.CreateNode(XmlNodeType.XmlDeclaration,
                    "", "");

            doc.AppendChild(xmlnode);

            XmlElement rootElement = doc.CreateElement("", "ServerResponse",
                    "");

            doc.AppendChild(rootElement);

            XmlElement result = doc.CreateElement("", "result", "");
            result.AppendChild(doc.CreateTextNode("Failure"));

            rootElement.AppendChild(result);

            return DocToBytes(doc);
        }

        private byte[] DocToBytes(XmlDocument doc)
        {
            MemoryStream ms = new MemoryStream();
            XmlTextWriter xw = new XmlTextWriter(ms, null);
            xw.Formatting = Formatting.Indented;
            doc.WriteTo(xw);
            xw.Flush();

            return ms.ToArray();
        }

    }
}
