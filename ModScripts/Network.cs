using ProtoBuf;
using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Utils;
using Sandbox.Game;
using VRageMath;
using Sandbox.Engine.Utils;
using VRage.ObjectBuilders;
using System.Reflection;

namespace CustomHangar
{

    public enum DataType
    {
        Sync,
        StoreGrid,
        FactionList,
        PrivateList,
        LoadFactionGrid,
        RequestGridData,
        SendGridData,
        SendObToSpawn,
        RequestConfig,
        SendConfig,
        ClientRequestTransfer,
        AddClientCooldown,
        UpdateIdentities,
        RequestGridRemoval,
        FactionToPrivateTransfer,
        PrivateToFactionTransfer,
        SendChatMessage,
        AddTime // retired: clients could add or shorten any faction's cooldowns; kept so enum values stay stable
    }

    [ProtoContract]
    public class ObjectContainer
    {
        [ProtoMember(1)] public Config settings;
        [ProtoMember(2)] public long playerId;
        [ProtoMember(3)] public List<GridData> gridData;
        [ProtoMember(4)] public string stringData;
        [ProtoMember(5)] public bool boolValue;
        [ProtoMember(6)] public int intValue;
        [ProtoMember(7)] public HangarType hangarType;
        [ProtoMember(8)] public long factionId;
        [ProtoMember(9)] public ulong steamId;
        [ProtoMember(10)] public MyObjectBuilder_Base ob;
        [ProtoMember(11)] public List<MyObjectBuilder_CubeGrid> cubeGridObs;
        [ProtoMember(12)] public Config config;
        [ProtoMember(13)] public TimerType timerType;
        [ProtoMember(14)] public List<MyObjectBuilder_Identity> identityObs;
        [ProtoMember(15)] public long requesterId;
        [ProtoMember(16)] public bool originalLocation;
        [ProtoMember(17)] public long cost;
        [ProtoMember(18)] public SpawnType spawnType;
        [ProtoMember(19)] public bool force;
        [ProtoMember(20)] public long gridId;
        [ProtoMember(21)] public MyPositionAndOrientation placement;
        [ProtoMember(22)] public double mass;
    }

    [ProtoContract]
    public class ChatMessage
    {
        [ProtoMember(1)] public string message;
        [ProtoMember(2)] public string color;
        [ProtoMember(3)] public long playerId;
        [ProtoMember(4)] public Color col;
    }

    [ProtoContract]
    public class CommsPackage
    {
        [ProtoMember(1)]
        public DataType Type;

        [ProtoMember(2)]
        public byte[] Data;

        public CommsPackage()
        {
            Type = DataType.Sync;
            Data = new byte[0];
        }

        public CommsPackage(DataType type, ObjectContainer oc)
        {
            Type = type;
            Data = MyAPIGateway.Utilities.SerializeToBinary(oc);
        }

        public CommsPackage(DataType type, ChatMessage cm)
        {
            Type = type;
            Data = MyAPIGateway.Utilities.SerializeToBinary(cm);
        }
    }

    public static class Comms
    {
        private static readonly ushort handler = Session.Instance.NetworkHandle;

        static bool IsServerBound(DataType type)
        {
            switch (type)
            {
                case DataType.StoreGrid:
                case DataType.FactionList:
                case DataType.PrivateList:
                case DataType.RequestGridRemoval:
                case DataType.FactionToPrivateTransfer:
                case DataType.PrivateToFactionTransfer:
                case DataType.RequestGridData:
                case DataType.SendObToSpawn:
                case DataType.RequestConfig:
                case DataType.ClientRequestTransfer:
                case DataType.SendChatMessage:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Server-bound packets are requests: the player is always the sender, never a packet field.
        /// Client-bound packets are only accepted from the server.
        /// </summary>
        public static void MessageHandler(ushort handlerId, byte[] data, ulong senderSteamId, bool fromServer)
        {
            try
            {
                var package = MyAPIGateway.Utilities.SerializeFromBinary<CommsPackage>(data);
                if (package == null) return;

                long senderId = 0;
                if (IsServerBound(package.Type))
                {
                    if (!Session.Instance.isServer) return;
                    senderId = MyAPIGateway.Players.TryGetIdentityId(senderSteamId);
                    if (senderId == 0) return;
                }
                else if (!fromServer && !(Session.Instance.isServer && senderSteamId == MyAPIGateway.Multiplayer.MyId))
                    return;

                if (package.Type == DataType.SendChatMessage)
                {
                    var chat = MyAPIGateway.Utilities.SerializeFromBinary<ChatMessage>(package.Data);
                    if (chat == null) return;

                    MyVisualScriptLogicProvider.SendChatMessageColored($"{chat.message}", chat.col, "[FactionHangar]", senderId, $"{chat.color}");
                    return;
                }

                var packet = MyAPIGateway.Utilities.SerializeFromBinary<ObjectContainer>(package.Data);
                if (packet == null) return;

                if (senderId != 0)
                {
                    packet.playerId = senderId;
                    packet.requesterId = senderId;
                    packet.steamId = senderSteamId;
                }

                // Server
                if (package.Type == DataType.StoreGrid)
                {
                    Utils.Storegrid(packet);
                    return;
                }

                switch (package.Type)
                {
                    // Server
                    case DataType.FactionList:
                        Utils.GetFactionList(packet);
                        return;

                    case DataType.PrivateList:
                        Utils.GetPrivateList(packet);
                        return;

                    case DataType.RequestGridRemoval:
                        Utils.TryHangarGridRemoval(packet);
                        return;

                    case DataType.FactionToPrivateTransfer:
                    case DataType.PrivateToFactionTransfer:
                    case DataType.ClientRequestTransfer:
                    {
                        IMyFaction faction = MyAPIGateway.Session.Factions.TryGetPlayerFaction(packet.playerId);
                        if (faction == null)
                        {
                            MyVisualScriptLogicProvider.SendChatMessageColored($"Need to be in a faction to transfer grids.", Color.Red, "[FactionHangar]", packet.playerId, "Red");
                            return;
                        }

                        bool toFaction = package.Type == DataType.PrivateToFactionTransfer
                            || (package.Type == DataType.ClientRequestTransfer && packet.hangarType == HangarType.Faction);
                        if (toFaction)
                            Session.Instance.allHangarData.TransferPrivateToFaction(faction.FactionId, packet.intValue, packet.playerId);
                        else
                            Session.Instance.allHangarData.TransferFactionToPrivate(faction.FactionId, packet.intValue, packet.playerId);
                        return;
                    }

                    case DataType.RequestGridData:
                        Utils.GetGridData(packet);
                        return;

                    case DataType.SendObToSpawn:
                        Session.Instance.HandleSpawnRequest(packet);
                        return;

                    case DataType.RequestConfig:
                        ServerSendConfig(Session.Instance.config, packet.steamId);
                        return;

                    // Client
                    case DataType.SendGridData:
                    {
                        MyObjectBuilder_CubeGrid[] cubeGridObs = Session.Instance.GetGridFromGridData(packet.ob, packet.intValue, packet.hangarType);
                        if (cubeGridObs == null)
                        {
                            MyVisualScriptLogicProvider.SendChatMessageColored($"Failed to get grids", Color.Red, "[FactionHangar]", MyAPIGateway.Session.Player.IdentityId, "Red");
                            return;
                        }

                        Session.Instance.previewMass = (float)packet.mass;
                        Session.Instance.spawnCostBypass = packet.boolValue;
                        Session.Instance.spawnGridId = packet.gridId;
                        Session.Instance.SpawnClientSideProjectedGrid(cubeGridObs);
                        return;
                    }

                    case DataType.SendConfig:
                        Session.Instance.config = packet.config;
                        Session.Instance.useInverseSpawnArea = false;
                        foreach (var area in packet.config.spawnAreas)
                        {
                            if (area.inverseArea)
                            {
                                Session.Instance.useInverseSpawnArea = true;
                                break;
                            }
                        }
                        return;

                    case DataType.AddClientCooldown:
                        if (packet.timerType == TimerType.StorageCooldown)
                            Session.Instance.storeTimer = packet.intValue;

                        if (packet.timerType == TimerType.RetrievalCooldown)
                            Session.Instance.retrievalTimer = packet.intValue;
                        return;

                    case DataType.UpdateIdentities:
                        Session.Instance.allIdentities = packet.identityObs;
                        return;
                }
            }
            catch (Exception ex)
            {
                MyLog.Default.WriteLineAndConsole($"[FactionHangar] - Network message error: {ex}");
            }
        }

        public static void ClientRequestStoreGrid(long requesterId, long playerId, List<GridData> gridData, string playerName, HangarType hangarType)
        {
            ObjectContainer oc = new ObjectContainer()
            {
                requesterId = requesterId,
                playerId = playerId,
                gridData = gridData,
                stringData = playerName,
                hangarType = hangarType
            };

            Session.Instance.gridListToStore.Clear();
            CommsPackage package = new CommsPackage(DataType.StoreGrid, oc);
            var sendData = MyAPIGateway.Utilities.SerializeToBinary(package);
            MyAPIGateway.Multiplayer.SendMessageToServer(handler, sendData);
        }

        public static void ClientRequestFactionList(long playerId, string playerName)
        {
            ObjectContainer oc = new ObjectContainer()
            {
                playerId = playerId,
                stringData = playerName,
            };

            CommsPackage package = new CommsPackage(DataType.FactionList, oc);
            var sendData = MyAPIGateway.Utilities.SerializeToBinary(package);
            MyAPIGateway.Multiplayer.SendMessageToServer(handler, sendData);
        }

        public static void ClientRequestPrivateList(long playerId, string playerName)
        {
            ObjectContainer oc = new ObjectContainer()
            {
                playerId = playerId,
                stringData = playerName
            };

            CommsPackage package = new CommsPackage(DataType.PrivateList, oc);
            var sendData = MyAPIGateway.Utilities.SerializeToBinary(package);
            MyAPIGateway.Multiplayer.SendMessageToServer(handler, sendData);
        }

        public static void ClientLoadGrid(int index, long playerId, HangarType hangarType)
        {
            ObjectContainer oc = new ObjectContainer()
            {
                intValue = index,
                playerId = playerId,
                hangarType = hangarType
            };

            CommsPackage package = new CommsPackage(DataType.LoadFactionGrid, oc);
            var sendData = MyAPIGateway.Utilities.SerializeToBinary(package);
            MyAPIGateway.Multiplayer.SendMessageToServer(handler, sendData);
        }

        public static void ClientRequestGridData(int index, long playerId, HangarType hangarType, ulong steamId, bool original, bool force)
        {
            ObjectContainer oc = new ObjectContainer()
            {
                intValue = index,
                playerId = playerId,
                hangarType = hangarType,
                steamId = steamId,
                originalLocation = original,
                force = force
            };

            CommsPackage package = new CommsPackage(DataType.RequestGridData, oc);
            var sendData = MyAPIGateway.Utilities.SerializeToBinary(package);
            MyAPIGateway.Multiplayer.SendMessageToServer(handler, sendData);
        }

        /// <summary>Preview data; mass and the cost bypass come from the server so the preview cost matches the charge.</summary>
        public static void SendOBToClient(MyObjectBuilder_Base data, ulong steamId, int index, HangarType hangarType, long gridId, double mass, bool costBypass)
        {
            ObjectContainer oc = new ObjectContainer()
            {
                ob = data,
                intValue = index,
                hangarType = hangarType,
                gridId = gridId,
                mass = mass,
                boolValue = costBypass
            };

            CommsPackage package = new CommsPackage(DataType.SendGridData, oc);
            var sendData = MyAPIGateway.Utilities.SerializeToBinary(package);
            MyAPIGateway.Multiplayer.SendMessageTo(handler, sendData, steamId);
        }

        /// <summary>Placement request: the server reloads the stored grids and only uses the main grid's placement.</summary>
        public static void SendGridsToSpawn(int index, HangarType hangarType, long gridId, MyPositionAndOrientation placement)
        {
            ObjectContainer oc = new ObjectContainer()
            {
                intValue = index,
                hangarType = hangarType,
                gridId = gridId,
                placement = placement
            };

            CommsPackage package = new CommsPackage(DataType.SendObToSpawn, oc);
            var sendData = MyAPIGateway.Utilities.SerializeToBinary(package);
            MyAPIGateway.Multiplayer.SendMessageToServer(handler, sendData);
        }

        public static void ClientRequestConfig(ulong steamId)
        {
            ObjectContainer oc = new ObjectContainer()
            {
                steamId = steamId
            };

            CommsPackage package = new CommsPackage(DataType.RequestConfig, oc);
            var sendData = MyAPIGateway.Utilities.SerializeToBinary(package);
            MyAPIGateway.Multiplayer.SendMessageToServer(handler, sendData);
        }

        public static void ServerSendConfig(Config config, ulong steamId)
        {
            ObjectContainer oc = new ObjectContainer()
            {
                config = config
            };

            CommsPackage package = new CommsPackage(DataType.SendConfig, oc);
            var sendData = MyAPIGateway.Utilities.SerializeToBinary(package);
            MyAPIGateway.Multiplayer.SendMessageTo(handler, sendData, steamId);
        }

        public static void ClientRequestTransfer(int index, long playerId, HangarType hangarType)
        {
            ObjectContainer oc = new ObjectContainer()
            {
                intValue = index,
                playerId = playerId,
                hangarType = hangarType
            };

            CommsPackage package = new CommsPackage(DataType.ClientRequestTransfer, oc);
            var sendData = MyAPIGateway.Utilities.SerializeToBinary(package);
            MyAPIGateway.Multiplayer.SendMessageToServer(handler, sendData);
        }

        public static void AddClientCooldown(ulong steamId, bool privateStorage, TimerType type)
        {
            if (steamId == 0) return;
            int cooldown = 0;
            if (type == TimerType.StorageCooldown)
            {
                if (privateStorage)
                    cooldown = Session.Instance.config.privateHangarConfig.privateHangarCooldown;
                else
                    cooldown = Session.Instance.config.factionHangarConfig.factionHangarCooldown;
            }

            if (type == TimerType.RetrievalCooldown)
            {
                if (privateStorage)
                    cooldown = Session.Instance.config.privateHangarConfig.privateRetrievalCooldown;
                else
                    cooldown = Session.Instance.config.factionHangarConfig.factionRetrievalCooldown;
            }

            ObjectContainer oc = new ObjectContainer()
            {
                intValue = cooldown,
                timerType = type
            };

            CommsPackage package = new CommsPackage(DataType.AddClientCooldown, oc);
            var sendData = MyAPIGateway.Utilities.SerializeToBinary(package);
            MyAPIGateway.Multiplayer.SendMessageTo(handler, sendData, steamId);
        }

        public static void SendIdentitiesToClients(List<MyObjectBuilder_Identity> list)
        {
            if (list == null)
                return;

            ObjectContainer oc = new ObjectContainer()
            {
                identityObs = new List<MyObjectBuilder_Identity>(list)
            };

            CommsPackage package = new CommsPackage(DataType.UpdateIdentities, oc);
            var sendData = MyAPIGateway.Utilities.SerializeToBinary(package);
            MyAPIGateway.Multiplayer.SendMessageToOthers(handler, sendData);
        }

        public static void RequestGridRemoval(int index, long playerId, HangarType hangarType)
        {
            ObjectContainer oc = new ObjectContainer()
            {
                intValue = index,
                playerId = playerId,
                hangarType = hangarType
            };

            CommsPackage package = new CommsPackage(DataType.RequestGridRemoval, oc);
            var sendData = MyAPIGateway.Utilities.SerializeToBinary(package);
            MyAPIGateway.Multiplayer.SendMessageToServer(handler, sendData);
        }

        public static void RequestTransferFactionToPrivate(long playerId, int index)
        {
            ObjectContainer oc = new ObjectContainer()
            {
                intValue = index,
                playerId = playerId
            };

            CommsPackage package = new CommsPackage(DataType.FactionToPrivateTransfer, oc);
            var sendData = MyAPIGateway.Utilities.SerializeToBinary(package);
            MyAPIGateway.Multiplayer.SendMessageToServer(handler, sendData);
        }

        public static void RequestTransferPrivateToFaction(long playerId, int index)
        {
            ObjectContainer oc = new ObjectContainer()
            {
                intValue = index,
                playerId = playerId
            };

            CommsPackage package = new CommsPackage(DataType.PrivateToFactionTransfer, oc);
            var sendData = MyAPIGateway.Utilities.SerializeToBinary(package);
            MyAPIGateway.Multiplayer.SendMessageToServer(handler, sendData);
        }

        public static void SendChatMessage(string message, string color, long playerId, Color col)
        {
            ChatMessage cm = new ChatMessage()
            {
                message = message,
                color = color,
                playerId = playerId,
                col = col
            };

            CommsPackage package = new CommsPackage(DataType.SendChatMessage, cm);
            var sendData = MyAPIGateway.Utilities.SerializeToBinary(package);
            MyAPIGateway.Multiplayer.SendMessageToServer(handler, sendData);
        }
    }
}
