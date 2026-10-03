using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using System.Collections.Generic;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Game.ObjectBuilders.ComponentSystem;
using VRageMath;

namespace CustomHangar
{
    /// <summary>
    /// Spawn rules shared by the client preview and the server. The server is the authority;
    /// the client runs the same code so the preview matches what the server will decide.
    /// </summary>
    public static class SpawnRules
    {
        /// <summary>Furthest a placed spawn's grid centre may be from the requesting player (the preview reaches 70 m from the camera).</summary>
        public const double MaxPlacementDistance = 200.0;

        static readonly List<MyEntity> _entities = new List<MyEntity>();

        static Config Cfg { get { return Session.Instance.config; } }

        /// <summary>
        /// Spawn type and cost for a grid centred on <paramref name="center"/>. Returns None for an invalid location.
        /// </summary>
        public static SpawnType Resolve(Vector3D center, Vector3D original, long playerId, double mass, out long cost, out EnemyChecks enemyCheck, out bool freeZone)
        {
            var cfg = Cfg;
            cost = 0;
            enemyCheck = default(EnemyChecks);
            // GVK: inside own faction's safezone the spawn is free and ignores nearby enemies
            freeZone = Utils.IsInOwnFactionSafeZone(center, playerId);
            double distance = Vector3D.Distance(center, original);

            if (cfg.spawnNearbyConfig.allowSpawnNearby && distance <= cfg.spawnNearbyConfig.nearbyRadius)
            {
                cost = freeZone ? 0 : cfg.spawnNearbyConfig.nearbySpawnCost;
                enemyCheck = cfg.spawnNearbyConfig.nearbyEnemyCheck;
                return SpawnType.Nearby;
            }

            bool inverse = Session.Instance.useInverseSpawnArea;
            foreach (var area in cfg.spawnAreas)
            {
                if (!area.enableSpawnArea) continue;
                bool inside = Vector3D.DistanceSquared(center, area.areaCenter) <= (double)area.areaRadius * area.areaRadius;
                if (inside == inverse) continue;

                cost = freeZone ? 0 : area.spawnAreaCost;
                enemyCheck = area.spawnAreasEnemyCheck;
                return SpawnType.SpawnArea;
            }

            if (cfg.dynamicSpawningConfig.enableDynamicSpawning && !inverse)
            {
                cost = freeZone ? 0 : (long)((float)mass * (float)distance * cfg.dynamicSpawningConfig.costMultiplier);
                enemyCheck = cfg.dynamicSpawningConfig.dynamicEnemyCheck;
                return SpawnType.Dynamic;
            }

            return SpawnType.None;
        }

        /// <summary>Wallet rule shared with Utils.UpdateBalance: a faction hangar spawn uses the faction wallet if it can, otherwise the player's.</summary>
        public static bool CanAfford(long cost, HangarType hangarType, long factionBalance, long playerBalance)
        {
            if (cost <= 0) return true;
            if (hangarType == HangarType.Faction && cost <= factionBalance) return true;
            return cost <= playerBalance;
        }

        /// <summary>True if a hostile grid (or tracked block, with block checks on) is within the check radius.</summary>
        public static bool IsEnemyNear(Vector3D point, EnemyChecks check, long owner)
        {
            if (!check.checkEnemiesNearby) return false;
            IMyFaction ownerFaction = MyAPIGateway.Session.Factions.TryGetPlayerFaction(owner);

            if (Cfg.enemyCheckConfig.enableBlockCheck)
            {
                double rangeSq = (double)check.enemyDistanceCheck * check.enemyDistanceCheck;
                foreach (var block in Session.Instance.enemyBlockCheckList)
                {
                    if (block.Closed || Vector3D.DistanceSquared(point, block.GetPosition()) > rangeSq) continue;
                    if (IsHostile(block.OwnerId, owner, ownerFaction, check)) return true;
                }

                return false;
            }

            var sphere = new BoundingSphereD(point, check.enemyDistanceCheck);
            _entities.Clear();
            MyGamePruningStructure.GetAllTopMostEntitiesInSphere(ref sphere, _entities);
            bool found = false;
            foreach (var ent in _entities)
            {
                if (ent.MarkedForClose) continue;
                var grid = ent as IMyCubeGrid;
                if (grid == null || grid.Physics == null) continue;

                long gridOwner = grid.BigOwners.Count > 0 ? grid.BigOwners[0] : 0;
                if (IsHostile(gridOwner, owner, ownerFaction, check))
                {
                    found = true;
                    break;
                }
            }

            _entities.Clear();
            return found;
        }

        static bool IsHostile(long other, long owner, IMyFaction ownerFaction, EnemyChecks check)
        {
            if (other == owner || other == 0) return false;
            IMyFaction otherFaction = MyAPIGateway.Session.Factions.TryGetPlayerFaction(other);
            // NPCs are skipped, not taken as proof that nothing hostile is near
            if (otherFaction != null && check.omitNPCs && (otherFaction.IsEveryoneNpc() || otherFaction.Tag.Length > 3)) return false;
            return AreFactionsEnemies(ownerFaction, otherFaction, check.alliesFriendly);
        }

        static bool AreFactionsEnemies(IMyFaction faction1, IMyFaction faction2, bool alliesFriendly)
        {
            if (faction1 == null || faction2 == null) return true;
            if (faction1 == faction2) return false;

            var relation = MyAPIGateway.Session.Factions.GetRelationBetweenFactions(faction1.FactionId, faction2.FactionId);
            if (relation == MyRelationsBetweenFactions.Enemies) return true;
            return relation == MyRelationsBetweenFactions.Friends && !alliesFriendly;
        }

        /// <summary>Mass of stored grids from definitions: blocks plus inventory contents.</summary>
        public static double GetBlueprintMass(MyObjectBuilder_CubeGrid[] grids)
        {
            double mass = 0;
            var defs = MyDefinitionManager.Static;
            foreach (var grid in grids)
            {
                if (grid?.CubeBlocks == null) continue;
                foreach (var block in grid.CubeBlocks)
                {
                    MyCubeBlockDefinition def;
                    if (defs.TryGetCubeBlockDefinition(block.GetId(), out def))
                        mass += def.Mass;

                    var components = block.ComponentContainer?.Components;
                    if (components == null) continue;
                    foreach (var data in components)
                        mass += GetInventoryMass(data.Component);
                }
            }

            return mass;
        }

        static double GetInventoryMass(MyObjectBuilder_ComponentBase component)
        {
            var aggregate = component as MyObjectBuilder_InventoryAggregate;
            if (aggregate != null)
            {
                double sum = 0;
                if (aggregate.Inventories != null)
                    foreach (var inner in aggregate.Inventories)
                        sum += GetInventoryMass(inner);
                return sum;
            }

            var inventory = component as MyObjectBuilder_Inventory;
            if (inventory?.Items == null) return 0;

            double mass = 0;
            foreach (var item in inventory.Items)
            {
                if (item.PhysicalContent == null) continue;
                MyPhysicalItemDefinition def;
                if (MyDefinitionManager.Static.TryGetPhysicalItemDefinition(item.PhysicalContent.GetId(), out def))
                    mass += def.Mass * (double)item.Amount;
            }

            return mass;
        }

        /// <summary>World centre of the grid's cube bounds; the client preview keeps this point on the crosshair.</summary>
        public static Vector3D GetGridWorldCenter(MyObjectBuilder_CubeGrid grid)
        {
            MatrixD world = grid.PositionAndOrientation.Value.GetMatrix();
            if (grid.CubeBlocks == null || grid.CubeBlocks.Count == 0) return world.Translation;

            var min = new Vector3I(int.MaxValue);
            var max = new Vector3I(int.MinValue);
            foreach (var block in grid.CubeBlocks)
            {
                Vector3I blockMin = block.Min;
                Vector3I blockMax = blockMin;
                MyCubeBlockDefinition def;
                if (MyDefinitionManager.Static.TryGetCubeBlockDefinition(block.GetId(), out def))
                {
                    Matrix rotation;
                    ((MyBlockOrientation)block.BlockOrientation).GetMatrix(out rotation);
                    Vector3 size = Vector3.Abs(Vector3.TransformNormal(new Vector3(def.Size), rotation));
                    blockMax = blockMin + Vector3I.Round(size) - Vector3I.One;
                }

                min = Vector3I.Min(min, blockMin);
                max = Vector3I.Max(max, blockMax);
            }

            double half = 0.5 * MyDefinitionManager.Static.GetCubeSize(grid.GridSizeEnum);
            var localCenter = new Vector3D((double)(min.X + max.X) * half, (double)(min.Y + max.Y) * half, (double)(min.Z + max.Z) * half);
            return Vector3D.Transform(localCenter, world);
        }

        /// <summary>
        /// Moves the stored grids rigidly so the main grid lands on <paramref name="placement"/>.
        /// False if the placement is malformed or the blueprint has no positions.
        /// </summary>
        public static bool ApplyPlacement(MyObjectBuilder_CubeGrid[] grids, MyPositionAndOrientation placement)
        {
            Vector3D position = placement.Position;
            Vector3 forward = placement.Forward;
            Vector3 up = placement.Up;
            if (!position.IsValid() || !forward.IsValid() || !up.IsValid()) return false;
            if (forward.LengthSquared() < 0.01f) return false;

            forward.Normalize();
            up -= forward * forward.Dot(up);
            if (up.LengthSquared() < 0.01f) return false;
            up.Normalize();

            foreach (var grid in grids)
                if (grid == null || !grid.PositionAndOrientation.HasValue) return false;

            MatrixD target = MatrixD.CreateWorld(position, forward, up);
            MatrixD delta = MatrixD.Invert(grids[0].PositionAndOrientation.Value.GetMatrix()) * target;
            foreach (var grid in grids)
            {
                MatrixD moved = grid.PositionAndOrientation.Value.GetMatrix() * delta;
                grid.PositionAndOrientation = new MyPositionAndOrientation(moved.Translation, (Vector3)moved.Forward, (Vector3)moved.Up);
            }

            return true;
        }
    }
}
