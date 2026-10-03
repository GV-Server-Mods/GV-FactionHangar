using ProtoBuf;
using Sandbox.Game;
using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;
using VRage.Game.ModAPI;
using VRage.GameServices;
using VRageMath;

namespace CustomHangar
{

    public enum HangarType
    {
        Private,
        Faction
    }

    public enum SpawnType
    {
        None,
        Dynamic,
        Nearby,
        SpawnArea,
        Original
    }

    [XmlRoot("HangarData")]
    public class AllHangarData
    {
        [XmlElement("FactionData")] public List<FactionData> factionData = new List<FactionData>();
        [XmlElement("PrivateData")] public List<PrivateData> privateData = new List<PrivateData>();

        public AllHangarData() { }

        public GridData AddFactionData(long factionId, string gridName, long gridId, long playerId, string path, string playerName, bool autoHangar)
        {
            IMyFaction faction = MyAPIGateway.Session.Factions.TryGetFactionById(factionId);
            if (faction == null) return null;

            GridData gData = new GridData()
            {
                gridId = gridId,
                gridName = gridName,
                owner = playerId,
                gridPath = path,
                ownerName = playerName,
                autoHangared = autoHangar
            };

            FactionData fData = GetFactionData(factionId);
            if (fData == null)
            {
                HangarData hData = new HangarData()
                {
                    gridData = new List<GridData>() { gData },
                    type = HangarType.Faction
                };
                
                FactionData data = new FactionData()
                {
                    factionId = factionId,
                    factionHangarData = hData,
                    factionName = faction.Name
                };

                factionData.Add(data);
            }
            else
                fData.factionHangarData.gridData.Add(gData);

            return gData;
        }

        /// <summary>Removes one entry by reference (indexes can shift while a store is in progress).</summary>
        public void RemoveEntry(GridData entry)
        {
            foreach (var fData in factionData)
                if (fData.factionHangarData.gridData.Remove(entry)) return;

            foreach (var pData in privateData)
                if (pData.privateHangarData.gridData.Remove(entry)) return;
        }

        public void RemoveFactionData(long factionId, int index, bool nullBlueprint = false)
        {
            if (factionData == null) return;

            foreach (var fData in factionData)
                if (fData.factionId == factionId)
                    if (index <= fData.factionHangarData.gridData.Count - 1)
                    {
                        if (nullBlueprint)
                            Session.Instance.cacheGridPaths.Add(fData.factionHangarData.gridData[index].gridPath);

                        fData.factionHangarData.gridData.RemoveAt(index);
                        return;
                    }
        }

        public int GetFactionSlots(long factionId)
        {
            if (factionData == null) return 0;

            foreach (var fData in factionData)
            {
                int slots = 0;
                if (fData.factionId == factionId)
                {
                    foreach(var gData in fData.factionHangarData.gridData)
                    {
                        if (!gData.autoHangared)
                            slots++;
                    }

                    return slots;
                }
            }

            return 0;
        }

        public FactionData GetFactionData(long factionId)
        {
            if (factionData == null) return null;

            foreach (var fData in factionData)
                if (fData.factionId == factionId) return fData;

            return null;
        }

        public GridData GetFactionGridData(long factionId, int index)
        {
            FactionData fData = GetFactionData(factionId);
            if (fData == null) return null;

            if (index >= 0)
            {
                if (index > fData.factionHangarData.gridData.Count - 1) return null;
                return fData.factionHangarData.gridData[index];
            }

            return null;
        }

        public string GetFactionsGridNames(long factionId, long playerId, bool isLeader = true)
        {
            if (factionData == null) return null;

            string names = "";
            foreach (var fData in factionData)
            {
                if (fData.factionId == factionId)
                {
                    if (fData.factionHangarData.gridData.Count == 0)
                        return names;

                    var sb = new StringBuilder();
                    for (int i = 0; i < fData.factionHangarData.gridData.Count; i++)
                    {
                        // Members only see (and can only use) their own grids
                        if (!isLeader && fData.factionHangarData.gridData[i].owner != playerId) continue;
                        AppendGridLine(sb, i, fData.factionHangarData.gridData[i]);
                    }

                    names = sb.ToString();
                    names += $"\nFaction Hangar Totals: {GetFactionSlots(factionId)}/{Session.Instance.config.factionHangarConfig.maxFactionSlots}";
                }
                    
            }

            return names;
        }

        public bool IsFactionGridAutoHangared(long factionId, int index)
        {
            FactionData fData = GetFactionData(factionId);
            if (fData == null) return false;

            if (index >= 0)
            {
                if (index > fData.factionHangarData.gridData.Count - 1) return false;
                return fData.factionHangarData.gridData[index].autoHangared;
            }

            return false;
        }

        public int GetPrivateSlots(long playerId)
        {
            if (privateData == null) return 0;
            foreach (var pData in privateData)
            {
                if (pData.playerId == playerId)
                {
                    int slots = 0;
                    foreach(var gData in pData.privateHangarData.gridData)
                    {
                        if (!gData.autoHangared)
                            slots++;
                    }

                    return slots;
                }
            }

            return 0;
        }

        public string GetPrivateGridNames(long playerId)
        {
            if (privateData == null) return null;

            string names = "";
            foreach (var pData in privateData)
            {
                if (pData.playerId == playerId)
                {
                    if (pData.privateHangarData.gridData.Count == 0)
                        return names;

                    var sb = new StringBuilder();
                    for (int i = 0; i < pData.privateHangarData.gridData.Count; i++)
                        AppendGridLine(sb, i, pData.privateHangarData.gridData[i]);

                    names = sb.ToString();
                    names += $"\nPrivate Hangar Totals: {GetPrivateSlots(playerId)}/{Session.Instance.config.privateHangarConfig.maxPrivateSlots}";
                }
            }

            return names;
        }

        static void AppendGridLine(StringBuilder sb, int index, GridData data)
        {
            sb.Append('\n').Append('[').Append(index).Append("] ").Append(data.gridName);
            if (data.autoHangared) sb.Append(" [AutoHangar]");
            if (data.fileMissing) sb.Append(" [Missing]");
        }

        public PrivateData GetPrivateData(long playerId)
        {
            if (privateData == null) return null;

            foreach (var pData in privateData)
                if (pData.playerId == playerId) return pData;

            return null;
        }

        public GridData GetPrivateGridData(long playerId, int index)
        {
            PrivateData pData = GetPrivateData(playerId);
            if (pData == null) return null;

            if (index >= 0)
            {
                if (index > pData.privateHangarData.gridData.Count - 1) return null;
                return pData.privateHangarData.gridData[index];
            }

            return null;
        }

        public bool IsPrivateGridAutoHangared(long playerId, int index)
        {
            PrivateData pData = GetPrivateData(playerId);
            if (pData == null) return false;

            if (index >= 0)
            {
                if (index > pData.privateHangarData.gridData.Count - 1) return false;
                return pData.privateHangarData.gridData[index].autoHangared;
            }

            return false;
        }

        public bool TransferFactionToPrivate(long factionId, int index, long playerId)
        {
            if (!Session.Instance.config.factionHangarConfig.factionToPrivateTransfer)
            {
                MyVisualScriptLogicProvider.SendChatMessageColored($"Faction to private transfers are not allowed.", Color.Red, "[FactionHangar]", playerId, "Red");
                return false;
            }

            GridData gridData = GetFactionGridData(factionId, index);
            if (gridData == null)
            {
                MyVisualScriptLogicProvider.SendChatMessageColored($"Grid index {index} is invalid", Color.Red, "[FactionHangar]", playerId, "Red");
                return false;
            }

            if (gridData.owner != playerId)
            {
                MyVisualScriptLogicProvider.SendChatMessageColored($"You can only transfer grids that you own.", Color.Red, "[FactionHangar]", playerId, "Red");
                return false;
            }

            if (GetPrivateSlots(playerId) >= Session.Instance.config.privateHangarConfig.maxPrivateSlots)
            {
                MyVisualScriptLogicProvider.SendChatMessageColored($"Failed to transfer grid, you have exceeded your private hangar slots.", Color.Red, "[FactionHangar]", playerId, "Red");
                return false;
            }

            RemoveFactionData(factionId, index);
            AddPrivateData(gridData.gridName, gridData.gridId, playerId, gridData.gridPath, gridData.ownerName, gridData.autoHangared);
            MyVisualScriptLogicProvider.SendChatMessageColored($"Moved {gridData.gridName} to your private hangar.", Color.Green, "[FactionHangar]", playerId, "Green");
            return true;
        }

        public bool TransferPrivateToFaction(long factionId, int index, long playerId)
        {
            if (!Session.Instance.config.privateHangarConfig.privateToFactionTransfer)
            {
                MyVisualScriptLogicProvider.SendChatMessageColored($"Private to faction transfers are not allowed.", Color.Red, "[FactionHangar]", playerId, "Red");
                return false;
            }

            GridData gridData = GetPrivateGridData(playerId, index);
            if (gridData == null)
            {
                MyVisualScriptLogicProvider.SendChatMessageColored($"Grid index {index} is invalid", Color.Red, "[FactionHangar]", playerId, "Red");
                return false;
            }

            if (GetFactionSlots(factionId) >= Session.Instance.config.factionHangarConfig.maxFactionSlots)
            {
                MyVisualScriptLogicProvider.SendChatMessageColored($"Failed to transfer grid, you have exceeded the faction hangar slots.", Color.Red, "[FactionHangar]", playerId, "Red");
                return false;
            }

            RemovePrivateData(playerId, index);
            AddFactionData(factionId, gridData.gridName, gridData.gridId, playerId, gridData.gridPath, gridData.ownerName, gridData.autoHangared);
            MyVisualScriptLogicProvider.SendChatMessageColored($"Moved {gridData.gridName} to the faction hangar.", Color.Green, "[FactionHangar]", playerId, "Green");
            return true;
        }

        public GridData AddPrivateData(string gridName, long gridId, long playerId, string path, string playerName, bool autoHangar)
        {
            GridData gData = new GridData()
            {
                gridId = gridId,
                gridName = gridName,
                owner = playerId,
                gridPath = path,
                ownerName = playerName,
                autoHangared = autoHangar
            };

            PrivateData pData = GetPrivateData(playerId);
            if (pData == null)
            {
                HangarData hData = new HangarData()
                {
                    gridData = new List<GridData>() { gData },
                    type = HangarType.Private
                };

                PrivateData data = new PrivateData()
                {
                    privateHangarData = hData,
                    playerId = playerId,
                    playerNameRef = playerName,
                };

                privateData.Add(data);
            }
            else
                pData.privateHangarData.gridData.Add(gData);

            return gData;
        }

        public void RemovePrivateData(long playerId, int index, bool nullBlueprint = false)
        {
            if (privateData == null) return;

            foreach (var pData in privateData)
                if (pData.playerId == playerId)
                    if (index <= pData.privateHangarData.gridData.Count - 1)
                    {
                        if (nullBlueprint)
                            Session.Instance.cacheGridPaths.Add(pData.privateHangarData.gridData[index].gridPath);

                        pData.privateHangarData.gridData.RemoveAt(index);
                        return;
                    }
        }

        public static AllHangarData LoadHangarData()
        {
            if (MyAPIGateway.Utilities.FileExistsInWorldStorage("FactionHangarStorage.xml", typeof(AllHangarData)) == true)
            {
                string content = null;
                try
                {
                    var reader = MyAPIGateway.Utilities.ReadFileInWorldStorage("FactionHangarStorage.xml", typeof(AllHangarData));
                    content = reader.ReadToEnd();
                    reader.Close();

                    AllHangarData data = MyAPIGateway.Utilities.SerializeFromXML<AllHangarData>(content);
                    if (data != null)
                    {
                        if (data.factionData == null) data.factionData = new List<FactionData>();
                        if (data.privateData == null) data.privateData = new List<PrivateData>();
                        return data;
                    }
                }
                catch (Exception ex)
                {
                    VRage.Utils.MyLog.Default.WriteLineAndConsole($"FactionHangar: Could not read hangar data!\n {ex}");
                }

                // The next save overwrites the file, so keep a copy of what couldn't be read
                BackupUnreadableData(content);
                return new AllHangarData();
            }

            return new AllHangarData();
        }

        static void BackupUnreadableData(string content)
        {
            const string backupName = "FactionHangarStorage.unreadable.xml";
            if (string.IsNullOrEmpty(content))
            {
                VRage.Utils.MyLog.Default.WriteLineAndConsole("FactionHangar: Starting with empty hangars; the hangar data file could not be read at all.");
                return;
            }

            try
            {
                using (var writer = MyAPIGateway.Utilities.WriteFileInWorldStorage(backupName, typeof(AllHangarData)))
                    writer.Write(content);
                VRage.Utils.MyLog.Default.WriteLineAndConsole($"FactionHangar: Starting with empty hangars. The unreadable file is kept as {backupName} in the world's Storage folder.");
            }
            catch (Exception ex)
            {
                VRage.Utils.MyLog.Default.WriteLineAndConsole($"FactionHangar: Could not back up hangar data!\n {ex}");
            }
        }
    }

    public class FactionData
    {
        [XmlElement("Faction")] public string factionName;
        [XmlElement("FactionId")] public long factionId;
        [XmlElement("FactionHangarData")] public HangarData factionHangarData;
    }

    public class PrivateData
    {
        [XmlElement("Private")] public HangarData privateHangarData;
        [XmlElement("PlayerId")] public long playerId;
        [XmlElement("Player")] public string playerNameRef;
    }

    public class HangarData
    {
        [XmlElement("HangarType")] public HangarType type;
        [XmlElement("StoredGrid")] public List<GridData> gridData = new List<GridData>();
    }

    [ProtoContract]
    public class HangarDelayData
    {
        [ProtoMember(1)] public long playerId;
        [ProtoMember(2)] public List<GridData> gridData;
        [ProtoMember(3)] public int timer;
        [ProtoMember(4)] public string playerName;
        [ProtoMember(5)] public HangarType hangarType;
        [ProtoMember(6)] public long requesterId;
    }

    [ProtoContract]
    public class GridData
    {
        [XmlElement("GridId")] [ProtoMember(1)] public long gridId;
        [XmlElement("GridName")] [ProtoMember(2)] public string gridName;
        [XmlElement("StoredPath")] [ProtoMember(3)] public string gridPath;
        [XmlElement("Owner")] [ProtoMember(4)] public long owner;
        [XmlElement("OwnerName")] [ProtoMember(5)] public string ownerName;
        [XmlElement("AutoHangared")][ProtoMember(6)] public bool autoHangared;
        // Server only: set when a load finds the stored file missing, shown as [Missing] in the list
        [XmlIgnore] [ProtoIgnore] public bool fileMissing;
    }

    public class CacheGridsForStorage
    {
        public long ownerId;
        public long requesterId;
        public List<IMyCubeGrid> grids;
    }
}

