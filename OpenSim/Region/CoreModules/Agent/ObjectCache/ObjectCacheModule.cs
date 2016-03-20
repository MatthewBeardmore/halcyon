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
using System.IO;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Framework;
using Mono.Addins;
using log4net;
using System.Reflection;

namespace OpenSim.Region.CoreModules.ObjectCache
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule")]
    public class ObjectCacheModule : INonSharedRegionModule, IObjectCache
    {
        #region Declares

        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly Dictionary<UUID, Dictionary<UUID, uint>> ObjectCacheAgents =
            new Dictionary<UUID, Dictionary<UUID, uint>>();

        protected bool m_Enabled = true;

        private string m_filePath = "ObjectCache/";
        private Scene m_scene;

        #endregion

        #region INonSharedRegionModule

        public virtual void Initialize(IConfigSource source)
        {
            IConfig moduleConfig = source.Configs["ObjectCache"];
            if (moduleConfig != null)
            {
                m_Enabled = moduleConfig.GetString("Module", "") == Name;
                m_filePath = moduleConfig.GetString("PathToSaveFiles", m_filePath);
            }

            //MTB - disable this for now as it really doesn't work... needs more investigation
            // as to why the viewer always rejects ObjectCachePackets
            m_Enabled = false;

            if (m_Enabled)
            {
                if (!Directory.Exists(m_filePath))
                {
                    try
                    {
                        Directory.CreateDirectory(m_filePath);
                    }
                    catch (Exception)
                    {
                    }
                }
                m_log.Info("[ObjectCache]: Module enabled and using path " + m_filePath);
            }
        }

        public virtual void AddRegion(Scene scene)
        {
            if (!m_Enabled)
                return;

            m_scene = scene;
            scene.RegisterModuleInterface<IObjectCache>(this);
            scene.EventManager.OnNewClient += OnNewClient;
            scene.EventManager.OnClientClosed += EventManager_OnClientClosed;
        }

        public virtual void RemoveRegion(Scene scene)
        {
            if (!m_Enabled)
                return;

            scene.UnregisterModuleInterface<IObjectCache>(this);
            scene.EventManager.OnNewClient -= OnNewClient;
            scene.EventManager.OnClientClosed -= EventManager_OnClientClosed;
        }

        public virtual void RegionLoaded(Scene scene)
        {
        }

        public virtual void Close()
        {
        }

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        public virtual string Name
        {
            get { return "ObjectCacheModule"; }
        }

        #region Events

        public void OnNewClient(IClientAPI client)
        {
            ScenePresence sp;
            ((Scene)client.Scene).TryGetAvatar(client.AgentId, out sp);
            //Create the client's cache
            if (sp != null)
            {
                Util.FireAndForget(LoadFileOnNewClient, sp.UUID);
            }
        }

        /// <summary>
        ///     Load the file for the client async so that we don't lock up the system for too long
        /// </summary>
        /// <param name="o"></param>
        public void LoadFileOnNewClient(object o)
        {
            UUID agentID = (UUID) o;
            LoadFromFileForClient(agentID);
        }

        private void EventManager_OnClientClosed(UUID clientID, Scene scene)
        {
            //Save the cache to the file for the client
            ScenePresence sp;
            scene.TryGetAvatar(clientID, out sp);
            //This is shared, so all get saved into one file
            if (sp != null && !sp.IsChildAgent)
                SaveToFileForClient(clientID);
            //Remove the client's cache
            lock (ObjectCacheAgents)
            {
                ObjectCacheAgents.Remove(clientID);
            }
        }

        #endregion

        #region Serialization

        public string SerializeAgentCache(Dictionary<UUID, uint> cache)
        {
            OSDMap cachedMap = new OSDMap();
            foreach (KeyValuePair<UUID, uint> kvp in cache)
            {
                cachedMap.Add(kvp.Key.ToString(), OSD.FromUInteger(kvp.Value));
            }
            return OSDParser.SerializeJsonString(cachedMap);
        }

        public Dictionary<UUID, uint> DeserializeAgentCache(string osdMap)
        {
            Dictionary<UUID, uint> cache = new Dictionary<UUID, uint>();
            try
            {
                OSDMap cachedMap = (OSDMap) OSDParser.DeserializeJson(osdMap);
                foreach (KeyValuePair<string, OSD> kvp in cachedMap)
                {
                    cache[UUID.Parse(kvp.Key)] = kvp.Value.AsUInteger();
                }
            }
            catch
            {
                //It has an error, destroy the cache
                //null will tell the caller that it errored out and needs to be removed
                cache = null;
            }
            return cache;
        }

        #endregion

        #region Load/Save from file

        public void SaveToFileForClient(UUID AgentID)
        {
            Dictionary<UUID, uint> cache;
            lock (ObjectCacheAgents)
            {
                if (!ObjectCacheAgents.ContainsKey(AgentID))
                    return;
                cache = new Dictionary<UUID, uint>(ObjectCacheAgents[AgentID]);
                ObjectCacheAgents[AgentID].Clear();
                ObjectCacheAgents.Remove(AgentID);
            }
            FileStream stream = new FileStream(m_filePath + AgentID + m_scene.RegionInfo.RegionName + ".oc",
                                               FileMode.Create);
            StreamWriter m_streamWriter = new StreamWriter(stream);
            m_streamWriter.WriteLine(SerializeAgentCache(cache));
            m_streamWriter.Close();
        }

        public void LoadFromFileForClient(UUID AgentID)
        {
            FileStream stream = new FileStream(m_filePath + AgentID + m_scene.RegionInfo.RegionName + ".oc",
                                               FileMode.OpenOrCreate);
            StreamReader m_streamReader = new StreamReader(stream);
            string file = m_streamReader.ReadToEnd();
            m_streamReader.Close();
            //Read file here
            if (file != "") //New file
            {
                Dictionary<UUID, uint> cache = DeserializeAgentCache(file);
                if (cache == null)
                {
                    //Something went wrong, delete the file
                    try
                    {
                        File.Delete(m_filePath + AgentID + m_scene.RegionInfo.RegionName + ".oc");
                    }
                    catch
                    {
                    }
                    return;
                }
                lock (ObjectCacheAgents)
                {
                    ObjectCacheAgents[AgentID] = cache;
                }
            }
        }

        #endregion

        public virtual void PostInitialise()
        {
        }

        #endregion

        #region IObjectCache

        /// <summary>
        ///     Check whether we can send a CachedObjectUpdate to the client
        /// </summary>
        /// <param name="AgentID"></param>
        /// <param name="localID"></param>
        /// <param name="CurrentEntityCRC"></param>
        /// <returns></returns>
        public bool UseCachedObject(UUID AgentID, UUID localID, uint CurrentEntityCRC)
        {
            lock (ObjectCacheAgents)
            {
                if (ObjectCacheAgents.ContainsKey(AgentID))
                {
                    uint CurrentCachedCRC = 0;
                    if (ObjectCacheAgents[AgentID].TryGetValue(localID, out CurrentCachedCRC))
                    {
                        if (CurrentEntityCRC == CurrentCachedCRC)
                        {
                            //The client knows of the newest version
                            return true;
                        }
                    }
                }
                return false;
            }
        }

        public void AddCachedObject(UUID AgentID, UUID localID, uint CRC)
        {
            lock (ObjectCacheAgents)
            {
                if (!ObjectCacheAgents.ContainsKey(AgentID))
                    ObjectCacheAgents[AgentID] = new Dictionary<UUID, uint>();
                ObjectCacheAgents[AgentID][localID] = CRC;
            }
        }

        public void RemoveObject(UUID AgentID, UUID localID, byte cacheMissType)
        {
            lock (ObjectCacheAgents)
            {
                if (ObjectCacheAgents.ContainsKey(AgentID))
                    ObjectCacheAgents[AgentID].Remove(localID);
            }
        }

        #endregion
    }
}