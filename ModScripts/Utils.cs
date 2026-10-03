using EmptyKeys.UserInterface.Generated.StoreBlockView_Bindings;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.Weapons;
using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Game.ObjectBuilders.Definitions;
using VRage.ObjectBuilders;
using VRage.Utils;
using VRageMath;
using VRage.Game.ModAPI.Ingame;
using Sandbox.Engine.Utils;
using VRage.Compiler;
using VRage.Game.ObjectBuilders.Definitions.SessionComponents;
using ProtoBuf.Meta;
using EmptyKeys.UserInterface.Generated;
using VRage.Scripting;
using System.Net.Sockets;

namespace CustomHangar
{
    public static class Utils
    {
        public static string GetGridsToStore(List<MyEntity> entities, HangarType hangarType, long playerId)
        {
            IMyFaction faction = MyAPIGateway.Session.Factions.TryGetPlayerFaction(playerId);
            bool isLeader = false;
            if (faction != null)
                isLeader = faction.IsLeader(playerId);

            Session.Instance.gridListToStore.Clear();
            int index = 0;
            string gridNames = "Choose grid by index, \nPotential Grids To Store:\n";

            foreach(var entity in entities)
            {
                VRage.Game.ModAPI.IMyCubeGrid grid = entity as VRage.Game.ModAPI.IMyCubeGrid;
                MyCubeGrid cubeGrid = entity as MyCubeGrid;
                if (grid == null || grid.Physics == null || cubeGrid.BlocksCount < 2) continue;

                if (faction != null)
                {
                    if (Session.Instance.DoesFactionOwnGrid(grid, faction))
                    {
                        if (!isLeader)
                        {
                            if (Session.Instance.DoesPlayerOwnGrid(grid, playerId))
                            {
                                gridNames += $"[{index}] {grid.CustomName},\n";
                                Session.Instance.gridListToStore.Add(grid);
                                index++;
                            }
                        }
                        else
                        {
                            gridNames += $"[{index}] {grid.CustomName},\n";
                            Session.Instance.gridListToStore.Add(grid);
                            index++;
                        }
                    } 
                }
                else
                {
                    if (Session.Instance.DoesPlayerOwnGrid(grid, playerId))
                    {
                        gridNames += $"[{index}] {grid.CustomName},\n";
                        Session.Instance.gridListToStore.Add(grid);
                        index++;
                    }
                }
                    
            }

            return gridNames;
        }

        public static void CheckOwnerValidFaction(IMyFaction faction, MyCubeGrid grid, long playerId)
        {
            if (faction == null) return;
            if (faction.IsMember(playerId)) return;

            grid.ChangeGridOwner(playerId, MyOwnershipShareModeEnum.Faction);
        }

        /// <summary>
        /// GVK: true if the point is inside an enabled safezone whose block is owned by the player's faction,
        /// or whose faction whitelist includes it. Always false when the config toggle is off.
        /// </summary>
        public static bool IsInOwnFactionSafeZone(Vector3D point, long playerId)
        {
            if (Session.Instance.config == null || !Session.Instance.config.freeSpawnInOwnFactionSafeZone) return false;
            IMyFaction faction = MyAPIGateway.Session.Factions.TryGetPlayerFaction(playerId);
            if (faction == null) return false;

            foreach (MySafeZone zone in MySessionComponentSafeZones.SafeZones)
            {
                if (zone == null || zone.MarkedForClose || !zone.Enabled) continue;
                if (!IsInsideSafeZone(zone, point)) continue;

                MyEntity blockEnt;
                if (zone.SafeZoneBlockId != 0 && MyEntities.TryGetEntityById(zone.SafeZoneBlockId, out blockEnt))
                {
                    var block = blockEnt as VRage.Game.ModAPI.IMyCubeBlock;
                    IMyFaction ownerFaction = block != null && block.OwnerId != 0 ? MyAPIGateway.Session.Factions.TryGetPlayerFaction(block.OwnerId) : null;
                    if (ownerFaction != null && ownerFaction.FactionId == faction.FactionId) return true;
                }

                if (zone.AccessTypeFactions == MySafeZoneAccess.Whitelist)
                {
                    var zoneOb = zone.GetObjectBuilder() as MyObjectBuilder_SafeZone;
                    if (zoneOb != null && zoneOb.Factions != null && Array.IndexOf(zoneOb.Factions, faction.FactionId) >= 0) return true;
                }
            }

            return false;
        }

        private static bool IsInsideSafeZone(MySafeZone zone, Vector3D point)
        {
            if (zone.Shape == MySafeZoneShape.Sphere)
                return Vector3D.DistanceSquared(zone.PositionComp.GetPosition(), point) <= (double)zone.Radius * zone.Radius;

            var obb = new MyOrientedBoundingBoxD(zone.PositionComp.LocalAABB, zone.WorldMatrix);
            return obb.Contains(ref point);
        }

        public static bool IsGridIntersecting(List<MyCubeGrid> grids)
        {
            if (grids == null || grids.Count == 0) return false;
            /*List<MyEntity> ents = new List<MyEntity>();
            var bb = grid.PositionComp.WorldVolume.GetBoundingBox();
            MyGamePruningStructure.GetAllEntitiesInBox(ref bb, ents);
            return ents.Count != 0;*/

            /*var aabb = grid.PositionComp.WorldAABB;
            bool result = grid.GetIntersectionWithAABB(ref aabb);
            return result;*/
            var settings = new MyGridPlacementSettings();
            settings.VoxelPlacement = new VoxelPlacementSettings()
            {
                PlacementMode = VoxelPlacementMode.OutsideVoxel
            };

            foreach (var grid in grids)
            {
                bool isStatic = grid.GridSizeEnum == MyCubeSize.Large ? true : Session.Instance.config.spawnSGStatic;
                var canPlace = MyCubeGrid.TestPlacementArea(grid, isStatic, ref settings, grid.PositionComp.LocalAABB, false, null, !isStatic, true);
                if (canPlace) continue;
                return true;
            }

            return false;
        }

        public static bool CheckForExcludedBlock(MyCubeGrid grid)
        {
            var blocks = grid.GetFatBlocks();
            foreach(var block in blocks)
            {
                VRage.Game.ModAPI.IMyCubeBlock cubeBlock = block as VRage.Game.ModAPI.IMyCubeBlock;
                long owner = block.OwnerId;
                if (owner == 0) continue;

                MyDefinitionId blockDef = cubeBlock.BlockDefinition;
                foreach(var def in Session.Instance.config.autoHangarConfig.exclusions.excludedBlockTypes)
                {
                    foreach(var subtype in def.blockSubtypes.subtype)
                    {
                        MyDefinitionId id;
                        MyDefinitionId.TryParse(def.blockType, subtype, out id);
                        if (id != null && id == blockDef) return true;
                    }
                }
            }

            return false;
        }

        public static void LoadHelpPopup()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("*** Faction leaders can execute commands on any faction grid while members can only execute commands on their own grids in the faction hangar. ***\n\n");
            sb.Append("/fh help or /ph help - Shows this list. /factionhangar and /privatehangar also work.\n");
            sb.Append("load, transfer or remove without an index shows the hangar list.\n\n");
            sb.Append("/fh list - Displays a list of grids and their index currently in your faction hangar.\n");
            sb.Append("/fh store - Displays a list of possible grids you can store to your faction hangar in range.\n");
            sb.Append("/fh store [index#] - Attempts to store the desired grid to your faction hangar.\n");
            sb.Append("/fh load [index#] - Attempts to load the desired grid at index from faction hangar.\n");
            sb.Append("/fh load [index#].true - Attempts to load the desired grid at index to its original location.\n");
            sb.Append("/fh load [index#].true.force - Attempts to load the desired grid at index but forces spawning at original location (no collision checks).\n");
            sb.Append("/fh transfer [index#] - Transfers the desired grid at index over to your private hangar.\n");
            sb.Append("/fh remove [index#] - Attempts to delete the desired grid at index from faction hangar.\n");
            sb.Append("/fh togglesphere - Toggles the visble debugging sphere that shows the 'spawnable' areas.\n");
            sb.Append("/fh togglegps - Toggles the GPS markers for the 'spawnable' areas.\n");
            sb.Append("/ph list - Displays a list of grids and their index currently in your private hangar.\n");
            sb.Append("/ph store - Displays a list of possible grids you can store to private hangar in range.\n");
            sb.Append("/ph store [index#] - Attempts to store the desired grid to your private hangar.\n");
            sb.Append("/ph load [index#] - Attempts to load the desired grid at index from private hangar.\n");
            sb.Append("/ph load [index#].true - Attempts to load the desired grid at index to its original location.\n");
            sb.Append("/ph load [index#].true.force - Attempts to load the desired grid at index but forces spawning at original location (no collision checks).\n");
            sb.Append("/ph transfer [index#] - Transfers the desired grid at index over to your faction hangar.\n");
            sb.Append("/ph remove [index#] - Attempts to delete the desired grid at index from private hangar.\n");

            MyAPIGateway.Utilities.ShowMissionScreen("Faction Hangar Command List", "", null, sb.ToString(), null, "Ok");

        }

        /// <summary>Same radius the client uses to list grids it can store (plus the grid's own size).</summary>
        const double StoreRange = 500.0;

        public static void Reject(long playerId, string message)
        {
            MyVisualScriptLogicProvider.SendChatMessageColored(message, Color.Red, "[FactionHangar]", playerId, "Red");
        }

        /// <summary>Seconds left on a faction cooldown, 0 if none.</summary>
        public static int FactionCooldownLeft(IMyFaction faction, TimerType type)
        {
            FactionTimers timers;
            if (faction == null || !Session.Instance.cooldownTimers.TryGetValue(faction, out timers)) return 0;
            foreach (var timer in timers.timers)
                if (timer.type == type) return Math.Max(timer.time, 1);

            return 0;
        }

        /// <summary>Seconds left on a private hangar cooldown, 0 if none.</summary>
        public static int PrivateCooldownLeft(Dictionary<long, int> cooldownEnds, long playerId)
        {
            int end;
            if (!cooldownEnds.TryGetValue(playerId, out end)) return 0;
            int ticksLeft = end - Session.Instance.ticks;
            if (ticksLeft <= 0)
            {
                cooldownEnds.Remove(playerId);
                return 0;
            }

            return (ticksLeft + 59) / 60;
        }

        /// <summary>Ownership rules for storing: private = own grid; faction = faction grid, and members only their own.</summary>
        public static bool CanStoreGrid(VRage.Game.ModAPI.IMyCubeGrid grid, long requesterId, HangarType hangarType, out string reason)
        {
            var session = Session.Instance;
            reason = null;
            if (hangarType == HangarType.Private)
            {
                if (session.DoesPlayerOwnGrid(grid, requesterId)) return true;
                reason = $"Grid {grid.CustomName} is not owned by you.";
                return false;
            }

            IMyFaction faction = MyAPIGateway.Session.Factions.TryGetPlayerFaction(requesterId);
            if (faction == null)
            {
                reason = "Need to be in a faction to store a grid in faction hangar.";
                return false;
            }

            if (!session.DoesFactionOwnGrid(grid, faction))
            {
                reason = $"Grid {grid.CustomName} is not owned by you or your faction";
                return false;
            }

            if (!faction.IsLeader(requesterId) && !session.DoesPlayerOwnGrid(grid, requesterId))
            {
                reason = $"Grid {grid.CustomName} is not owned by you, must be a faction leader to store grids that you don't own";
                return false;
            }

            return true;
        }

        static bool IsQueuedForStorage(long gridId)
        {
            foreach (var delay in Session.Instance.hangarDelay)
                foreach (var data in delay.gridData)
                    if (data.gridId == gridId) return true;

            return false;
        }

        /// <summary>
        /// Server: queues a store request. The requester is the packet sender; each grid is re-checked here
        /// (exists, near the requester, owned, no enemies, not already queued) and the owner and names come
        /// from the server, never from the client.
        /// </summary>
        public static void Storegrid(ObjectContainer packet)
        {
            var session = Session.Instance;
            var config = session.config;
            long requesterId = packet.requesterId;
            HangarType hangarType = packet.hangarType;
            IMyPlayer player = session.GetPlayerfromID(requesterId);
            if (player == null || player.Character == null || packet.gridData == null || packet.gridData.Count == 0) return;

            IMyFaction faction = MyAPIGateway.Session.Factions.TryGetPlayerFaction(requesterId);
            int slots;
            int maxSlots;
            if (hangarType == HangarType.Faction)
            {
                if (faction == null)
                {
                    Reject(requesterId, "Need to be in a faction to store a grid in faction hangar.");
                    return;
                }

                int cooldown = FactionCooldownLeft(faction, TimerType.StorageCooldown);
                if (cooldown > 0)
                {
                    Reject(requesterId, $"Must wait {cooldown} seconds before your faction can store.");
                    return;
                }

                slots = session.allHangarData.GetFactionSlots(faction.FactionId);
                maxSlots = config.factionHangarConfig.maxFactionSlots;
                if (slots >= maxSlots)
                {
                    Reject(requesterId, "Your faction has exceeded the amount of stored grids.");
                    return;
                }
            }
            else
            {
                int cooldown = PrivateCooldownLeft(session.privateStoreCooldownEnd, requesterId);
                if (cooldown > 0)
                {
                    Reject(requesterId, $"Must wait {cooldown} seconds before you can store another grid.");
                    return;
                }

                slots = session.allHangarData.GetPrivateSlots(requesterId);
                maxSlots = config.privateHangarConfig.maxPrivateSlots;
                if (slots >= maxSlots)
                {
                    Reject(requesterId, $"You have exceeded the {maxSlots} max amount of stored grids in private hangar.");
                    return;
                }
            }

            if (slots + packet.gridData.Count > maxSlots)
            {
                Reject(requesterId, $"Storing {packet.gridData.Count} connected grids will exceed your hangar slots of {maxSlots}. Please remove connected grids.");
                return;
            }

            Vector3D playerPos = player.GetPosition();
            var grids = new List<VRage.Game.ModAPI.IMyCubeGrid>(packet.gridData.Count);
            foreach (var data in packet.gridData)
            {
                VRage.ModAPI.IMyEntity ent;
                var grid = MyAPIGateway.Entities.TryGetEntityById(data.gridId, out ent) ? ent as VRage.Game.ModAPI.IMyCubeGrid : null;
                if (grid == null || grid.MarkedForClose || grid.Physics == null)
                {
                    Reject(requesterId, "Failed to store grid, it no longer exists.");
                    return;
                }

                if (grids.Contains(grid) || IsQueuedForStorage(grid.EntityId))
                {
                    Reject(requesterId, $"Grid {grid.CustomName} is already being stored.");
                    return;
                }

                double range = StoreRange + grid.WorldVolume.Radius;
                if (Vector3D.DistanceSquared(grid.GetPosition(), playerPos) > range * range)
                {
                    Reject(requesterId, $"Grid {grid.CustomName} is too far away to store.");
                    return;
                }

                string reason;
                if (!CanStoreGrid(grid, requesterId, hangarType, out reason))
                {
                    Reject(requesterId, reason);
                    return;
                }

                if (session.IsEnemyNearStoring(grid, hangarType, requesterId))
                {
                    Reject(requesterId, "Cannot store when enemy is nearby.");
                    return;
                }

                data.gridName = grid.CustomName;
                grids.Add(grid);
            }

            long ownerId = requesterId;
            if (hangarType == HangarType.Faction && grids[0].BigOwners.Count > 0)
                ownerId = grids[0].BigOwners[0];

            string ownerName = session.GetPlayerName(ownerId);
            if (string.IsNullOrEmpty(ownerName))
                ownerName = MyVisualScriptLogicProvider.GetPlayersName(ownerId) ?? "";

            int delaySeconds = hangarType == HangarType.Faction ? config.factionHangarConfig.factionStoreDelay : config.privateHangarConfig.privateStoreDelay;
            HangarDelayData hangarDelayData = new HangarDelayData()
            {
                playerId = ownerId,
                gridData = packet.gridData,
                playerName = ownerName,
                hangarType = hangarType,
                requesterId = requesterId
            };

            foreach (var grid in grids)
            {
                MyCubeGrid cubeGrid = grid as MyCubeGrid;
                if (cubeGrid != null)
                    cubeGrid.OnGridBlockDamaged += GridDamageMonitor;
            }

            session.hangarDelay.Add(hangarDelayData);
            foreach (var grid in packet.gridData)
                MyVisualScriptLogicProvider.SendChatMessageColored($"Storing Grid {grid.gridName} in {delaySeconds} seconds", Color.Green, "[FactionHangar]", requesterId, "Green");

            bool privateStorage = hangarType == HangarType.Private;
            if (privateStorage)
                session.privateStoreCooldownEnd[requesterId] = session.ticks + config.privateHangarConfig.privateHangarCooldown * 60;
            else
                FactionTimers.AddTimer(faction, TimerType.StorageCooldown, config.factionHangarConfig.factionHangarCooldown);

            Comms.AddClientCooldown(player.SteamUserId, privateStorage, TimerType.StorageCooldown);
        }

        public static void GetFactionList(ObjectContainer packet)
        {
            IMyFaction faction = MyAPIGateway.Session.Factions.TryGetPlayerFaction(packet.playerId);
            if (faction != null)
            {
                bool isLeader = faction.IsLeader(packet.playerId);
                string gridNames = Session.Instance.allHangarData.GetFactionsGridNames(faction.FactionId, packet.playerId, isLeader);
                if (string.IsNullOrEmpty(gridNames))
                {
                    MyVisualScriptLogicProvider.SendChatMessageColored($"Your faction does not have any stored grids.", Color.Red, "[FactionHangar]", packet.playerId, "Red");
                    return;
                }

                if (!isLeader)
                    MyVisualScriptLogicProvider.SendChatMessageColored($"You are not a faction leader and can ONLY access grids owned by you.", Color.Orange, "[FactionHangar]", packet.playerId, "Green");

                MyVisualScriptLogicProvider.SendChatMessageColored($"{gridNames}", Color.Green, "[FactionHangar]", packet.playerId, "Green");
                return;
            }
        }

        public static void GetPrivateList(ObjectContainer packet)
        {
            string gridNames = Session.Instance.allHangarData.GetPrivateGridNames(packet.playerId);
            if (string.IsNullOrEmpty(gridNames))
            {
                MyVisualScriptLogicProvider.SendChatMessageColored($"Your private hangar does not have any stored grids.", Color.Red, "[FactionHangar]", packet.playerId, "Red");
                return;
            }

            MyVisualScriptLogicProvider.SendChatMessageColored($"{gridNames}", Color.Green, "[FactionHangar]", packet.playerId, "Green");
            return;
        }

        public static bool TryHangarGridRemoval(ObjectContainer packet)
        {
            if (packet.hangarType == HangarType.Faction)
            {
                IMyFaction faction = MyAPIGateway.Session.Factions.TryGetPlayerFaction(packet.playerId);
                if (faction == null)
                {
                    Reject(packet.playerId, "Need to be in a faction to remove grids from hangar.");
                    return false;
                }

                bool isLeader = faction.IsLeader(packet.playerId);
                var gridData = Session.Instance.allHangarData.GetFactionGridData(faction.FactionId, packet.intValue);
                if (gridData == null)
                {
                    Reject(packet.playerId, $"Grid index {packet.intValue} is invalid");
                    return false;
                }

                if (!isLeader)
                {
                    if (gridData.owner != packet.playerId)
                    {
                        Reject(packet.playerId, "You are not a faction leader and can ONLY remove grids owned by you.");
                        return false;
                    }
                }

                Session.Instance.allHangarData.RemoveFactionData(faction.FactionId, packet.intValue, !gridData.fileMissing);
                MyVisualScriptLogicProvider.SendChatMessageColored($"Removed [{packet.intValue}] {gridData.gridName} from the faction hangar.", Color.Green, "[FactionHangar]", packet.playerId, "Green");
                return true;
            }

            if (packet.hangarType == HangarType.Private)
            {
                var gridData = Session.Instance.allHangarData.GetPrivateGridData(packet.playerId, packet.intValue);
                if (gridData == null)
                {
                    Reject(packet.playerId, $"Grid index {packet.intValue} is invalid");
                    return false;
                }

                Session.Instance.allHangarData.RemovePrivateData(packet.playerId, packet.intValue, !gridData.fileMissing);
                MyVisualScriptLogicProvider.SendChatMessageColored($"Removed [{packet.intValue}] {gridData.gridName} from your private hangar.", Color.Green, "[FactionHangar]", packet.playerId, "Green");
                return true;
            }

            return false;
        }

        /// <summary>Server: access, cooldown and slot checks for loading a grid, then the stored blueprint.</summary>
        public static bool TryGetRetrievableGrid(long playerId, HangarType hangarType, int index, out GridData gridData, out MyObjectBuilder_Definitions blueprint)
        {
            var session = Session.Instance;
            gridData = null;
            blueprint = null;

            if (hangarType == HangarType.Faction)
            {
                IMyFaction faction = MyAPIGateway.Session.Factions.TryGetPlayerFaction(playerId);
                if (faction == null)
                {
                    Reject(playerId, "Need to be in a faction to load a grid in faction hangar.");
                    return false;
                }

                int cooldown = FactionCooldownLeft(faction, TimerType.RetrievalCooldown);
                if (cooldown > 0)
                {
                    Reject(playerId, $"Must wait {cooldown} seconds before your faction can load another grid.");
                    return false;
                }

                gridData = session.allHangarData.GetFactionGridData(faction.FactionId, index);
                if (gridData == null)
                {
                    Reject(playerId, $"Grid index {index} is invalid");
                    return false;
                }

                if (!faction.IsLeader(playerId) && gridData.owner != playerId)
                {
                    Reject(playerId, "You are not a faction leader and can ONLY access grids owned by you.");
                    return false;
                }
            }
            else
            {
                int cooldown = PrivateCooldownLeft(session.privateRetrievalCooldownEnd, playerId);
                if (cooldown > 0)
                {
                    Reject(playerId, $"Must wait {cooldown} seconds before you can load another grid.");
                    return false;
                }

                gridData = session.allHangarData.GetPrivateGridData(playerId, index);
                if (gridData == null)
                {
                    Reject(playerId, $"Grid index {index} is invalid");
                    return false;
                }
            }

            blueprint = LoadStoredBlueprint(gridData.gridPath);
            if (GetBlueprintGrids(blueprint) == null)
            {
                gridData.fileMissing = true;
                string prefix = hangarType == HangarType.Faction ? "/fh" : "/ph";
                Reject(playerId, $"The stored file for grid [{index}] {gridData.gridName} is missing or damaged. Use {prefix} remove {index} to clear it.");
                return false;
            }

            return true;
        }

        const string SavesFolder = "FactionHangarSaves";

        /// <summary>
        /// Stored blueprint path re-rooted on the current user-data folder, so entries survive a server
        /// instance move (mods may only touch files under it). Null if the path has no saves folder.
        /// </summary>
        public static string ResolveGridPath(string storedPath)
        {
            if (string.IsNullOrEmpty(storedPath)) return null;
            string path = storedPath.Replace('/', '\\');
            int start = path.IndexOf("\\" + SavesFolder + "\\", StringComparison.OrdinalIgnoreCase);
            if (start >= 0)
                path = path.Substring(start + 1);
            else if (!path.StartsWith(SavesFolder + "\\", StringComparison.OrdinalIgnoreCase))
                return null;

            return Path.Combine(MyAPIGateway.Utilities.GamePaths.UserDataPath, path);
        }

        /// <summary>Stored blueprint, or null if the file is missing, damaged or unreachable.</summary>
        public static MyObjectBuilder_Definitions LoadStoredBlueprint(string storedPath)
        {
            string path = ResolveGridPath(storedPath);
            if (path == null) return null;

            MyObjectBuilder_Definitions blueprint;
            try
            {
                MyObjectBuilderSerializer.DeserializeXML(path, out blueprint);
            }
            catch (Exception ex)
            {
                MyLog.Default.WriteLineAndConsole($"[FactionHangar] - Could not read stored grid {path}: {ex.Message}");
                return null;
            }

            return blueprint;
        }

        public static MyObjectBuilder_CubeGrid[] GetBlueprintGrids(MyObjectBuilder_Definitions blueprint)
        {
            if (blueprint?.ShipBlueprints == null || blueprint.ShipBlueprints.Length == 0) return null;
            MyObjectBuilder_ShipBlueprintDefinition bpDef = blueprint.ShipBlueprints[0];
            if (bpDef?.CubeGrids == null || bpDef.CubeGrids.Length == 0) return null;
            return bpDef.CubeGrids;
        }

        /// <summary>Server: original-location spawn, or send the preview data (with server-side mass) to the client.</summary>
        public static void GetGridData(ObjectContainer packet)
        {
            var session = Session.Instance;
            GridData gridData;
            MyObjectBuilder_Definitions ob;
            if (!TryGetRetrievableGrid(packet.playerId, packet.hangarType, packet.intValue, out gridData, out ob)) return;

            MyObjectBuilder_CubeGrid[] cubeGridObs = GetBlueprintGrids(ob);
            if (cubeGridObs == null || !cubeGridObs[0].PositionAndOrientation.HasValue)
            {
                Reject(packet.playerId, "Failed to load the stored grid.");
                return;
            }

            if (packet.originalLocation)
            {
                Vector3D originalPos = cubeGridObs[0].PositionAndOrientation.Value.Position;
                if (!IsInOwnFactionSafeZone(originalPos, packet.playerId) && SpawnRules.IsEnemyNear(originalPos, session.config.spawnOriginalConfig.originalEnemyCheck, packet.playerId))
                {
                    Reject(packet.playerId, "Enemy nearby, failed to spawn from hangar.");
                    return;
                }

                session.SpawnGridsFromOb(new List<MyObjectBuilder_CubeGrid>(cubeGridObs), packet.intValue, packet.hangarType, packet.playerId, 0, SpawnType.Original, !packet.force);
                return;
            }

            double mass = SpawnRules.GetBlueprintMass(cubeGridObs);
            bool costBypass = gridData.autoHangared && session.config.autoHangarConfig.autoBypassSpawnCost;
            Comms.SendOBToClient(ob, packet.steamId, packet.intValue, packet.hangarType, gridData.gridId, mass, costBypass);
        }

        public static void CreateNullShipBlueprint(string path)
        {
            //List<MyObjectBuilder_CubeGrid> list = GetGridGroupObs(myCubeGrid, groupType);
            MyObjectBuilder_ShipBlueprintDefinition myObjectBuilder_ShipBlueprintDefinition = MyObjectBuilderSerializer.CreateNewObject<MyObjectBuilder_ShipBlueprintDefinition>();
            //myObjectBuilder_ShipBlueprintDefinition.Id = new MyDefinitionId(new MyObjectBuilderType(typeof(MyObjectBuilder_ShipBlueprintDefinition)), MyUtils.StripInvalidChars(blueprintName));
            //myObjectBuilder_ShipBlueprintDefinition.CubeGrids = list.ToArray();
            //myObjectBuilder_ShipBlueprintDefinition.RespawnShip = false;
            //myObjectBuilder_ShipBlueprintDefinition.DisplayName = blueprintName;
            //myObjectBuilder_ShipBlueprintDefinition.CubeGrids[0].DisplayName = blueprintName;
            MyObjectBuilder_Definitions myObjectBuilder_Definitions = MyObjectBuilderSerializer.CreateNewObject<MyObjectBuilder_Definitions>();
            myObjectBuilder_Definitions.ShipBlueprints = new MyObjectBuilder_ShipBlueprintDefinition[1];
            myObjectBuilder_Definitions.ShipBlueprints[0] = myObjectBuilder_ShipBlueprintDefinition;
            string resolved = ResolveGridPath(path);
            if (resolved != null)
                MyObjectBuilderSerializer.SerializeXML(resolved, false, myObjectBuilder_Definitions);
        }

        /// <summary>
        /// Charges a spawn. Faction hangar: faction wallet if it can pay, otherwise the player's.
        /// Private hangar: the player's wallet only (same rule as SpawnRules.CanAfford).
        /// </summary>
        public static void UpdateBalance(long playerId, long amount, int index, HangarType hangarType)
        {
            var session = Session.Instance;
            if (!session.isServer || amount <= 0) return;

            IMyFaction faction = MyAPIGateway.Session.Factions.TryGetPlayerFaction(playerId);
            GridData data;
            if (hangarType == HangarType.Faction)
                data = faction != null ? session.allHangarData.GetFactionGridData(faction.FactionId, index) : null;
            else
                data = session.allHangarData.GetPrivateGridData(playerId, index);

            if (data != null && data.autoHangared && session.config.autoHangarConfig.autoBypassSpawnCost)
                return;

            long current;
            if (hangarType == HangarType.Faction && faction != null && faction.TryGetBalanceInfo(out current) && amount <= current)
            {
                faction.RequestChangeBalance(-amount);
                return;
            }

            IMyPlayer player = session.GetPlayerfromID(playerId);
            if (player != null && player.TryGetBalanceInfo(out current) && amount <= current)
                player.RequestChangeBalance(-amount);
        }

        public static void AddSpawnLocationsClientGPS()
        {
            if (!Session.Instance.spawnClientGPS) return;
            foreach (var area in Session.Instance.config.spawnAreas)
            {
                if (!area.enableSpawnArea) continue;
                if (Session.Instance.useInverseSpawnArea)
                {
                    IMyGps gps = MyAPIGateway.Session.GPS.Create($"Non-{SpawnType.SpawnArea} - {area.areaRadius}m", "", area.areaCenter, true);
                    gps.GPSColor = Color.SeaGreen;
                    MyAPIGateway.Session.GPS.AddLocalGps(gps);
                    Session.Instance.clientSpawnLocations.Add(gps);
                }
                else
                {
                    IMyGps gps = MyAPIGateway.Session.GPS.Create($"{SpawnType.SpawnArea} - {area.areaRadius}m", "", area.areaCenter, true);
                    gps.GPSColor = Color.SeaGreen;
                    MyAPIGateway.Session.GPS.AddLocalGps(gps);
                    Session.Instance.clientSpawnLocations.Add(gps);
                }
            }

            if (Session.Instance.config.spawnNearbyConfig.allowSpawnNearby)
            {
                IMyGps gps = MyAPIGateway.Session.GPS.Create($"{SpawnType.Nearby} - {Session.Instance.config.spawnNearbyConfig.nearbyRadius}m", "", Session.Instance.original, true);
                gps.GPSColor = Color.Orange;
                MyAPIGateway.Session.GPS.AddLocalGps(gps);
                Session.Instance.clientSpawnLocations.Add(gps);
            }
        }

        public static void RemoveSpawnLocationsClientGPS()
        {
            foreach(var gps in Session.Instance.clientSpawnLocations)
                MyAPIGateway.Session.GPS.RemoveLocalGps(gps);

            Session.Instance.clientSpawnLocations.Clear();
        }

        public static void CheckGridSpawnLimits(VRage.Game.ModAPI.IMyCubeGrid grid, IMyFaction faction, HangarType hangarType, int index, SpawnType spawnType, long playerId)
        {
            if (hangarType == HangarType.Faction)
            {
                if (faction != null)
                {
                    if (Session.Instance.allHangarData.IsFactionGridAutoHangared(faction.FactionId, index))
                        if (Session.Instance.config.autoHangarConfig.autoBypassSpawnLimits) return;

                    if (BypassLimits(spawnType, grid.GetPosition())) return;
                }
            }

            if (hangarType == HangarType.Private)
            {
                if (Session.Instance.allHangarData.IsPrivateGridAutoHangared(playerId, index))
                    if (Session.Instance.config.autoHangarConfig.autoBypassSpawnLimits) return;

                if (BypassLimits(spawnType, grid.GetPosition())) return;
            }

            List<VRage.Game.ModAPI.IMyCubeGrid> connectedGrids = new List<VRage.Game.ModAPI.IMyCubeGrid>();
            Session.Instance.GetGroupByType(grid, connectedGrids, GridLinkTypeEnum.Physical);

            bool removeAmmo = Session.Instance.config.spawnConfig.removeAmmo;
            bool removeUranium = Session.Instance.config.spawnConfig.removeUranium;
            bool removeIce = Session.Instance.config.spawnConfig.removeIce;

            foreach(var connectedGrid in connectedGrids)
            {
                var blocks = connectedGrid.GetFatBlocks<VRage.Game.ModAPI.IMyCubeBlock>();
                foreach(var block in blocks)
                {
                    if (!block.HasInventory) continue;
                    if (removeAmmo || removeUranium || removeIce)
                    {
                        var invList = new List<VRage.Game.ModAPI.Ingame.MyInventoryItem>();
                        VRage.Game.ModAPI.IMyInventory blockInv = block.GetInventory();
                        blockInv.GetItems(invList);

                        foreach (var item in invList)
                        {
                            bool removeItem = false;
                            if (item.Type.GetItemInfo().IsAmmo && removeAmmo)
                                removeItem = true;
                            if (item.Type.SubtypeId.Contains("Ice") && removeIce)
                                removeItem = true;
                            if(item.Type.SubtypeId.Contains("Uranium") && removeUranium)
                                removeItem = true;

                            if (!removeItem) continue;
                            MyFixedPoint amount = item.Amount;
                            uint itemId = item.ItemId;

                            blockInv.RemoveItems(itemId, amount);
                        }
                    }
                }
            }
        }

        public static bool BypassLimits(SpawnType spawntype, Vector3D pos)
        {
            if (spawntype == SpawnType.Dynamic)
                return Session.Instance.config.dynamicSpawningConfig.dynamicSpawnBypass;

            if (spawntype == SpawnType.SpawnArea)
            { 
                foreach(var area in Session.Instance.config.spawnAreas)
                {
                    if (!area.enableSpawnArea) continue;
                    if (Vector3D.Distance(pos, area.areaCenter) > area.areaRadius) continue;
                    return area.spawnAreaBypass;
                }

                return true;
            }

            if (spawntype == SpawnType.Nearby)
                return Session.Instance.config.spawnNearbyConfig.nearbySpawnBypass;

            if (spawntype == SpawnType.Original)
                return Session.Instance.config.spawnOriginalConfig.originalSpawnBypass;

            return true;
        }

        public static void CheckGridSpawnLimitsInOB(List<MyObjectBuilder_CubeGrid> obs, IMyFaction faction, HangarType hangarType, int index, SpawnType spawnType, long playerId)
        {
            if (hangarType == HangarType.Faction)
            {
                if (faction != null)
                {
                    if (Session.Instance.allHangarData.IsFactionGridAutoHangared(faction.FactionId, index))
                        if (Session.Instance.config.autoHangarConfig.autoBypassSpawnLimits) return;

                    if (BypassLimits(spawnType, obs[0].PositionAndOrientation.Value.Position)) return;
                }
            }

            if (hangarType == HangarType.Private)
            {
                if (Session.Instance.allHangarData.IsPrivateGridAutoHangared(playerId, index))
                    if (Session.Instance.config.autoHangarConfig.autoBypassSpawnLimits) return;

                if (BypassLimits(spawnType, obs[0].PositionAndOrientation.Value.Position)) return;
            }

            foreach (var grid in obs)
            {
                foreach(var block in grid.CubeBlocks)
                {
                    if (block.TypeId == typeof(MyObjectBuilder_BatteryBlock))
                    {
                        var baseBlock = block as MyObjectBuilder_Base;
                        if (baseBlock == null) continue;

                        var battery = baseBlock as MyObjectBuilder_BatteryBlock;
                        if (battery == null) continue;

                        // A negative setting (default -1) leaves the stored charge as it was
                        float batteryPct = Session.Instance.config.spawnConfig.batteryPercentage;
                        var batteryDef = MyDefinitionManager.Static.GetCubeBlockDefinition(block.GetId()) as MyBatteryBlockDefinition;
                        if (batteryPct >= 0 && batteryDef != null)
                            battery.CurrentStoredPower = batteryDef.MaxStoredPower * MathHelper.Clamp(batteryPct, 0f, 100f) / 100f;

                        continue;
                    }

                    if (block.TypeId == typeof(MyObjectBuilder_GasTank))
                    {
                        var baseBlock = block as MyObjectBuilder_Base;
                        if (baseBlock == null) continue;

                        var tank = baseBlock as MyObjectBuilder_GasTank;
                        if (tank == null) continue;

                        float h2Pct = Session.Instance.config.spawnConfig.h2Percentage;
                        if (h2Pct >= 0)
                            tank.FilledRatio = MathHelper.Clamp(h2Pct, 0f, 100f) / 100f;
                    }
                }
            }
        }

        public static void RemovePlayersFromSeats(MyCubeGrid grid)
        {
            List<IMyCockpit> Blocks = new List<IMyCockpit>();
            MyAPIGateway.TerminalActionsHelper.GetTerminalSystemForGrid(grid).GetBlocksOfType(Blocks, x => x.IsFunctional);

            foreach(var block in Blocks)
            {
                if (block.IsOccupied)
                    block.RemovePilot();
            }
        }

        public static void GridDamageMonitor(VRage.Game.ModAPI.IMySlimBlock block, float damage, MyHitInfo? info, long attackerId)
        {
            for (int i = Session.Instance.hangarDelay.Count - 1; i >= 0; i--)
            {
                bool found = false;
                HangarDelayData data = Session.Instance.hangarDelay[i];
                for (int j = data.gridData.Count - 1; j >= 0; j--)
                {
                    GridData gData = data.gridData[j];
                    if (gData.gridId != block.CubeGrid.EntityId) continue;

                    MyVisualScriptLogicProvider.SendChatMessageColored($"Grid {gData.gridName} was damaged which has stopped the hangaring process!!", Color.Red, "[FactionHangar]", data.requesterId, "Red");

                    found = true;
                    RemoveGridDamageMontior(data.gridData);
  
                    break;
                }

                if (found)
                {
                    Session.Instance.hangarDelay.RemoveAtFast(i);
                    return;
                }
                
            }
        }

        public static void RemoveGridDamageMontior(List<GridData> gridData)
        {
            foreach (var grids in gridData)
            {
                VRage.ModAPI.IMyEntity ent;
                MyAPIGateway.Entities.TryGetEntityById(grids.gridId, out ent);
                if (ent == null) continue;

                MyCubeGrid myGrid = ent as MyCubeGrid;
                if (myGrid != null)
                    myGrid.OnGridBlockDamaged -= GridDamageMonitor;
            }
        }
    }
}
