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
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading;
using System.Text;
using System.Xml;
using log4net;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Serialization;
using OpenSim.Framework.Serialization.External;
using OpenSim.Region.CoreModules.World.Archiver;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;

namespace OpenSim.Region.CoreModules.Avatar.Inventory.Archiver
{
    public class InventoryArchiveReadRequest
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        protected TarArchiveReader archive;

        private UserAccount m_userInfo;
        private string m_invPath;
        
        /// <summary>
        /// Do we want to merge this load with existing inventory?
        /// </summary>
        protected bool m_merge;

        /// <value>
        /// We only use this to request modules
        /// </value>
        protected IRegistryCore m_registry;

        /// <value>
        /// The stream from which the inventory archive will be loaded.
        /// </value>
        private Stream m_loadStream;

        /// <summary>
        /// Record the creator id that should be associated with an asset.  This is used to adjust asset creator ids
        /// after OSP resolution (since OSP creators are only stored in the item
        /// </summary>
        protected Dictionary<UUID, UUID> m_creatorIdForAssetId = new Dictionary<UUID, UUID> ();

        public InventoryArchiveReadRequest(
            IRegistryCore registry, UserAccount userInfo, string invPath, string loadPath, bool merge)
            : this(
                registry,
                userInfo,
                invPath,
                new GZipStream(ArchiveHelpers.GetStream(loadPath), CompressionMode.Decompress),
                merge)
        {
        }

        public InventoryArchiveReadRequest(
            IRegistryCore registry, UserAccount userInfo, string invPath, Stream loadStream, bool merge)
        {
            m_registry = registry;
            m_merge = merge;
            m_userInfo = userInfo;
            m_invPath = invPath;
            m_loadStream = loadStream;
        }

        /// <summary>
        /// Execute the request
        /// </summary>
        /// <returns>
        /// A list of the inventory nodes loaded.  If folders were loaded then only the root folders are
        /// returned
        /// </returns>
        public HashSet<InventoryNodeBase> Execute(bool loadAll)
        {
            try
            {
                string filePath = "ERROR";
                int successfulAssetRestores = 0;
                int failedAssetRestores = 0;
                int successfulItemRestores = 0;

                HashSet<InventoryNodeBase> loadedNodes = loadAll ? new HashSet<InventoryNodeBase>() : null;
               
                List<InventoryFolderBase> folderCandidates
                    = InventoryArchiveUtils.FindFolderByPath(
                        m_registry.RequestModuleInterface<IInventoryService>(), m_userInfo.PrincipalID, m_invPath);
    
                if (folderCandidates.Count == 0)
                {
                    // Possibly provide an option later on to automatically create this folder if it does not exist
                    m_log.ErrorFormat("[INVENTORY ARCHIVER]: Inventory path {0} does not exist", m_invPath);
    
                    return loadedNodes;
                }
                
                InventoryFolderBase rootDestinationFolder = folderCandidates[0];
                archive = new TarArchiveReader(m_loadStream);
    
                // In order to load identically named folders, we need to keep track of the folders that we have already
                // resolved
                Dictionary <string, InventoryFolderBase> resolvedFolders = new Dictionary<string, InventoryFolderBase>();
    
                byte[] data;
                TarArchiveReader.TarEntryType entryType;

                while ((data = archive.ReadEntry(out filePath, out entryType)) != null)
                {
                    if (filePath.StartsWith(ArchiveConstants.ASSETS_PATH))
                    {
                        if (LoadAsset(filePath, data))
                            successfulAssetRestores++;
                        else
                            failedAssetRestores++;
    
                        if ((successfulAssetRestores) % 50 == 0)
                            m_log.InfoFormat(
                                "[INVENTORY ARCHIVER]: Loaded {0} assets...", 
                                successfulAssetRestores);
                    }
                    else if (filePath.StartsWith(ArchiveConstants.INVENTORY_PATH))
                    {
                        filePath = filePath.Substring(ArchiveConstants.INVENTORY_PATH.Length);
                        
                        // Trim off the file portion if we aren't already dealing with a directory path
                        if (TarArchiveReader.TarEntryType.TYPE_DIRECTORY != entryType)
                            filePath = filePath.Remove(filePath.LastIndexOf("/") + 1);
                        
                        InventoryFolderBase foundFolder 
                            = ReplicateArchivePathToUserInventory(
                                filePath, rootDestinationFolder, ref resolvedFolders, ref loadedNodes);
    
                        if (TarArchiveReader.TarEntryType.TYPE_DIRECTORY != entryType)
                        {
                            InventoryItemBase item = LoadItem (data, foundFolder);
    
                            if (item != null)
                            {
                                successfulItemRestores++;

                                if ((successfulItemRestores) % 50 == 0)
                                    m_log.InfoFormat(
                                        "[INVENTORY ARCHIVER]: Loaded {0} items...",
                                        successfulItemRestores);
                                
                                // If we aren't loading the folder containing the item then well need to update the 
                                // viewer separately for that item.
                                if (loadAll && !loadedNodes.Contains(foundFolder))
                                    loadedNodes.Add(item);
                            }
                            item = null;
                        }
                    }
                    data = null;
                }

                m_log.InfoFormat (
                    "[INVENTORY ARCHIVER]: Successfully loaded {0} assets with {1} failures", 
                    successfulAssetRestores, failedAssetRestores);
                m_log.InfoFormat ("[INVENTORY ARCHIVER]: Successfully loaded {0} items", successfulItemRestores);
                
                return loadedNodes;
            }
            finally
            {
                m_loadStream.Close();
            }
        }

        public void Close()
        {
            if (m_loadStream != null)
                m_loadStream.Close();
        }

        /// <summary>
        /// Replicate the inventory paths in the archive to the user's inventory as necessary.
        /// </summary>
        /// <param name="iarPath">The item archive path to replicate</param>
        /// <param name="rootDestinationFolder">The root folder for the inventory load</param>
        /// <param name="resolvedFolders">
        /// The folders that we have resolved so far for a given archive path.
        /// This method will add more folders if necessary
        /// </param>
        /// <param name="loadedNodes">
        /// Track the inventory nodes created.
        /// </param>
        /// <returns>The last user inventory folder created or found for the archive path</returns>
        public InventoryFolderBase ReplicateArchivePathToUserInventory(
            string iarPath, 
            InventoryFolderBase rootDestFolder, 
            ref Dictionary <string, InventoryFolderBase> resolvedFolders,
            ref HashSet<InventoryNodeBase> loadedNodes)
        {
            string iarPathExisting = iarPath;

//            m_log.DebugFormat(
//                "[INVENTORY ARCHIVER]: Loading folder {0} {1}", rootDestFolder.Name, rootDestFolder.ID);
                        
            InventoryFolderBase destFolder 
                = ResolveDestinationFolder(rootDestFolder, ref iarPathExisting, ref resolvedFolders);
            
//            m_log.DebugFormat(
//                "[INVENTORY ARCHIVER]: originalArchivePath [{0}], section already loaded [{1}]", 
//                iarPath, iarPathExisting);
            
            string iarPathToCreate = iarPath.Substring(iarPathExisting.Length);
            CreateFoldersForPath(destFolder, iarPathExisting, iarPathToCreate, ref resolvedFolders, ref loadedNodes);
            
            return destFolder;
        }

        /// <summary>
        /// Resolve a destination folder
        /// </summary>
        /// 
        /// We require here a root destination folder (usually the root of the user's inventory) and the archive
        /// path.  We also pass in a list of previously resolved folders in case we've found this one previously.
        /// 
        /// <param name="archivePath">
        /// The item archive path to resolve.  The portion of the path passed back is that
        /// which corresponds to the resolved desintation folder.
        /// <param name="rootDestinationFolder">
        /// The root folder for the inventory load
        /// </param>
        /// <param name="resolvedFolders">
        /// The folders that we have resolved so far for a given archive path.
        /// </param>
        /// <returns>
        /// The folder in the user's inventory that matches best the archive path given.  If no such folder was found
        /// then the passed in root destination folder is returned.
        /// </returns>
        protected InventoryFolderBase ResolveDestinationFolder(
            InventoryFolderBase rootDestFolder,
            ref string archivePath,
            ref Dictionary <string, InventoryFolderBase> resolvedFolders)
        {
//            string originalArchivePath = archivePath;

            while (archivePath.Length > 0)
            {
//                m_log.DebugFormat("[INVENTORY ARCHIVER]: Trying to resolve destination folder {0}", archivePath);
                
                if (resolvedFolders.ContainsKey(archivePath))
                {
//                    m_log.DebugFormat(
//                        "[INVENTORY ARCHIVER]: Found previously created folder from archive path {0}", archivePath);
                    return resolvedFolders[archivePath];
                }
                else
                {
                    if (m_merge)
                    {
                        // TODO: Using m_invPath is totally wrong - what we need to do is strip the uuid from the 
                        // iar name and try to find that instead.
                        string plainPath = ArchiveConstants.ExtractPlainPathFromIarPath(archivePath);
                        List<InventoryFolderBase> folderCandidates
                            = InventoryArchiveUtils.FindFolderByPath(
                                m_registry.RequestModuleInterface<IInventoryService>(), m_userInfo.PrincipalID, plainPath);
            
                        if (folderCandidates.Count != 0)
                        {
                            InventoryFolderBase destFolder = folderCandidates[0];
                            resolvedFolders[archivePath] = destFolder;
                            return destFolder;
                        }
                    }
                    
                    // Don't include the last slash so find the penultimate one
                    int penultimateSlashIndex = archivePath.LastIndexOf("/", archivePath.Length - 2);

                    if (penultimateSlashIndex >= 0)
                    {
                        // Remove the last section of path so that we can see if we've already resolved the parent
                        archivePath = archivePath.Remove(penultimateSlashIndex + 1);
                    }
                    else
                    {
//                        m_log.DebugFormat(
//                            "[INVENTORY ARCHIVER]: Found no previously created folder for archive path {0}",
//                            originalArchivePath);
                        archivePath = string.Empty;
                        return rootDestFolder;
                    }
                }
            }
            
            return rootDestFolder;
        }
        
        /// <summary>
        /// Create a set of folders for the given path.
        /// </summary>
        /// <param name="destFolder">
        /// The root folder from which the creation will take place.
        /// </param>
        /// <param name="iarPathExisting">
        /// the part of the iar path that already exists
        /// </param>
        /// <param name="iarPathToReplicate">
        /// The path to replicate in the user's inventory from iar
        /// </param>
        /// <param name="resolvedFolders">
        /// The folders that we have resolved so far for a given archive path.
        /// </param>
        /// <param name="loadedNodes">
        /// Track the inventory nodes created.
        /// </param>
        protected void CreateFoldersForPath(
            InventoryFolderBase destFolder, 
            string iarPathExisting,
            string iarPathToReplicate, 
            ref Dictionary <string, InventoryFolderBase> resolvedFolders, 
            ref HashSet<InventoryNodeBase> loadedNodes)
        {
            string[] rawDirsToCreate = iarPathToReplicate.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

            for (int i = 0; i < rawDirsToCreate.Length; i++)
            {
//                m_log.DebugFormat("[INVENTORY ARCHIVER]: Creating folder {0} from IAR", rawDirsToCreate[i]);

                if (!rawDirsToCreate[i].Contains(ArchiveConstants.INVENTORY_NODE_NAME_COMPONENT_SEPARATOR))
                    continue;
                
                int identicalNameIdentifierIndex
                    = rawDirsToCreate[i].LastIndexOf(
                        ArchiveConstants.INVENTORY_NODE_NAME_COMPONENT_SEPARATOR);

                string newFolderName = rawDirsToCreate[i].Remove(identicalNameIdentifierIndex);

                newFolderName = InventoryArchiveUtils.UnescapeArchivePath(newFolderName);
                UUID newFolderId = UUID.Random();

                // Asset type has to be Unknown here rather than Folder, otherwise the created folder can't be
                // deleted once the client has relogged.
                // The root folder appears to be labelled AssetType.Folder (shows up as "Category" in the client)
                // even though there is a AssetType.RootCategory
                destFolder 
                    = new InventoryFolderBase(
                        newFolderId, newFolderName, m_userInfo.PrincipalID, 
                        (short)AssetType.Unknown, destFolder.ID, 1);
                m_registry.RequestModuleInterface<IInventoryService>().AddFolder(destFolder);

                // Record that we have now created this folder
                iarPathExisting += rawDirsToCreate[i] + "/";
                m_log.DebugFormat("[INVENTORY ARCHIVER]: Created folder {0} from IAR", iarPathExisting);
                resolvedFolders[iarPathExisting] = destFolder;

                if (0 == i && loadedNodes != null)
                    loadedNodes.Add(destFolder);
            }
        }
        
        /// <summary>
        /// Load an item from the archive
        /// </summary>
        /// <param name="filePath">The archive path for the item</param>
        /// <param name="data">The raw item data</param>
        /// <param name="rootDestinationFolder">The root destination folder for loaded items</param>
        /// <param name="nodesLoaded">All the inventory nodes (items and folders) loaded so far</param>
        protected InventoryItemBase LoadItem(byte[] data, InventoryFolderBase loadFolder)
        {
            InventoryItemBase item = UserInventoryItemSerializer.Deserialize(data);
            
            // Don't use the item ID that's in the file
            item.ID = UUID.Random();

            UUID ospResolvedId = OspResolver.ResolveOspa(item.CreatorId, m_registry.RequestModuleInterface<IUserAccountService>()); 
            if (UUID.Zero != ospResolvedId)
            {
                item.CreatorIdAsUuid = ospResolvedId;

                // Don't preserve the OSPA in the creator id (which actually gets persisted to the
                // database).  Instead, replace with the UUID that we found.
                item.CreatorId = ospResolvedId.ToString ();

                item.CreatorData = string.Empty;
            }
            else if (item.CreatorData == null || item.CreatorData == String.Empty)
            {
                item.CreatorId = m_userInfo.PrincipalID.ToString ();
                item.CreatorIdAsUuid = new UUID (item.CreatorId);
            }

            item.Owner = m_userInfo.PrincipalID;

            // Record the creator id for the item's asset so that we can use it later, if necessary, when the asset
            // is loaded.
            // FIXME: This relies on the items coming before the assets in the TAR file.  Need to create stronger
            // checks for this, and maybe even an external tool for creating OARs which enforces this, rather than
            // relying on native tar tools.
            m_creatorIdForAssetId[item.AssetID] = item.CreatorIdAsUuid;

            // Reset folder ID to the one in which we want to load it
            item.Folder = loadFolder.ID;

            AddInventoryItem(item);
        
            return item;
        }

        public bool AddInventoryItem(InventoryItemBase item)
        {
            if (UUID.Zero == item.Folder)
            {
                InventoryFolderBase f = m_registry.RequestModuleInterface<IInventoryService>().GetFolderForType(item.Owner, (InventoryType)item.InvType, (AssetType)item.AssetType);
                if (f != null)
                {
                    //                    m_log.DebugFormat(
                    //                        "[LOCAL INVENTORY SERVICES CONNECTOR]: Found folder {0} type {1} for item {2}", 
                    //                        f.Name, (AssetType)f.Type, item.Name);

                    item.Folder = f.ID;
                }
                else
                {
                    f = m_registry.RequestModuleInterface<IInventoryService>().GetRootFolder(item.Owner);
                    if (f != null)
                    {
                        item.Folder = f.ID;
                    }
                    else
                    {
                        m_log.WarnFormat(
                            "[AGENT INVENTORY]: Could not find root folder for {0} when trying to add item {1} with no parent folder specified",
                            item.Owner, item.Name);
                        return false;
                    }
                }
            }

            if (m_registry.RequestModuleInterface<IInventoryService>().AddItem(item))
                return true;
            else
            {
                m_log.WarnFormat(
                    "[AGENT INVENTORY]: Agent {0} could not add item {1} {2}",
                    item.Owner, item.Name, item.ID);

                return false;
            }
        }

        /// <summary>
        /// Load an asset
        /// </summary>
        /// <param name="assetFilename"></param>
        /// <param name="data"></param>
        /// <returns>true if asset was successfully loaded, false otherwise</returns>
        private bool LoadAsset(string assetPath, byte[] data)
        {
            //IRegionSerialiser serialiser = scene.RequestModuleInterface<IRegionSerialiser>();
            // Right now we're nastily obtaining the UUID from the filename
            string filename = assetPath.Remove(0, ArchiveConstants.ASSETS_PATH.Length);
            int i = filename.LastIndexOf(ArchiveConstants.ASSET_EXTENSION_SEPARATOR);

            if (i == -1)
            {
                m_log.ErrorFormat(
                   "[INVENTORY ARCHIVER]: Could not find extension information in asset path {0} since it's missing the separator {1}.  Skipping",
                    assetPath, ArchiveConstants.ASSET_EXTENSION_SEPARATOR);

                return false;
            }

            string extension = filename.Substring(i);
            string uuid = filename.Remove(filename.Length - extension.Length);

            if (ArchiveConstants.EXTENSION_TO_ASSET_TYPE.ContainsKey(extension))
            {
                AssetType assetType = ArchiveConstants.EXTENSION_TO_ASSET_TYPE[extension];

                if (assetType == AssetType.Unknown)
                    m_log.WarnFormat("[INVENTORY ARCHIVER]: Importing {0} byte asset {1} with unknown type", data.Length, uuid);
                else if (assetType == AssetType.Object)
                {
                    string xmlData = Utils.BytesToString(data);
                    List<SceneObjectGroup> sceneObjects = new List<SceneObjectGroup>();

                    sceneObjects.Add(OpenSim.Region.Framework.Scenes.Serialization.SceneObjectSerializer.FromOriginalXmlFormat(xmlData, m_registry));

                    if(m_creatorIdForAssetId.ContainsKey(UUID.Parse(uuid)))
                    {
                        foreach (SceneObjectGroup sog in sceneObjects)
                            foreach (SceneObjectPart sop in sog.Parts)
                                if (string.IsNullOrEmpty(sop.CreatorData))
                                    sop.CreatorID = m_creatorIdForAssetId[UUID.Parse (uuid)];
                    }
                    foreach(SceneObjectGroup sog in sceneObjects)
                        foreach(SceneObjectPart sop in sog.Parts)
                        {
                            //Fix ownerIDs and perms
                            sop.Inventory.ApplyGodPermissions((uint)PermissionMask.All);
                            sog.ApplyPermissions((uint)PermissionMask.All);
                            foreach(TaskInventoryItem item in sop.Inventory.GetInventoryItems())
                                item.OwnerID = m_userInfo.PrincipalID;
                            sop.OwnerID = m_userInfo.PrincipalID;
                        }
                    data = Utils.StringToBytes(OpenSim.Region.Framework.Scenes.Serialization.SceneObjectSerializer.ToOriginalXmlFormat(sceneObjects[0]));
                }
                //m_log.DebugFormat("[INVENTORY ARCHIVER]: Importing asset {0}, type {1}", uuid, assetType);

                AssetBase asset = new AssetBase(UUID.Parse(uuid), "RandomName", (AssetType) assetType, UUID.Zero)
                                      {Data = data, Flags = AssetFlags.Normal};
                asset.ID = m_registry.RequestModuleInterface<IAssetService>().Store(asset);
                return true;
            }
            else
            {
                m_log.ErrorFormat(
                   "[INVENTORY ARCHIVER]: Tried to dearchive data with path {0} with an unknown type extension {1}",
                    assetPath, extension);

                return false;
            }
        }
    }
}
