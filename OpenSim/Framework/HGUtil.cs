﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;
using OpenMetaverse;

namespace OpenSim.Framework
{
    public class HGUtil
    {
        #region Universal User Identifiers

        public static IRegistryCore Registry = null;

        /// <summary>
        /// </summary>
        /// <param name="value">uuid[;endpoint[;name]]</param>
        /// <param name="uuid"></param>
        /// <param name="url"></param>
        /// <param name="firstname"></param>
        /// <param name="lastname"></param>
        public static bool ParseUniversalUserIdentifier (string value, out UUID uuid, out string url, out string firstname, out string lastname, out string secret)
        {
            uuid = UUID.Zero;
            url = string.Empty;
            firstname = "Unknown";
            lastname = "User";
            secret = string.Empty;

            string[] parts = value.Split (';');
            if (parts.Length >= 1)
                if (!UUID.TryParse (parts[0], out uuid))
                    return false;

            if (parts.Length >= 2)
                url = parts[1];

            if (parts.Length >= 3)
            {
                string[] name = parts[2].Split ();
                if (name.Length == 2)
                {
                    firstname = name[0];
                    lastname = name[1];
                }
            }
            if (parts.Length >= 4)
                secret = parts[3];

            if (Registry != null && url != "")
            {
                IUserFinder uf = Registry.RequestModuleInterface<IUserFinder> ();
                if (uf != null)
                    uf.AddUser (uuid, firstname, lastname, url);//Add to the cache
            }
            return url != "";
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="acircuit"></param>
        /// <returns>uuid[;endpoint[;name]]</returns>
        public static string ProduceUserUniversalIdentifier (AgentCircuitData acircuit)
        {
            if (acircuit.ServiceURLs.ContainsKey ("HomeURI"))
            {
                string agentsURI = acircuit.ServiceURLs["HomeURI"].ToString ();
                if (!agentsURI.EndsWith ("/"))
                    agentsURI += "/";

                // This is ugly, but there's no other way, given that the name is changed
                // in the agent circuit data for foreigners
                if (acircuit.lastname.Contains ("@"))
                {
                    string[] parts = acircuit.firstname.Split (new char[] { '.' });
                    if (parts.Length == 2)
                        return acircuit.AgentID.ToString () + ";" + agentsURI + ";" + parts[0] + " " + parts[1];
                }
                return acircuit.AgentID.ToString () + ";" + agentsURI + ";" + acircuit.firstname + " " + acircuit.lastname;
            }
            else
                return acircuit.AgentID.ToString ();
        }
        #endregion
    }
}
