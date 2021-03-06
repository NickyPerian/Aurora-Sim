﻿/*
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
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text.RegularExpressions;
using System.Threading;
using Aurora.Framework;
using log4net;
using OpenMetaverse;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;
using System.Security.Cryptography;

namespace Aurora.Services.DataService.Connectors.Database.Asset
{
    public class LocalAssetBlackholeConnector : IAssetDataPlugin
    {
        #region Variables

        private delegate void Blank ();
        private static readonly SHA256Managed SHA256HashGenerator = new SHA256Managed();
        private static readonly ILog m_Log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private IGenericData m_Gd;
        private bool m_Enabled;
        private bool needsConversion;
        private readonly List<char> m_InvalidChars = new List<char>();
        private string m_CacheDirectory = "./BlackHoleAssets";
        private string m_CacheDirectoryBackup = "./BlackHoleBackup";
        private const int m_CacheDirectoryTiers = 3;
        private const int m_CacheDirectoryTierLen = 1;
        private readonly System.Timers.Timer taskTimer = new System.Timers.Timer();
        private int NumberOfDaysForOldAssets = -30;
        private readonly List<Blank> m_genericTasks = new List<Blank>();
        private bool m_pointInventory2ParentAssets = true;

        // for debugging
        private const bool disableTimer = false;

        private int convertCount;
        private int convertCountDupe;
        private int convertCountParentFix;
        private int displayCount;
        readonly Stopwatch sw = new Stopwatch();

        #endregion

        #region Implementation of IAuroraDataPlugin

        public string Name
        {
            get { return "IAssetDataPlugin"; }
        }

        public void Initialize(IGenericData genericData, IConfigSource source, IRegistryCore simBase, string defaultConnectionString)
        {
            if (source.Configs["AuroraConnectors"].GetString("AssetConnector", "LocalConnector") != "LocalConnectorBlackHole")
                return;
            m_Gd = genericData;
            m_Enabled = true;

            if (source.Configs["Handlers"].GetString("AssetHandler", "") != "AssetService")
                return;

            m_CacheDirectory = source.Configs["BlackHole"].GetString("CacheDirector", m_CacheDirectory);
            m_CacheDirectoryBackup = source.Configs["BlackHole"].GetString("BackupCacheDirector", m_CacheDirectoryBackup);
            NumberOfDaysForOldAssets = source.Configs["BlackHole"].GetInt("AssetsAreOldAfterHowManyDays", 30) * -1;
            m_Enabled = true;

            m_pointInventory2ParentAssets = source.Configs["BlackHole"].GetBoolean("PointInventoryToParentAssets", true);


            if (!Directory.Exists(m_CacheDirectoryBackup))
                Directory.CreateDirectory(m_CacheDirectoryBackup);
            if (!Directory.Exists(m_CacheDirectoryBackup))
            {
                m_Log.Error(
                    "Check your Main.ini and ensure your backup directory is set! under [BlackHole] BackupCacheDirector");
                m_Enabled = false;
                return;
            }

            if (!Directory.Exists(m_CacheDirectory))
                Directory.CreateDirectory(m_CacheDirectory);
            if (!Directory.Exists(m_CacheDirectory))
            {
                m_Log.Error(
                    "Check your Main.ini and ensure your cache directory is set! under [BlackHole] m_CacheDirectory");
                m_Enabled = false;
                return;
            }

            if (source.Configs[Name] != null)
                defaultConnectionString = source.Configs[Name].GetString("ConnectionString", defaultConnectionString);
            genericData.ConnectToDatabase(defaultConnectionString, "BlackholeAsset", source.Configs["AuroraConnectors"].GetBoolean("ValidateTables", true));

            m_InvalidChars.AddRange(Path.GetInvalidPathChars());
            m_InvalidChars.AddRange(Path.GetInvalidFileNameChars());

            if (m_Enabled)
            {
                m_Log.Error("[BlackholeAssets]: Blackhole assets enabled");
                DataManager.DataManager.RegisterPlugin(Name, this);
                needsConversion = (m_Gd.Query(" 1 = 1 LIMIT 1 ", "assets", "id").Count >= 1);
                convertCount = 0;
                taskTimer.Interval = 60000;
                taskTimer.Elapsed += t_Elapsed;
                taskTimer.Start();
            }
        }

        #endregion

        #region Implementation of IAssetDataPlugin

        #region GetAsset

        /// <summary>
        /// Get a asset
        /// </summary>
        /// <param name="uuid">UUID of the asset requesting</param>
        /// <returns>AssetBase</returns>
        public AssetBase GetAsset(UUID uuid)
        {
            return GetAsset(uuid, false, true);
        }

        /// <summary>
        /// Get a asset without the actual data. You can always use MetaOnly Property to deterine if its there
        /// </summary>
        /// <param name="uuid">UUID of the asset requesting</param>
        /// <returns>AssetBase without the actual asset data</returns>
        public AssetBase GetMeta(UUID uuid)
        {
            return GetAsset(uuid, true, true);
        }

        private AssetBase GetAsset(UUID uuid, bool metaOnly, bool displayMessages)
        {
            ResetTimer(15000);
            string databaseTable = "auroraassets_" + uuid.ToString().Substring(0, 1);
            IDataReader dr = null;
            AssetBase asset = null;
            try
            {
                // get the asset
                dr = m_Gd.QueryData("WHERE id = '" + uuid + "' LIMIT 1", databaseTable,
                                    "id, hash_code, parent_id, creator_id, name, description, asset_type, create_time, access_time, asset_flags, host_uri");
                asset = LoadAssetFromDR(dr);

                if ((asset == null) && (needsConversion))
                {
                    // check to see if it needs converted
                    asset = Convert2BH(uuid);
                    if (asset != null)
                    {
                        if (metaOnly) asset.Data = new byte[] { };
                        asset.MetaOnly = metaOnly;
                    }
                }

                if (asset == null)
                {
                    // check the old table
                    databaseTable = "auroraassets_old";
                    dr = m_Gd.QueryData("WHERE id = '" + uuid + "' LIMIT 1", databaseTable,
                                    "id, hash_code, parent_id, creator_id, name, description, asset_type, create_time, access_time, asset_flags, host_uri");
                    asset = LoadAssetFromDR(dr);
                    if (asset != null) asset.ID = Store(asset);
                }


                if ((asset == null) && (displayMessages))
                {
                    // oh well.. we tried
                    m_Log.Warn("[LocalAssetBlackholeConnector] GetAsset(" + uuid + "); Unable to find asset " + uuid);
                }
                if (asset == null) return null;

                if (!metaOnly)
                {
                    // load all the data
                    asset.Data = LoadFile(asset.HashCode);
                }
                asset.MetaOnly = metaOnly;
                // save down last time updated
                m_Gd.Update(databaseTable, new object[] { Util.ToUnixTime(DateTime.UtcNow) }, new[] { "access_time" }, new[] { "id" }, new object[] { asset.ID });
            }
            catch (Exception e)
            {
                if (displayMessages)
                    m_Log.Error("[LocalAssetBlackholeConnector] GetAsset(" + uuid + "); Error ", e);
            }
            finally
            {
                if (dr != null) dr.Close();
            }
            return asset;
        }

        private AssetBase LoadAssetFromDR(IDataReader dr)
        {
            try
            {
                if (dr != null)
                {
                    while (dr.Read())
                    {
                        return new AssetBase(UUID.Parse(dr["id"].ToString()), dr["name"].ToString(),
                                              (AssetType)int.Parse(dr["asset_type"].ToString()),
                                              UUID.Parse(dr["creator_id"].ToString()))
                        {
                            CreationDate = UnixTimeStampToDateTime(int.Parse(dr["create_time"].ToString())),
                            DatabaseTable = "auroraassets_" + dr["id"].ToString().Substring(0, 1),
                            Description = dr["description"].ToString(),
                            Flags = (AssetFlags)int.Parse(dr["asset_flags"].ToString()),
                            HashCode = dr["hash_code"].ToString(),
                            HostUri = dr["host_uri"].ToString(),
                            LastAccessed = DateTime.UtcNow,
                            ParentID = (dr["parent_id"].ToString() == "") ? UUID.Parse(dr["id"].ToString()) : UUID.Parse(dr["parent_id"].ToString())
                        };
                    }
                }
            }
            catch (Exception e)
            {
                m_Log.Error("[LocalAssetBlackholeConnector] LoadAssetFromDR(); Error Loading", e);

            }
            finally
            {
                if (dr != null)
                {
                    dr.Close();
                }
            }
            return null;
        }

        #endregion

        #region Store Asset

        public UUID Store(AssetBase asset)
        {
            bool successful;
            asset = StoreAsset(asset, out successful);
            return asset.ID;
        }

        /// <summary>
        /// Stores the Asset in the database
        /// </summary>
        /// <param name="asset">Asset you wish to store</param>
        /// <returns></returns>
        public bool StoreAsset(AssetBase asset)
        {
            bool successful;
            StoreAsset(asset, out successful);
            return successful;
        }

        private AssetBase StoreAsset(AssetBase asset, out bool successful)
        {
            ResetTimer(15000);
            try
            {
                // this was causing problems with convering the first asset which.. is a zero id.. 
                if ((asset.ParentID == UUID.Zero) && (asset.ID != UUID.Zero))
                {
                    // most likely this has never been saved before or is some new asset
                    // otherwise the parent id would hold a value and would have had this check done before
                    List<string> check1 = m_Gd.Query(
                        "hash_code = '" + asset.HashCode + "' and creator_id = '" + asset.CreatorID +
                        "'", "auroraassets_temp", "id");
                    if (((check1 != null) && (check1.Count == 0)) || (check1 == null))
                    {
                        m_Gd.Insert("auroraassets_temp", new[] { "id", "hash_code", "creator_id" },
                                    new object[] { asset.ID, asset.HashCode, asset.CreatorID });
                        asset.ParentID = asset.ID;
                    }
                    else
                    {
                        successful = true;
                        AssetBase abtemp = GetAsset(UUID.Parse(check1[0]));
                        // not going to save it... 
                        // use existing asset instead
                        if (abtemp != null) return abtemp;

                        // that asset returned nothing.. so.. 
                        // do some checks on it later
                        m_Gd.Insert("auroraassets_tasks", new[] { "id", "task_type", "task_values" }, new object[] { UUID.Random(), "PARENTCHECK", check1[0] + "|" + asset.ID });
                        asset.ParentID = asset.ID;
                    }
                }

                // Ensure some data is correct
                string database = "auroraassets_" + asset.ID.ToString().Substring(0, 1);
                if (asset.Name.Length > 63) asset.Name = asset.Name.Substring(0, 63);
                if (asset.Description.Length > 128) asset.Description = asset.Description.Substring(0, 128);

                // Get the new hashcode if this is not MataOnly Data
                if ((!asset.MetaOnly) || ((asset.Data != null) && (asset.Data.Length >= 1)))
                    asset.HashCode = WriteFile(asset.ID, asset.Data);

                if ((!asset.MetaOnly) && (asset.HashCode != asset.LastHashCode))
                {
                    // Assign the task to check to see if this hash file is being used anymore
                    m_Gd.Insert("auroraassets_tasks", new[] { "id", "task_type", "task_values" }, new object[] { UUID.Random(), "HASHCHECK", asset.HashCode });
                }

                // Delete and save the asset
                Delete(asset.ID, false);
                m_Gd.Insert(database,
                            new[]
                                {
                                    "id", "hash_code", "parent_id", "creator_id", "name", "description", "asset_type",
                                    "create_time", "access_time", "asset_flags",
                                    "host_uri"
                                },
                            new object[]
                                {
                                    asset.ID, asset.HashCode,
                                    (asset.ID == asset.ParentID) ? ""
                                        : (UUID.Zero == asset.ParentID) ? "" : asset.ParentID.ToString(),
                                    (asset.CreatorID == UUID.Zero) ? "" : asset.CreatorID.ToString(), asset.Name,
                                    asset.Description, (int) asset.TypeAsset,
                                    Util.ToUnixTime(asset.CreationDate), Util.ToUnixTime(DateTime.UtcNow)
                                    , (int) asset.Flags, asset.HostUri
                                });

                // Double checked its saved. Just for debug
                if (needsConversion)
                {
                    if (m_Gd.Query("id", asset.ID, "auroraassets_" + asset.ID.ToString().Substring(0, 1), "id").Count ==
                        0)
                    {
                        m_Log.Error("[AssetDataPlugin] Asset did not saver propery: " + asset.ID);
                        successful = false;
                        return asset;
                    }
                }
                successful = true;
                return asset;
            }
            catch (Exception e)
            {
                m_Log.Error("[AssetDataPlugin]: StoreAsset(" + asset.ID + ")", e);
            }
            successful = false;
            return asset;
        }

        public void UpdateContent(UUID id, byte[] assetdata)
        {
            ResetTimer(15000);
            string newHash = WriteFile(id, assetdata);
            List<string> hashCodeCheck = m_Gd.Query("id", id, "auroraassets_" + id.ToString().ToCharArray()[0], "hash_code");
            if (hashCodeCheck.Count >= 1)
            {
                if (hashCodeCheck[0] != newHash)
                {
                    m_Gd.Insert("auroraassets_tasks", new[] { "id", "task_type", "task_values" },
                                new object[] { UUID.Random(), "HASHCHECK", hashCodeCheck[0] });
                    m_Gd.Update("auroraassets_" + id.ToString().ToCharArray()[0], new object[] { newHash },
                        new[] { "hash_code" }, new[] { "id" }, new object[] { id });

                }
            }
        }
        #endregion

        #region asset exists

        /// <summary>
        /// Check to see if a asset exists
        /// </summary>
        /// <param name="uuid">UUID of the asset you want to check</param>
        /// <returns></returns>
        public bool ExistsAsset(UUID uuid)
        {
            ResetTimer(15000);
            try
            {
                bool result = m_Gd.Query("id", uuid, "auroraassets_" + uuid.ToString().Substring(0, 1), "id").Count > 0;
                if (!result)
                    result = m_Gd.Query("id", uuid, "auroraassets_old", "id").Count > 0;
                if (!result)
                {
                    AssetBase a = Convert2BH(uuid);
                    return a != null;
                }
                return true;
            }
            catch (Exception e)
            {
                m_Log.ErrorFormat(
                    "[ASSETS DB]: MySql failure fetching asset {0}" + Environment.NewLine + e, uuid);
            }
            return false;
        }

        #endregion

        #region Delete Asset

        /// <summary>
        /// Delete the asset from the database and file system
        /// </summary>
        /// <param name="id">UUID of the asset you wish to delete</param>
        /// <returns></returns>
        public bool Delete(UUID id)
        {
            return Delete(id, true);
        }

        private bool Delete(UUID id, bool assignHashCodeCheckTask)
        {
            ResetTimer(15000);
            string tableName = "auroraassets_" + id.ToString().Substring(0, 1);
            try
            {
                // assign a task to see if the hash code is being used anywhere else
                if (assignHashCodeCheckTask)
                {
                    List<string> results = m_Gd.Query("id", id, tableName, "hash_code");
                    if (results.Count == 0) results = m_Gd.Query("id", id, "auroraassets_old", "hash_code");
                    if (results.Count == 1)
                    {
                        m_Gd.Insert("auroraassets_tasks", new[] { "id", "task_type", "task_values" },
                                    new object[] { UUID.Random(), "HASHCHECK", results[0] });
                    }
                }

                // delete the asset
                m_Gd.Delete(tableName, "id = '" + id + "'");
                // just for safe measure check here as well
                m_Gd.Delete("auroraassets_old", "id = '" + id + "'");
                return true;
            }
            catch (Exception e)
            {
                m_Log.Error("[AssetDataPlugin] Delete - Error for asset ID " + id, e);
                return false;
            }
        }

        #endregion

        #endregion

        #region util functions

        private static DateTime UnixTimeStampToDateTime(int unixTimeStamp)
        {
            // Unix timestamp is seconds past epoch
            DateTime dtDateTime = new DateTime(1970, 1, 1, 0, 0, 0, 0);
            dtDateTime = dtDateTime.AddSeconds(unixTimeStamp).ToLocalTime();
            return dtDateTime;
        }

        #endregion

        #region File Management

        private string WriteFile(UUID assetid, byte[] data, int tryCount)
        {
            bool alreadyWriten = false;
            Stream stream = null;
            BinaryFormatter bformatter = new BinaryFormatter();
            string hashCode = Convert.ToBase64String(new SHA256Managed().ComputeHash(data)) + data.Length;
            try
            {
                string filename = GetFileName(hashCode, false);
                string directory = Path.GetDirectoryName(filename);
                if (directory != null && !Directory.Exists(directory)) Directory.CreateDirectory(directory);
                if (File.Exists(filename)) alreadyWriten = true;

                if (!alreadyWriten)
                {
                    try
                    {
                        stream = File.Open(filename, FileMode.Create);
                        bformatter.Serialize(stream, data);
                        stream.Close();
                        stream = null;
                    }
                    catch (IOException e)
                    {
                        if (stream != null) stream.Close();
                        stream = null;
                        if (tryCount <= 2)
                        {
                            Thread.Sleep(4000);
                            WriteFile(assetid, data, ++tryCount);
                        }
                        else
                        {
                            m_Log.Error("[AssetDataPlugin] Error writing Asset File " + assetid, e);
                        }
                    }
                    string filenameForBackup = GetFileName(hashCode, true) + ".7z";
                    directory = Path.GetDirectoryName(filenameForBackup);
                    if (directory != null && !Directory.Exists(directory)) Directory.CreateDirectory(directory);
                    if (!File.Exists(filenameForBackup))
                        Util.Compress7ZipFile(filename, filenameForBackup);
                }
            }
            catch (Exception e)
            {
                m_Log.Error("[AssetDataPlugin]: WriteFile(" + assetid + ")", e);
            }
            finally
            {
                if (stream != null)
                    stream.Close();
            }
            return hashCode;
        }

        private string WriteFile(UUID assetid, byte[] data)
        {
            return WriteFile(assetid, data, 0);
        }

        private Byte[] LoadFile(string hashCode)
        {
            return LoadFile(hashCode, false);
        }

        private Byte[] LoadFile(string hashCode, bool waserror)
        {
            Stream stream = null;
            BinaryFormatter bformatter = new BinaryFormatter();
            byte[] results = new byte[] { };
            string filename = GetFileName(hashCode, false);
            try
            {
                if(!File.Exists(filename))
                {
                    if(!RestoreBackup(hashCode))
                        return new byte[] { };
                }
                stream = File.Open(filename, FileMode.Open, FileAccess.Read, FileShare.Read);
                results = (Byte[])bformatter.Deserialize(stream);
                if(hashCode != Convert.ToBase64String(SHA256HashGenerator.ComputeHash(results)) + results.Length)
                {
                    // seen this happen a couple times.. recovery seems to work good..
                    if (!waserror)
                    {
                        if (RestoreBackup(hashCode)) return LoadFile(hashCode, true);
                        else m_Log.Error("[AssetDataPlugin]: Resulting files didn't match hash. Failed recovery");
                    }
                    else
                        m_Log.Error("[AssetDataPlugin]: Resulting files didn't match hash. Failed recovery 2");
                }
                return results;
            }
            catch
            {
                if(!waserror)
                {
                    if(RestoreBackup(hashCode))
                        return LoadFile(hashCode, true);
                }
                return null;
            }
            finally
            {
                if(stream != null)
                    stream.Close();
                stream = null;
            }
        }

        private bool RestoreBackup(string hashCode)
        {
            string backupfile = GetFileName(hashCode, true);
            string file = GetFileName(hashCode, false);
            if (File.Exists(backupfile))
            {
                File.Move(file, file + ".corrupt");
                Util.UnCompress7ZipFile(backupfile + ".7z", Path.GetDirectoryName(file));
                m_Log.Info("[AssetDataPlugin]: Restored backup asset file " + file);
                return true;
            }
            return false;
        }

        private string GetFileName(string id, bool backup)
        {
            string path = (backup) ? m_CacheDirectoryBackup : m_CacheDirectory;
            try
            {
                id = m_InvalidChars.Aggregate(id, (current, c) => current.Replace(c, '_'));
                for (int p = 1; p <= m_CacheDirectoryTiers; p++)
                {
                    string pathPart = id.Substring(0, m_CacheDirectoryTierLen);
                    path = Path.Combine(path, pathPart);
                    id = id.Substring(1);
                }
            }
            catch (Exception ex)
            {
                m_Log.Error("[] Error while getting filename", ex);
            }
            return Path.Combine(path, id + ".ass");
        }

        #endregion

        #region Old Asset Migration To BlackHole
        private void StartMigration()
        {
            if (!sw.IsRunning) sw.Start();
            displayCount++;
            List<string> toConvert = m_Gd.Query(" 1 = 1 LIMIT 5 ", "assets", "id");
            if(toConvert.Count >= 1)
            {
                foreach(string assetkey in toConvert)
                    Convert2BH(UUID.Parse(assetkey));
            }
            else
                needsConversion = false;//ALL DONE!
            if (displayCount == 100)
            {
                sw.Stop();
                m_Log.Info("[Blackhole Assets] Converted:" + convertCount + " DupeContent:" + convertCountDupe +
                           " Dupe4Creator:" + convertCountParentFix);
                m_Log.Info("[Blackhole Assets] 500 in " + sw.Elapsed.Minutes + ":" + sw.Elapsed.Seconds);
                displayCount = 0;
                sw.Reset();
                sw.Start();
            }
        }

        private Dictionary<UUID, AssetBase> m_convertingAssets = new Dictionary<UUID, AssetBase>();
        private AssetBase Convert2BH(UUID uuid)
        {
            AssetBase asset = null;
            if(m_convertingAssets.TryGetValue(uuid, out asset))
                return asset;
            IDataReader dr = m_Gd.QueryData("WHERE id = '" + uuid + "' LIMIT 1", "assets", "id, name, description, assetType, local, temporary, asset_flags, CreatorID, create_time, data");
            try
            {
                if (dr != null)
                {
                    while(dr != null && dr.Read())
                    {
                        asset = new AssetBase(dr["id"].ToString(), dr["name"].ToString(), (AssetType)int.Parse(dr["assetType"].ToString()), UUID.Parse(dr["CreatorID"].ToString()))
                        {
                            CreatorID = UUID.Parse(dr["CreatorID"].ToString()),
                            Flags = (AssetFlags)int.Parse(dr["asset_flags"].ToString()),
                            Data = (Byte[])dr["data"],
                            Description = dr["description"].ToString(),
                            CreationDate = UnixTimeStampToDateTime(int.Parse(dr["create_time"].ToString())),
                            LastAccessed = DateTime.Now,
                            DatabaseTable = "auroraassets_" + dr["id"].ToString().Substring(0, 1),
                            MetaOnly = false,
                            ParentID = UUID.Parse(dr["id"].ToString())
                        };

                        // set the flags
                        if(dr["local"].ToString().Equals("1") || dr["local"].ToString().Equals("true", StringComparison.InvariantCultureIgnoreCase))
                            asset.Flags |= AssetFlags.Local;
                        if(bool.Parse(dr["temporary"].ToString()))
                            asset.Flags |= AssetFlags.Temperary;
                        dr.Close();
                        dr = null;
                        m_convertingAssets[uuid] = asset;

                        ResetTimer(1000);//Fire the timer in 1s to finish conversion
                        lock(m_genericTasks)
                        {
                            AssetBase asset1 = asset;
                            m_genericTasks.Add(delegate
                                                   {
                                // go through this asset and change all the guids to the parent IDs
                                if(!asset1.IsBinaryAsset)
                                {
                                    const string sPattern = @"(\{{0,1}([0-9a-fA-F]){8}-([0-9a-f]){4}-([0-9a-f]){4}-([0-9a-f]){4}-([0-9a-f]){12}\}{0,1})";
                                    string stringData = Utils.BytesToString(asset1.Data);
                                    bool changed = false;
                                    MatchCollection mc = Regex.Matches(stringData, sPattern);
                                    if(mc.Count >= 1)
                                    {
                                        foreach(Match match in mc)
                                        {
                                            try
                                            {
                                                UUID theMatch = UUID.Parse(match.Value);
                                                if(theMatch != UUID.Zero)
                                                {
                                                    AssetBase mightBeAsset = GetAsset(theMatch, true, false);
                                                    if((mightBeAsset != null) && (mightBeAsset.ParentID != UUID.Zero) &&
                                                        (mightBeAsset.ParentID != mightBeAsset.ID))
                                                    {
                                                        stringData = stringData.Replace(match.Value,
                                                                                        mightBeAsset.ParentID.ToString());
                                                        changed = true;
                                                    }
                                                }
                                            }
                                            catch(Exception e)
                                            {
                                                m_Log.Error("Errored", e);
                                            }
                                        }
                                    }
                                    if(changed)
                                    {
                                        asset1.Data = Utils.StringToBytes(stringData);
                                        // so it doesn't try to find the old file
                                        asset1.LastHashCode = asset1.HashCode;
                                    }
                                }
                                if(File.Exists(GetFileName(Convert.ToBase64String(SHA256HashGenerator.ComputeHash(asset1.Data)) + asset1.Data.Length, false)))
                                    convertCountDupe++;

                                // check to see if this asset should have a parent ID
                                List<string> check1 = m_Gd.Query(
                                    "hash_code = '" + asset1.HashCode + "' and creator_id = '" + asset1.CreatorID +
                                    "'", "auroraassets_temp", "id");
                                bool update = false;
                                bool insert = false;
                                if((check1 != null) && (check1.Count == 0))
                                {
                                    asset1.ParentID = asset1.ID;
                                    insert = true;
                                }
                                else if((check1 != null) && (check1[0] != asset1.ID.ToString()))
                                {
                                    convertCountParentFix++;
                                    asset1.ParentID = new UUID(check1[0]);

                                    update = true;
                                }
                                else
                                    asset1.ParentID = asset1.ID;

                                if(StoreAsset(asset1))
                                    m_Gd.Delete("assets", "id = '" + asset1.ID + "'");

                                try
                                {
                                    if(insert)
                                        m_Gd.Insert("auroraassets_temp", new[] { "id", "hash_code", "creator_id" },
                                                    new object[] { asset1.ID, asset1.HashCode, asset1.CreatorID });
                                    else if ((update) && (m_pointInventory2ParentAssets))
                                    {
                                        m_Gd.Update("inventoryitems", new object[] {asset1.ParentID}, new[] {"assetID"},
                                                    new[] {"assetID"}, new object[] {asset1.ID});
                                    }
                                }
                                catch(Exception e)
                                {
                                    m_Log.Error("[LocalAssetBlackholeManualMigration] Error on update/insert", e);
                                }
                                convertCount++;
                                m_convertingAssets.Remove(uuid);
                            });
                        }
                    }
                }
            }
            catch (Exception e)
            {
                m_Log.Error("[LocalAssetBlackholeManualMigration] Migrate Error", e);
            }
            finally
            {
                if (dr != null) dr.Close();
            }
            return asset;
        }
        #endregion

        #region Timer

        private void ResetTimer(int howLong)
        {
            taskTimer.Stop();
            taskTimer.Interval = howLong;
            if ((m_Enabled) && (!disableTimer))
                taskTimer.Start();
        }

        /// <summary>
        /// Timer runs tasks in the background when not busy
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void t_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            taskTimer.Stop();
            if(m_genericTasks.Count > 0)
            {
                List<Blank> tasks;
                lock(m_genericTasks)
                {
                    tasks = new List<Blank>(m_genericTasks);
                    m_genericTasks.Clear();
                }
                foreach(Blank b in tasks)
                {
                    b();
                }
                ResetTimer(1000);
                return;
            }
            if (needsConversion)
            {
                StartMigration();
                ResetTimer(1000);
                return;
            }

            // check for task in the auroraassets_task table
            List<string> taskCheck = m_Gd.Query(" 1 = 1 LIMIT 1 ", "auroraassets_tasks",
                                                "id, task_type, task_values");
            if (taskCheck.Count == 3)
            {
                string task_id = taskCheck[0];
                string task_type = taskCheck[1];
                string task_value = taskCheck[2];

                try
                {
                    //check if this hash file is still used anywhere
                    if (task_type == "HASHCHECK")
                    {
                        if (File.Exists(GetFileName(task_value, false)))
                        {
                            int result = TaskGetHashCodeUseCount(task_value);
                            if (result == 0)
                            {
                                m_Log.Info("[AssetDataPlugin] Deleteing old unused asset file");
                                File.Delete(GetFileName(task_value, false));
                                if (File.Exists(GetFileName(task_value, true)))
                                    File.Delete(GetFileName(task_value, true));
                            }
                        }
                    }
                    else if ((task_type == "PARENTCHECK") && (task_value.Split('|').Count() > 1))
                    {
                        UUID uuid1 = UUID.Parse(task_value.Split('|')[0]);

                        UUID uuid2 = UUID.Parse(task_value.Split('|')[1]);

                        // double check this asset does not exist 
                        AssetBase abtemp = GetAsset(uuid1);
                        AssetBase actemp = GetAsset(uuid2);
                        if ((abtemp == null) && (actemp != null))
                        {
                            m_Gd.Delete("auroraassets_temp", new[] { "id" }, new object[] { uuid1 });
                            m_Gd.Insert("auroraassets_temp", new[] { "id", "hash_code", "creator_id" },
                                        new object[] { actemp.ID, actemp.HashCode, actemp.CreatorID });
                            // I admit this might be a bit over kill.. 
                            m_Gd.Update("auroraassets_a", new object[] { uuid2 }, new[] { "parent_id" },
                                        new[] { "parent_id" }, new object[] { uuid1 });
                            m_Gd.Update("auroraassets_b", new object[] { uuid2 }, new[] { "parent_id" },
                                        new[] { "parent_id" }, new object[] { uuid1 });
                            m_Gd.Update("auroraassets_c", new object[] { uuid2 }, new[] { "parent_id" },
                                        new[] { "parent_id" }, new object[] { uuid1 });
                            m_Gd.Update("auroraassets_d", new object[] { uuid2 }, new[] { "parent_id" },
                                        new[] { "parent_id" }, new object[] { uuid1 });
                            m_Gd.Update("auroraassets_e", new object[] { uuid2 }, new[] { "parent_id" },
                                        new[] { "parent_id" }, new object[] { uuid1 });
                            m_Gd.Update("auroraassets_f", new object[] { uuid2 }, new[] { "parent_id" },
                                        new[] { "parent_id" }, new object[] { uuid1 });
                            m_Gd.Update("auroraassets_0", new object[] { uuid2 }, new[] { "parent_id" },
                                        new[] { "parent_id" }, new object[] { uuid1 });
                            m_Gd.Update("auroraassets_1", new object[] { uuid2 }, new[] { "parent_id" },
                                        new[] { "parent_id" }, new object[] { uuid1 });
                            m_Gd.Update("auroraassets_2", new object[] { uuid2 }, new[] { "parent_id" },
                                        new[] { "parent_id" }, new object[] { uuid1 });
                            m_Gd.Update("auroraassets_3", new object[] { uuid2 }, new[] { "parent_id" },
                                        new[] { "parent_id" }, new object[] { uuid1 });
                            m_Gd.Update("auroraassets_4", new object[] { uuid2 }, new[] { "parent_id" },
                                        new[] { "parent_id" }, new object[] { uuid1 });
                            m_Gd.Update("auroraassets_5", new object[] { uuid2 }, new[] { "parent_id" },
                                        new[] { "parent_id" }, new object[] { uuid1 });
                            m_Gd.Update("auroraassets_6", new object[] { uuid2 }, new[] { "parent_id" },
                                        new[] { "parent_id" }, new object[] { uuid1 });
                            m_Gd.Update("auroraassets_7", new object[] { uuid2 }, new[] { "parent_id" },
                                        new[] { "parent_id" }, new object[] { uuid1 });
                            m_Gd.Update("auroraassets_8", new object[] { uuid2 }, new[] { "parent_id" },
                                        new[] { "parent_id" }, new object[] { uuid1 });
                            m_Gd.Update("auroraassets_9", new object[] { uuid2 }, new[] { "parent_id" },
                                        new[] { "parent_id" }, new object[] { uuid1 });
                        }
                    }
                }
                catch (Exception ex)
                {
                    m_Log.Error("[AssetDataPlugin] Background task error. Task " + task_type, ex);
                }
                finally
                {
                    m_Gd.Delete("auroraassets_tasks", new[] { "id" }, new object[] { task_id });
                    ResetTimer(500);
                }
            }
            else
            {
                // check for old assets that have not been access for over 30 days
                try
                {
                    List<string> findOld2 =
                    m_Gd.Query(" access_time < " + Util.ToUnixTime(DateTime.UtcNow.AddDays(NumberOfDaysForOldAssets)) + " LIMIT 1 ",
                               "auroraassets_" + UUID.Random().ToString().ToCharArray()[0],
                               "id");
                    if (findOld2.Count >= 1)
                    {
                        foreach (string ass in findOld2)
                        {
                            List<string> findOld = m_Gd.Query(" id = '" + ass + "'",
                                                              "auroraassets_" + ass.ToCharArray()[0],
                                                              "id,hash_code,name,description,asset_type,create_time,access_time,asset_flags,creator_id,host_uri,parent_id");
                            if (m_Gd.Query("id", ass, "auroraassets_old", "id").Count == 0)
                                m_Gd.Insert("auroraassets_old",
                                            new[]
                                                {
                                                    "id", "hash_code", "name", "description", "asset_type",
                                                    "create_time",
                                                    "access_time", "asset_flags", "creator_id", "host_uri",
                                                    "parent_id"
                                                },
                                            new object[]
                                                {
                                                    findOld[0], findOld[1], findOld[2], findOld[3], findOld[4],
                                                    findOld[5],
                                                    findOld[6], findOld[7], findOld[8], findOld[9], findOld[10],
                                                    findOld[11]
                                                });
                            if (m_Gd.Query("id", ass, "auroraassets_old", "id").Count > 0)
                                m_Gd.Delete("auroraassets_" + ass.ToCharArray()[0], new[] { "id" }, new object[] { ass });
                        }
                        ResetTimer(100);
                        return;
                    }
                }
                catch (Exception exx)
                {
                    m_Log.Error("[AssetDataPlugin] Background task retiring asset", exx);
                }
            }
            ResetTimer(15000);
        }

        private int TaskGetHashCodeUseCount(string hash_code)
        {
            return m_Gd.Query("hash_code", hash_code, "auroraassets_old", "id").Count +
            m_Gd.Query("hash_code", hash_code, "auroraassets_9", "id").Count +
            m_Gd.Query("hash_code", hash_code, "auroraassets_8", "id").Count +
            m_Gd.Query("hash_code", hash_code, "auroraassets_7", "id").Count +
            m_Gd.Query("hash_code", hash_code, "auroraassets_6", "id").Count +
            m_Gd.Query("hash_code", hash_code, "auroraassets_5", "id").Count +
            m_Gd.Query("hash_code", hash_code, "auroraassets_4", "id").Count +
            m_Gd.Query("hash_code", hash_code, "auroraassets_3", "id").Count +
            m_Gd.Query("hash_code", hash_code, "auroraassets_2", "id").Count +
            m_Gd.Query("hash_code", hash_code, "auroraassets_1", "id").Count +
            m_Gd.Query("hash_code", hash_code, "auroraassets_0", "id").Count +
            m_Gd.Query("hash_code", hash_code, "auroraassets_f", "id").Count +
            m_Gd.Query("hash_code", hash_code, "auroraassets_e", "id").Count +
            m_Gd.Query("hash_code", hash_code, "auroraassets_d", "id").Count +
            m_Gd.Query("hash_code", hash_code, "auroraassets_c", "id").Count +
            m_Gd.Query("hash_code", hash_code, "auroraassets_b", "id").Count +
            m_Gd.Query("hash_code", hash_code, "auroraassets_a", "id").Count;
        }

        #endregion
    }
}
