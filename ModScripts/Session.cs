using Sandbox.ModAPI;
using VRage.Game.Components;
using VRageMath;
using Sandbox.Game.Entities;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using Sandbox.Game;
using VRage.Game;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRage.Utils;
using VRage;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using VRage.Input;
using System.Runtime.CompilerServices;
using Sandbox.Definitions;
using VRage.GameServices;
using System.Net;
using System.Linq;
using Sandbox.Game.Weapons.Guns;
using System.Collections.Concurrent;
using VRage.Game.ModAPI.Ingame.Utilities;
using System.Text;
using VRageRender;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Entities.Character;
using System.Net.Sockets;

namespace CustomHangar
{
    [MySessionComponentDescriptor(MyUpdateOrder.BeforeSimulation)]
    public class Session : MySessionComponentBase
    {
        public bool isServer;
        public bool isDedicated;
        public ConcurrentDictionary<IMyFaction, ConcurrentQueue<IMyCubeGrid>> factionGrids = new ConcurrentDictionary<IMyFaction, ConcurrentQueue<IMyCubeGrid>>();
        public ConcurrentDictionary<long, ConcurrentQueue<IMyCubeGrid>> playerGrids = new ConcurrentDictionary<long, ConcurrentQueue<IMyCubeGrid>>();
        public static Session Instance;
        public readonly ushort NetworkHandle = 2355;
        
        public Dictionary<long, int> factionSlotTotals = new Dictionary<long, int>();
        public Dictionary<long, int> privateSlotTotals = new Dictionary<long, int>();
        public Config config;
        public List<MyObjectBuilder_Identity> allIdentities = new List<MyObjectBuilder_Identity>(); 
        public const string path = "{0}.xml";
        public List<HangarDelayData> hangarDelay = new List<HangarDelayData>();
        public int ticks;
        public List<long> npcs = new List<long>();

        public AllHangarData allHangarData = new AllHangarData();
        public CacheGridsForStorage gridsToStore = new CacheGridsForStorage();
        public HashSet<IMyCubeBlock> enemyBlockCheckList = new HashSet<IMyCubeBlock>();
        private readonly HashSet<MyDefinitionId> enemyCheckBlockIds = new HashSet<MyDefinitionId>();
        private readonly HashSet<IMyCubeGrid> trackedGrids = new HashSet<IMyCubeGrid>();
        private bool blockTracking;
        public Dictionary<IMyFaction, FactionTimers> cooldownTimers = new Dictionary<IMyFaction, FactionTimers>();
        public List<string> cacheGridPaths = new List<string>();

        //ClientSide Vars
        public List<MyCubeGrid> previewGrids = new List<MyCubeGrid>();
        private bool enableInput;
        private bool RotateX;
        private bool RotateY;
        private bool RotateZ;
        private bool nRotateX;
        private bool nRotateY;
        private bool nRotateZ;
        private float RotationSpeed = 0.01f;
        private int previewDistance = 50;
        private bool allowSpawn;
        private int spawnIndex = -1;
        private HangarType hangarType = HangarType.Faction;
        public Vector3D original = new Vector3D();
        public float previewMass;
        public SpawnType spawnType = SpawnType.None;
        public bool useInverseSpawnArea;
        public IMyPlayer playerCache = null;
        private Vector3D startCoordsCache = new Vector3D();
        private Vector3D endCoordCache= new Vector3D();
        public List<IMyGps> clientSpawnLocations = new List<IMyGps>();
        private bool init;
        private IMyHudNotification hudNotify;
        private long spawnCost;
        private SpawnError spawnError = SpawnError.None;
        public long playerWallet;
        public long factionWallet;
        private bool drawClientSphereDebug = true;
        public bool spawnClientGPS = true;
        public List<IMyCubeGrid> gridListToStore = new List<IMyCubeGrid>();
        public HangarType gridListType = HangarType.Faction;
        public bool inGridPlacementView;
        public bool spawnCostBypass;
        public long spawnGridId;

        // Server: private hangar cooldown end, in session ticks, per identity
        public readonly Dictionary<long, int> privateStoreCooldownEnd = new Dictionary<long, int>();
        public readonly Dictionary<long, int> privateRetrievalCooldownEnd = new Dictionary<long, int>();
        private readonly List<IMyPlayer> playerBuffer = new List<IMyPlayer>();

        // Client timers
        public int retrievalTimer;
        public int storeTimer;



        // TODO
        // ent.Components.Get<MyResourceSourceComponent>()

        // *Add more logging/nofications
        // *Run spawning grids per physical connection in offset ticks
        // *Add grid below surface to autohangar
        // *Fix connectors not staying connected when spawning grid from autohangar(physical connections)
        // *Add nofication to first player that logins when their stuff has been autohangared
        // ***DONE*** Add damage resets/stops timer to delay grid storage
        // *Add plugin for admin commands via console
        // *Add velocity check when storing grids
        // *Add save file for client commands
        // *Fix allPlayers not syncing in mp
        // *Adjust spawn areas to be free for spawning autohangared grids but doesn't exist otherwise
        // *Clamp min cost for dynamic spawning with config
        // *Remove mechanical connections from potencial grids to store
        // ***DONE*** Fix grid dup from running .true while having active ghost grid
        // *Remove projections for potential hangaring
        // *Set velocity to 0 when before setting grid to static
        // *Fix leader access to storing/spawning
        // Add enemy checks during hangar delay
        // ***DONE*** Add ownership checks on all connected grids
        // Fix blocks turning off when spawning from hangar

        public override void BeforeStart()
        {
            Instance = this;
            isServer = MyAPIGateway.Session.IsServer;
            isDedicated = MyAPIGateway.Utilities.IsDedicated;
            MyAPIGateway.Multiplayer.RegisterSecureMessageHandler(NetworkHandle, Comms.MessageHandler);
            MyAPIGateway.Utilities.MessageEntered += ChatHandler;

            if (isServer)
            {
                config = Config.LoadConfig();
                foreach (var area in config.spawnAreas)
                {
                    if (area.inverseArea)
                    {
                        useInverseSpawnArea = true;
                        break;
                    }
                }

                allHangarData = AllHangarData.LoadHangarData();

                if (isDedicated)
                    StartBlockTracking();
            }

            if (!isDedicated)
                MyVisualScriptLogicProvider.ToolEquipped += ToolEquipped;
        }

        /// <summary>
        /// Server, once at startup: identities with their last logout time, for auto-hangar and names.
        /// Builds a world checkpoint, so it is not run on every save; GetPlayerName covers newer players.
        /// </summary>
        public void UpdateIdentities()
        {
            var save = MyAPIGateway.Session.GetCheckpoint(MyAPIGateway.Session.Name);
            if (save?.Identities != null)
                allIdentities = save.Identities;
        }

        private void CheckLastLogOff()
        {
            UpdateIdentities();
            if (!config.autoHangarConfig.enableAutoHangar) return;

            Dictionary<IMyFaction, bool> factionsToHangar = new Dictionary<IMyFaction, bool>();
            List<IMyFaction> expiredFactions = new List<IMyFaction>();
            List<long> expiredPlayers = new List<long>();
            
            foreach(var identity in allIdentities)
            {
                IMyFaction faction = MyAPIGateway.Session.Factions.TryGetPlayerFaction(identity.IdentityId);

                if (faction != null)
                {
                    if (faction.Tag.Length > 3 || faction.IsEveryoneNpc()) continue;

                    bool excludeFaction = false;
                    foreach(var excludedTag in config.autoHangarConfig.exclusions.excludedFactions.excludedFaction)
                    {
                        if (faction.Tag != excludedTag) continue;
                        excludeFaction = true;
                        break;
                    }
                    if (excludeFaction) continue;

                    if (!factionsToHangar.ContainsKey(faction))
                        factionsToHangar.Add(faction, true);
                }

                if (DateTime.Now - identity.LastLogoutTime >= TimeSpan.FromDays(config.autoHangarConfig.daysFactionLogin))
                {
                    MyLog.Default.WriteLineAndConsole($"[FactionHangar] Player {identity.DisplayName} hasn't logged in within {config.autoHangarConfig.daysFactionLogin} days.");

                    if (faction != null)
                    {
                        if (!factionsToHangar.ContainsKey(faction)) continue;
                        if (!factionsToHangar[faction]) continue;
                    }    
                    else
                    {
                        if (expiredPlayers.Contains(identity.IdentityId))
                            continue;
                        else
                            expiredPlayers.Add(identity.IdentityId);
                    }
                }
                else
                {
                    if (faction != null)
                    {
                        if (!factionsToHangar.ContainsKey(faction)) continue;
                        factionsToHangar[faction] = false;
                    }
                }
            }

            foreach(var faction in factionsToHangar.Keys)
            {
                if (factionsToHangar[faction])
                    expiredFactions.Add(faction);
            }

            if (expiredFactions.Count > 0 || expiredPlayers.Count > 0)
            {
                HashSet<IMyEntity> ents = new HashSet<IMyEntity>();
                List<IMyCubeGrid> physicalGroup = new List<IMyCubeGrid>();
                HashSet<long> processedGrids = new HashSet<long>();
                MyAPIGateway.Entities.GetEntities(ents);

                factionGrids.Clear();
                playerGrids.Clear();

                // Queue every grid once, under the owner of the first grid seen in its physical group
                foreach (var ent in ents)
                {
                    IMyCubeGrid grid = ent as IMyCubeGrid;
                    if (grid == null || !processedGrids.Add(grid.EntityId)) continue;

                    long owner = grid.BigOwners.Count > 0 ? grid.BigOwners[0] : 0;
                    IMyFaction faction = MyAPIGateway.Session.Factions.TryGetPlayerFaction(owner);
                    ConcurrentQueue<IMyCubeGrid> queue;
                    if (faction != null)
                    {
                        if (!factionGrids.TryGetValue(faction, out queue))
                        {
                            queue = new ConcurrentQueue<IMyCubeGrid>();
                            factionGrids.TryAdd(faction, queue);
                        }
                    }
                    else if (!playerGrids.TryGetValue(owner, out queue))
                    {
                        queue = new ConcurrentQueue<IMyCubeGrid>();
                        playerGrids.TryAdd(owner, queue);
                    }

                    queue.Enqueue(grid);
                    GetGroupByType(grid, physicalGroup, GridLinkTypeEnum.Physical);
                    foreach (var connectedGrid in physicalGroup)
                        if (processedGrids.Add(connectedGrid.EntityId))
                            queue.Enqueue(connectedGrid);
                }
            }

            foreach (var faction in expiredFactions)
                AutoHangar(faction, 0);

            foreach (var playerId in expiredPlayers)
                AutoHangar(null, playerId);
        }

        private void AutoHangar(IMyFaction faction, long playerId)
        {
            ConcurrentQueue<IMyCubeGrid> grids;
            if (faction != null)
                factionGrids.TryGetValue(faction, out grids);
            else
                playerGrids.TryGetValue(playerId, out grids);

            if (grids == null) return;

            HashSet<long> handled = new HashSet<long>();
            List<IMyCubeGrid> connectedGrids = new List<IMyCubeGrid>();
            IMyCubeGrid grid;
            while (grids.TryDequeue(out grid))
            {
                if (grid == null || grid.MarkedForClose || !handled.Add(grid.EntityId)) continue;

                GetGroupByType(grid, connectedGrids, GridLinkTypeEnum.Physical);
                bool skip = false;
                foreach (var connectedGrid in connectedGrids)
                {
                    handled.Add(connectedGrid.EntityId);
                    if (skip) continue;

                    // Never hangar a group that includes someone else's grid (e.g. landing-geared to an active base)
                    if (!IsOwnedBy(connectedGrid, faction, playerId))
                    {
                        MyLog.Default.WriteLineAndConsole($"[FactionHangar] - AutoHangar skipped grid {grid.CustomName}: connected to {connectedGrid.CustomName}, which another player owns");
                        skip = true;
                        continue;
                    }

                    if (faction != null && Utils.CheckForExcludedBlock(connectedGrid as MyCubeGrid))
                        skip = true;
                }

                if (skip) continue;

                MyCubeGrid biggestGrid = (grid as MyCubeGrid)?.GetBiggestGridInGroup();
                if (biggestGrid == null) continue;

                long owner = biggestGrid.BigOwners.Count > 0 ? biggestGrid.BigOwners[0] : 0;
                if (owner == 0) continue;

                string playerName = GetPlayerName(owner);
                RequestingGridStorage(owner, owner, biggestGrid.EntityId, playerName, faction == null, true);
            }
        }

        /// <summary>True if the grid is unowned or owned by the expired faction (or expired player).</summary>
        static bool IsOwnedBy(IMyCubeGrid grid, IMyFaction faction, long playerId)
        {
            long owner = grid.BigOwners.Count > 0 ? grid.BigOwners[0] : 0;
            if (owner == 0) return true;
            if (faction == null) return owner == playerId;

            IMyFaction ownerFaction = MyAPIGateway.Session.Factions.TryGetPlayerFaction(owner);
            return ownerFaction != null && ownerFaction.FactionId == faction.FactionId;
        }

        public new void HandleInput()
        {
            if (isServer && isDedicated) return;
            if (!enableInput) return;

            if (playerCache.Character == null || playerCache.Character.IsDead)
            {
                RemovePreviewGrids();
                return;
            }

            UpdatePosition();
            DetectSpawnType();
            //spawnCost = CalculateCost();
            //UpdateHudMessage();

            if (MyAPIGateway.Input.IsKeyPress(MyKeys.Home))
                RotateX = true;
            else
                RotateX = false;
                
            if (MyAPIGateway.Input.IsKeyPress(MyKeys.End))
                nRotateX = true;
            else
                nRotateX = false;

            if (MyAPIGateway.Input.IsKeyPress(MyKeys.Delete))
                RotateY = true;
            else
                RotateY = false;

            if (MyAPIGateway.Input.IsKeyPress(MyKeys.PageDown))
                nRotateY = true;
            else
                nRotateY = false;

            if (MyAPIGateway.Input.IsKeyPress(MyKeys.Insert))
                RotateZ = true;
            else
                RotateZ = false;

            if (MyAPIGateway.Input.IsKeyPress(MyKeys.PageUp))
                nRotateZ = true;
            else
                nRotateZ = false;

            if (MyAPIGateway.Input.IsKeyPress(MyKeys.D0))
                RemovePreviewGrids();

            if (MyAPIGateway.Input.IsNewLeftMousePressed())
                TrySpawnPlacement();

            if (MyAPIGateway.Input.IsKeyPress(MyKeys.LeftAlt))
            {
                var rotateSpeed = MyAPIGateway.Input.DeltaMouseScrollWheelValue();
                if (rotateSpeed > 0)
                    RotationSpeed += .001f;
                else if (rotateSpeed < 0)
                    RotationSpeed -= .001f;

                RotationSpeed = MathHelper.Clamp(RotationSpeed, .01f, .2f);
                return;
            }

            var dScroll = MyAPIGateway.Input.DeltaMouseScrollWheelValue();

            if (dScroll > 0)
                previewDistance++;
            else if (dScroll < 0)
                previewDistance--;

            previewDistance = MathHelper.Clamp(previewDistance, 10, 70);
        }

        private void UpdatePosition()
        {
            //startCoordsCache = playerCache.Character.GetPosition();
            //endCoordCache = playerCache.Character.WorldMatrix.Forward * previewDistance + startCoordsCache;
            startCoordsCache = MyAPIGateway.Session.Camera.Position;
            endCoordCache = MyAPIGateway.Session.Camera.WorldMatrix.Forward * previewDistance + startCoordsCache;
        }

        private bool hudShown;
        private bool hudAllow;
        private long hudCost;
        private SpawnType hudType;
        private SpawnError hudError;
        private HangarType hudHangar;

        private void UpdateHudMessage()
        {
            if (allowSpawn)
                spawnError = SpawnError.None;

            if (hudNotify == null) return;

            // Only rebuild the text when something shown on it changed
            if (hudShown && hudAllow == allowSpawn && hudCost == spawnCost && hudType == spawnType && hudError == spawnError && hudHangar == hangarType) return;
            hudShown = true;
            hudAllow = allowSpawn;
            hudCost = spawnCost;
            hudType = spawnType;
            hudError = spawnError;
            hudHangar = hangarType;

            hudNotify.Hide();
            hudNotify.Text = $"[Valid Spawn] = {allowSpawn} | [Cost To Spawn] = {spawnCost} SC | [SpawnOption] = {spawnType} | [SpawningError] = {spawnError} | [HangarType] = {hangarType}";
            hudNotify.Show();
        }

        /// <summary>Client preview of the server's spawn decision (SpawnRules), measured at the main preview grid's centre.</summary>
        private void DetectSpawnType(bool force = false)
        {
            if (!force)
                if (ticks % 10 != 0) return;

            if (previewGrids == null || previewGrids.Count == 0) return;
            spawnError = SpawnError.None;

            // Same point the server derives from the placement it receives
            Vector3D center = previewGrids[0].PositionComp.WorldAABB.Center;
            long playerId = playerCache.IdentityId;
            long cost;
            EnemyChecks enemyCheck;
            bool freeZone;
            spawnType = SpawnRules.Resolve(center, original, playerId, previewMass, out cost, out enemyCheck, out freeZone);
            if (spawnType == SpawnType.None)
            {
                allowSpawn = false;
                spawnCost = 0;
                spawnError = SpawnError.InvalidSpawningLocation;
                UpdateHudMessage();
                return;
            }

            spawnCost = spawnCostBypass ? 0 : cost;
            bool enemy = !freeZone && SpawnRules.IsEnemyNear(center, enemyCheck, playerId);
            if (enemy)
                spawnError = SpawnError.EnemyNearby;

            bool canAfford = SpawnRules.CanAfford(spawnCost, hangarType, factionWallet, playerWallet);
            if (!canAfford)
                spawnError = SpawnError.InsuffientFunds;

            bool intersecting = Utils.IsGridIntersecting(previewGrids);
            if (intersecting)
                spawnError = SpawnError.EntityBlockingPlacement;

            allowSpawn = !enemy && canAfford && !intersecting;
            UpdateHudMessage();
        }

        /// <summary>Enemy check for storing a grid, using that hangar type's settings.</summary>
        public bool IsEnemyNearStoring(IMyCubeGrid grid, HangarType hangarType, long owner)
        {
            if (grid == null) return false;
            EnemyChecks check = hangarType == HangarType.Faction
                ? config.factionHangarConfig.factionHangarEnemyCheck
                : config.privateHangarConfig.privateHangarEnemyCheck;
            return SpawnRules.IsEnemyNear(grid.GetPosition(), check, owner);
        }

        static readonly MyStringId LineMaterial = MyStringId.GetOrCompute("WeaponLaser");
        readonly List<MyEntity> nearbyEntities = new List<MyEntity>();
        readonly List<IMyCubeGrid> nearbyGrids = new List<IMyCubeGrid>();

        /// <summary>Grids in front of the player within 200 m, refreshed 6 times a second instead of every frame.</summary>
        private void RefreshNearbyGrids()
        {
            nearbyGrids.Clear();
            GetEntitiesInSphere(nearbyEntities, playerCache.GetPosition(), 200);
            Vector3D myPosition = playerCache.GetPosition();
            Vector3D forward = playerCache.Character.WorldMatrix.Forward;
            foreach (var ent in nearbyEntities)
            {
                IMyCubeGrid grid = ent as IMyCubeGrid;
                if (grid == null || previewGrids.Contains(ent as MyCubeGrid)) continue;
                if ((grid.GetPosition() - myPosition).Dot(forward) < 0) continue; // behind us

                nearbyGrids.Add(grid);
            }

            nearbyEntities.Clear();
        }

        private void DrawBoundingBox()
        {
            if (ticks % 10 == 0)
                RefreshNearbyGrids();

            Color otherCol = Color.White;
            foreach (var grid in nearbyGrids)
            {
                if (grid.MarkedForClose) continue;
                BoundingBoxD boundingBoxD = grid.PositionComp.LocalAABB;
                MatrixD matrixD = grid.PositionComp.WorldMatrixRef;
                MySimpleObjectDraw.DrawTransparentBox(ref matrixD, ref boundingBoxD, ref otherCol, MySimpleObjectRasterizer.Wireframe, 1, 0.04f, null, LineMaterial, false, -1, MyBillboard.BlendTypeEnum.Standard, 1f, null);
            }

            Color color = allowSpawn ? Color.LightGreen : Color.Red;
            foreach (var grid in previewGrids)
            {
                BoundingBoxD boundingBoxD = grid.PositionComp.LocalAABB;
                MatrixD matrixD = grid.PositionComp.WorldMatrixRef;
                MySimpleObjectDraw.DrawTransparentBox(ref matrixD, ref boundingBoxD, ref color, MySimpleObjectRasterizer.Wireframe, 1, 0.04f, null, LineMaterial, false, -1, MyBillboard.BlendTypeEnum.Standard, 1f, null);
            }
        }

        public override void Draw()
        {
            if (isServer && isDedicated) return;
            if (previewGrids != null && previewGrids.Count != 0 && previewMass != 0)
            {
                if (playerCache.Character == null || playerCache.Character.IsDead)
                {
                    RemovePreviewGrids();
                    return;
                }

                DrawSpawnAreas();
                DrawBoundingBox();
                enableInput = true;

                IMyEntity ent = previewGrids[0] as IMyEntity;
                var center = ent.WorldAABB.Center;
                var matrix = ent.WorldMatrix;
                var entPos = ent.GetPosition();
                var diff = entPos - center;

                //Vector3D startCoords = playerCache.Character.GetPosition();
                //Vector3D endCoords = playerCache.Character.WorldMatrix.Forward * previewDistance + startCoords;
                Vector3D startCoords = MyAPIGateway.Session.Camera.Position;
                Vector3D endCoords = MyAPIGateway.Session.Camera.WorldMatrix.Forward * previewDistance + startCoords;
                //MatrixD endCoordsMatrix = MatrixD.CreateWorld(endCoords);

                ent.PositionComp.SetPosition(endCoords + diff);

                for (int i = 0; i < previewGrids.Count; i++)
                {
                    if (i == 0) continue;
                    IMyEntity subEnt = previewGrids[i] as IMyEntity;
                    var subDiff = subEnt.GetPosition() - entPos;
                    subEnt.PositionComp.SetPosition(endCoords + subDiff + diff);
                }

                if (RotateX || RotateY || RotateZ || nRotateX || nRotateY || nRotateZ)
                {
                    center = ent.WorldAABB.Center;
                    matrix = ent.WorldMatrix;
                    var OffsetVector3 = center;
                    MatrixD rotationMatrix = MatrixD.Zero;
                    var up = matrix.Up;
                    var left = matrix.Left;
                    var forward = matrix.Forward;
                    up = Vector3D.Normalize(up);
                    left = Vector3D.Normalize(left);
                    forward = Vector3D.Normalize(forward);

                    if (RotateX) rotationMatrix = MatrixD.CreateFromAxisAngle(left, RotationSpeed);
                    if (nRotateX) rotationMatrix = MatrixD.CreateFromAxisAngle(-left, RotationSpeed);

                    if (RotateY) rotationMatrix = MatrixD.CreateFromAxisAngle(up, RotationSpeed);
                    if (nRotateY) rotationMatrix = MatrixD.CreateFromAxisAngle(-up, RotationSpeed);

                    if (RotateZ) rotationMatrix = MatrixD.CreateFromAxisAngle(forward, RotationSpeed);
                    if (nRotateZ) rotationMatrix = MatrixD.CreateFromAxisAngle(-forward, RotationSpeed);

                    /*if (RotateY)
                    {
                        if (RotateX) rotationMatrix *= MatrixD.CreateFromAxisAngle(up, RotationSpeed);
                        else rotationMatrix = MatrixD.CreateFromAxisAngle(up, RotationSpeed);
                    }

                    if (RotateZ)
                    {
                        if (RotateY || RotateX) rotationMatrix *= MatrixD.CreateFromAxisAngle(forward, RotationSpeed);
                        else rotationMatrix = MatrixD.CreateFromAxisAngle(forward, RotationSpeed);
                    }*/

                    rotationMatrix = MatrixD.CreateTranslation(-OffsetVector3) * rotationMatrix * MatrixD.CreateTranslation(OffsetVector3);

                    matrix *= rotationMatrix;
                    ent.SetWorldMatrix(matrix);
                    UpdateSubgrids(rotationMatrix);
                }

            }
            else
                enableInput = false;
        }

        public void UpdateSubgrids(MatrixD rotationMat)
        {
            for (int i = 0; i < previewGrids.Count; i++)
            {
                if (i == 0) continue;

                MatrixD matrix = previewGrids[i].WorldMatrix;
                matrix *= rotationMat;
                previewGrids[i].PositionComp.SetWorldMatrix(ref matrix);
            }
        }

        private void DrawSpawnAreas()
        {
            if (!drawClientSphereDebug) return;
            foreach(var area in config.spawnAreas)
            {
                if (!area.enableSpawnArea) continue;

                MatrixD mat = MatrixD.CreateWorld(area.areaCenter);
                Color color = Color.LightBlue;
                MySimpleObjectDraw.DrawTransparentSphere(ref mat, area.areaRadius, ref color, MySimpleObjectRasterizer.Wireframe, 70, null, LineMaterial, 1f, -1, null, VRageRender.MyBillboard.BlendTypeEnum.Standard, 10f);
            }

            if (config.spawnNearbyConfig.allowSpawnNearby)
            {
                MatrixD mat = MatrixD.CreateWorld(original);
                Color color = Color.LightGreen;
                MySimpleObjectDraw.DrawTransparentSphere(ref mat, config.spawnNearbyConfig.nearbyRadius, ref color, MySimpleObjectRasterizer.Wireframe, 70, null, LineMaterial, .09f, -1, null, VRageRender.MyBillboard.BlendTypeEnum.Standard, 10f);
            }
        }

        public override void UpdateBeforeSimulation()
        {
            Init();
            HandleInput();

            ticks++;

            RunClientTimers();

            // Server Only
            if (!isServer) return;
            

            // Runs every 60 ticks (1 sec)
            RunDelayTimers();
        }

        private void Init()
        {
            if (isServer && isDedicated)
            {
                if (init) return;

                if (config.autoHangarConfig.enableAutoHangar)
                    CheckLastLogOff();
                else
                    UpdateIdentities();

                init = true;
                return;
            }

            if (init) return;

            if (playerCache == null)
                playerCache = MyAPIGateway.Session.LocalHumanPlayer;
                
            if (playerCache != null)
            {
                if (config == null)
                {
                    // Ask every 5 s until it arrives, not every tick
                    if (ticks % 300 == 0)
                        Comms.ClientRequestConfig(playerCache.SteamUserId);
                    return;
                }

                StartBlockTracking();

                if (isServer)
                {
                    if (config.autoHangarConfig.enableAutoHangar)
                        CheckLastLogOff();
                    else
                        UpdateIdentities();
                }

                init = true;
            }
                
        }

        public bool TrySpawnPlacement()
        {
            if (previewGrids.Count == 0) return false;
            DetectSpawnType(true);
            if (!allowSpawn) return false;

            // Only the slot and the main grid's placement are sent; the server reloads the stored grids.
            MatrixD placement = previewGrids[0].WorldMatrix;
            Comms.SendGridsToSpawn(spawnIndex, hangarType, spawnGridId, new MyPositionAndOrientation(ref placement));

            RemovePreviewGrids();

            return true;
        }

        public void RemovePreviewGrids()
        {
            foreach (var grid in previewGrids)
                grid.Close();

            previewGrids.Clear();
            ResetClientValues();

        }

        private void ResetClientValues()
        {
            previewDistance = 50;
            allowSpawn = false;
            spawnIndex = -1;
            hangarType = HangarType.Faction;
            original = new Vector3D();
            previewMass = 0;
            spawnType = SpawnType.None;
            spawnCost = 0;
            spawnCostBypass = false;
            spawnGridId = 0;
            hudNotify?.Hide();
            hudNotify = null;
            hudShown = false;
            spawnError = SpawnError.None;
            playerWallet = 0;
            factionWallet = 0;
            enableInput = false;
            Utils.RemoveSpawnLocationsClientGPS();
            inGridPlacementView = false;
            nearbyGrids.Clear();
        }

        /// <summary>
        /// Server: a placed (not original-location) unhangar request. The client only names the hangar slot and
        /// the main grid's placement; grids, spawn type, enemy check and cost are all decided here.
        /// On any rejection nothing spawns and nothing is charged.
        /// </summary>
        public void HandleSpawnRequest(ObjectContainer request)
        {
            long playerId = request.playerId;
            IMyPlayer player = GetPlayerfromID(playerId);
            if (player == null) return;

            GridData gridData;
            MyObjectBuilder_Definitions blueprint;
            if (!Utils.TryGetRetrievableGrid(playerId, request.hangarType, request.intValue, out gridData, out blueprint)) return;

            if (gridData.gridId != request.gridId)
            {
                Utils.Reject(playerId, "That hangar slot has changed since the preview opened. Load the grid again.");
                return;
            }

            MyObjectBuilder_CubeGrid[] obs = Utils.GetBlueprintGrids(blueprint);
            if (obs == null || !obs[0].PositionAndOrientation.HasValue)
            {
                Utils.Reject(playerId, "Failed to load the stored grid.");
                return;
            }

            // Stored grid centre, taken before the blueprint is moved to the placement
            Vector3D original = SpawnRules.GetGridWorldCenter(obs[0]);
            double mass = SpawnRules.GetBlueprintMass(obs);
            if (!SpawnRules.ApplyPlacement(obs, request.placement))
            {
                Utils.Reject(playerId, "Invalid placement.");
                return;
            }

            Vector3D center = SpawnRules.GetGridWorldCenter(obs[0]);
            if (Vector3D.DistanceSquared(center, player.GetPosition()) > SpawnRules.MaxPlacementDistance * SpawnRules.MaxPlacementDistance)
            {
                Utils.Reject(playerId, "Placement is too far away from you.");
                return;
            }

            long cost;
            EnemyChecks enemyCheck;
            bool freeZone;
            SpawnType resolvedType = SpawnRules.Resolve(center, original, playerId, mass, out cost, out enemyCheck, out freeZone);
            if (resolvedType == SpawnType.None)
            {
                Utils.Reject(playerId, "Not a valid spawning location.");
                return;
            }

            if (!freeZone && SpawnRules.IsEnemyNear(center, enemyCheck, playerId))
            {
                Utils.Reject(playerId, "Enemy nearby, failed to spawn from hangar.");
                return;
            }

            if (gridData.autoHangared && config.autoHangarConfig.autoBypassSpawnCost)
                cost = 0;

            if (cost > 0)
            {
                long factionBalance = 0;
                long playerBalance = 0;
                IMyFaction faction = MyAPIGateway.Session.Factions.TryGetPlayerFaction(playerId);
                if (request.hangarType == HangarType.Faction && faction != null)
                    faction.TryGetBalanceInfo(out factionBalance);
                player.TryGetBalanceInfo(out playerBalance);

                if (!SpawnRules.CanAfford(cost, request.hangarType, factionBalance, playerBalance))
                {
                    Utils.Reject(playerId, $"Insufficient funds, spawning here costs {cost} SC.");
                    return;
                }
            }

            SpawnGridsFromOb(new List<MyObjectBuilder_CubeGrid>(obs), request.intValue, request.hangarType, playerId, cost, resolvedType, true);
        }

        // Server
        public void SpawnGridsFromOb(List<MyObjectBuilder_CubeGrid> obs, int index, HangarType sentHangarType, long playerId, long cost, SpawnType spawnType, bool checkForIntersections)
        {
            IMyPlayer player = GetPlayerfromID(playerId);
            IMyFaction faction = MyAPIGateway.Session.Factions.TryGetPlayerFaction(playerId);

            MyAPIGateway.Entities.RemapObjectBuilderCollection(obs);
            IMyCubeGrid mainGrid = null;
            List<MyCubeGrid> gridsToSpawn = new List<MyCubeGrid>();
            Utils.CheckGridSpawnLimitsInOB(obs, faction, sentHangarType, index, spawnType, playerId);

            for (int i = 0; i < obs.Count; i++)
            {
                MyObjectBuilder_CubeGrid cloneOb = obs[i].Clone() as MyObjectBuilder_CubeGrid;
                if (cloneOb == null) continue;

                cloneOb.CreatePhysics = true;
                IMyEntity ent = MyAPIGateway.Entities.CreateFromObjectBuilder(cloneOb);
                var cubeGrid = ent as MyCubeGrid;
                var grid = ent as IMyCubeGrid;
                if (cubeGrid == null || grid == null)
                {
                    // Created grids that never get added must be closed, or they leak
                    ent?.Close();
                    foreach (var created in gridsToSpawn)
                        created.Close();
                    MyVisualScriptLogicProvider.SendChatMessageColored($"Failed to spawn grid from hangar.", Color.Red, "[FactionHangar]", playerId, "Red");
                    return;
                }

                cubeGrid.Save = true;
                cubeGrid.SyncFlag = true;
                cubeGrid.IsPreview = false;

                if (i == 0)
                {
                    cubeGrid.Physics.AngularVelocity = Vector3.Zero;
                    cubeGrid.Physics.LinearVelocity = Vector3.Zero;
                    if (cubeGrid.GridSizeEnum == MyCubeSize.Small)
                    {
                        if (config.spawnSGStatic)
                            grid.IsStatic = true;
                    }else
                        grid.IsStatic = true;

                    mainGrid = cubeGrid;
                }

                if (sentHangarType == HangarType.Faction)
                    Utils.CheckOwnerValidFaction(faction, cubeGrid, playerId);

                gridsToSpawn.Add(cubeGrid);
            }

            if (checkForIntersections)
            {
                if (Utils.IsGridIntersecting(gridsToSpawn))
                {
                    foreach (var created in gridsToSpawn)
                        created.Close();
                    MyVisualScriptLogicProvider.SendChatMessageColored($"Failed to spawn grid from hangar, something is blocking placement.", Color.Red, "[FactionHangar]", playerId, "Red");
                    return;
                }
            }
            
            foreach(var grid in gridsToSpawn)
                MyAPIGateway.Entities.AddEntity(grid, true);

            IMyEntity foundEnt;
            if (mainGrid == null)
            {
                MyVisualScriptLogicProvider.SendChatMessageColored($"Failed to spawn grid from hangar.", Color.Red, "[FactionHangar]", playerId, "Red");
                return;
            }

            MyAPIGateway.Entities.TryGetEntityById(mainGrid.EntityId, out foundEnt);
            if (foundEnt == null)
            {
                MyVisualScriptLogicProvider.SendChatMessageColored($"Failed to spawn grid from hangar.", Color.Red, "[FactionHangar]", playerId, "Red");
                return;
            }

            if (sentHangarType == HangarType.Faction)
            {
                if (faction != null)
                {
                    Utils.CheckGridSpawnLimits(mainGrid, faction, sentHangarType, index, spawnType, playerId);
                    Utils.UpdateBalance(playerId, cost, index, sentHangarType);
                    FactionTimers.AddTimer(faction, TimerType.RetrievalCooldown, config.factionHangarConfig.factionRetrievalCooldown);
                    allHangarData.RemoveFactionData(faction.FactionId, index, true);
                }

                if (player != null)
                    Comms.AddClientCooldown(player.SteamUserId, false, TimerType.RetrievalCooldown);
            }

            if (sentHangarType == HangarType.Private)
            {
                Utils.CheckGridSpawnLimits(mainGrid, null, sentHangarType, index, spawnType, playerId);
                Utils.UpdateBalance(playerId, cost, index, sentHangarType);
                allHangarData.RemovePrivateData(playerId, index, true);
                privateRetrievalCooldownEnd[playerId] = ticks + config.privateHangarConfig.privateRetrievalCooldown * 60;
                if (player != null)
                    Comms.AddClientCooldown(player.SteamUserId, true, TimerType.RetrievalCooldown);
            }

            if (player != null)
            {
                IMyCubeGrid grid = gridsToSpawn[0] as IMyCubeGrid;
                if (grid == null) return;
                MyVisualScriptLogicProvider.SendChatMessageColored($"Successfully spawned in grid '{grid.CustomName}'.", Color.Green, "[FactionHangar]", playerId, "Green");
            }
        }

        private void RunClientTimers()
        {
            if (isServer && isDedicated) return;
            if (ticks % 60 != 0) return;

            if (retrievalTimer > 0)
                retrievalTimer--;

            if (storeTimer > 0)
                storeTimer--;
        }

        readonly List<IMyFaction> cooldownKeys = new List<IMyFaction>();

        private void RunDelayTimers()
        {
            if (ticks % 60 != 0) return;

            if (cooldownTimers.Count > 0)
            {
                cooldownKeys.Clear();
                cooldownKeys.AddRange(cooldownTimers.Keys);
                foreach (var faction in cooldownKeys)
                {
                    for (int i = cooldownTimers[faction].timers.Count - 1; i >= 0; i--)
                    {
                        if (cooldownTimers[faction].timers[i].time > 0)
                            cooldownTimers[faction].timers[i].time--;
                        else
                            cooldownTimers[faction].timers.RemoveAt(i);

                        if (cooldownTimers[faction].timers.Count == 0)
                            cooldownTimers.Remove(faction);
                    }
                }
            }
            
            if (hangarDelay.Count == 0) return;
            for (int i = hangarDelay.Count - 1; i >= 0; i--)
            {
                if (hangarDelay[i].hangarType == HangarType.Faction)
                {
                    // Added enemy nearby check while hangar delay is running
                    if (config.factionHangarConfig.factionStoreDelay - hangarDelay[i].timer % 5 != 0)
                    {
                        VRage.ModAPI.IMyEntity entity;
                        MyAPIGateway.Entities.TryGetEntityById(hangarDelay[i].gridData[0].gridId, out entity);
                        if (entity != null)
                        {
                            IMyCubeGrid grid = entity as IMyCubeGrid;
                            if (grid != null)
                            {
                                if (IsEnemyNearStoring(grid, hangarDelay[i].hangarType, hangarDelay[i].requesterId))
                                {
                                    MyVisualScriptLogicProvider.SendChatMessageColored($"Enemy is now to close to store, halting all storage requests", Color.Red, "[FactionHangar]", hangarDelay[i].playerId, "Red");
                                    Utils.RemoveGridDamageMontior(hangarDelay[i].gridData);
                                    hangarDelay.RemoveAtFast(i);
                                    continue;
                                }
                            }
                        }
                    }

                    if (hangarDelay[i].timer >= config.factionHangarConfig.factionStoreDelay)
                    {
                        foreach (var grid in hangarDelay[i].gridData)
                            RequestingGridStorage(hangarDelay[i].requesterId, hangarDelay[i].playerId, grid.gridId, hangarDelay[i].playerName);

                        hangarDelay.RemoveAtFast(i);
                        continue;
                    }
                }

                if (hangarDelay[i].hangarType == HangarType.Private)
                {
                    // Added enemy nearby check while hangar delay is running
                    if (config.privateHangarConfig.privateStoreDelay - hangarDelay[i].timer % 5 != 0)
                    {
                        VRage.ModAPI.IMyEntity entity;
                        MyAPIGateway.Entities.TryGetEntityById(hangarDelay[i].gridData[0].gridId, out entity);
                        if (entity != null)
                        {
                            IMyCubeGrid grid = entity as IMyCubeGrid;
                            if (grid != null)
                            {
                                if (IsEnemyNearStoring(grid, hangarDelay[i].hangarType, hangarDelay[i].requesterId))
                                {
                                    MyVisualScriptLogicProvider.SendChatMessageColored($"Enemy is now to close to store, halting all storage requests", Color.Red, "[FactionHangar]", hangarDelay[i].playerId, "Red");
                                    Utils.RemoveGridDamageMontior(hangarDelay[i].gridData);
                                    hangarDelay.RemoveAtFast(i);
                                    continue;
                                }
                            }
                        }
                    }

                    if (hangarDelay[i].timer >= config.privateHangarConfig.privateStoreDelay)
                    {
                        foreach (var grid in hangarDelay[i].gridData)
                            RequestingGridStorage(hangarDelay[i].requesterId, hangarDelay[i].playerId, grid.gridId, hangarDelay[i].playerName, true);

                        hangarDelay.RemoveAtFast(i);
                        continue;
                    }
                }

                hangarDelay[i].timer++;

                if (hangarDelay[i].hangarType == HangarType.Faction)
                {
                    if (config.factionHangarConfig.factionStoreDelay - hangarDelay[i].timer == 10)
                        foreach (var grid in hangarDelay[i].gridData)
                            MyVisualScriptLogicProvider.SendChatMessageColored($"Storing Grid {grid.gridName} in 10 seconds", Color.Green, "[FactionHangar]", hangarDelay[i].playerId, "Green");
                }
                else
                {
                    if (config.privateHangarConfig.privateStoreDelay - hangarDelay[i].timer == 10)
                        foreach (var grid in hangarDelay[i].gridData)
                            MyVisualScriptLogicProvider.SendChatMessageColored($"Storing Grid {grid.gridName} in 10 seconds", Color.Green, "[FactionHangar]", hangarDelay[i].playerId, "Green");
                }
            }
        }

        public void ChatHandler(string messageText, ref bool sendToOthers)
        {
            if (isDedicated) return;
            string[] words = messageText.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string prefix = words.Length > 0 ? GetHangarPrefix(words[0]) : null;
            if (prefix == null) return;

            // Hangar commands never reach public chat
            sendToOthers = false;
            IMyPlayer client = MyAPIGateway.Session.LocalHumanPlayer;
            if (inGridPlacementView)
            {
                Comms.SendChatMessage($"Command NOT allowed while in placement mode.", "Red", client.IdentityId, Color.Red);
                return;
            }

            string typed = messageText;
            words[0] = prefix;
            for (int i = 1; i < words.Length; i++)
                words[i] = words[i].ToLowerInvariant();
            messageText = string.Join(" ", words);

            if (words.Length == 1 || words[1] == "help")
            {
                Utils.LoadHelpPopup();
                return;
            }

            if (Array.IndexOf(prefix == "/fh" ? FactionSubcommands : PrivateSubcommands, words[1]) < 0)
            {
                Comms.SendChatMessage($"Unknown command '{typed}'. Type {prefix} help for the command list.", "Red", client.IdentityId, Color.Red);
                Utils.LoadHelpPopup();
                return;
            }

            // An index command without an index shows the list to pick from
            if (words.Length == 2 && (words[1] == "load" || words[1] == "transfer" || words[1] == "remove"))
            {
                if (prefix == "/fh")
                    Comms.ClientRequestFactionList(client.IdentityId, client.DisplayName);
                else
                    Comms.ClientRequestPrivateList(client.IdentityId, client.DisplayName);
                return;
            }

            IMyFaction faction = MyAPIGateway.Session.Factions.TryGetPlayerFaction(client.IdentityId);
            bool isLeader = false;
            if (faction != null)
                isLeader = faction.IsLeader(client.IdentityId);

            if (messageText.Equals("/fh list") || messageText.Equals("/factionhangar list"))
            {
                sendToOthers = false;
                Comms.ClientRequestFactionList(client.IdentityId, client.DisplayName);
     
                return;
            }

            if (messageText.StartsWith("/fh store") || messageText.StartsWith("/factionhangar store"))
            {
                List<MyEntity> entities = new List<MyEntity>();
                sendToOthers = false;

                if (faction == null)
                {
                    Comms.SendChatMessage("Need to be in a faction to store a grid in faction hangar.", "Red", client.IdentityId, Color.Red);
                    //MyVisualScriptLogicProvider.SendChatMessageColored($"Need to be in a faction to store a grid in faction hangar.", Color.Red, "[FactionHangar]", 0, "Red");
                    return;
                }

                if (storeTimer > 0)
                {
                    Comms.SendChatMessage($"Must wait {storeTimer} seconds before you can store another grid.", "Red", client.IdentityId, Color.Red);
                    //MyVisualScriptLogicProvider.SendChatMessageColored($"Must wait {storeTimer} seconds before you can store another grid.", Color.Red, "[FactionHangar]", 0, "Red");
                    return;
                }

                var split = messageText.Split(' ');
                string gridName = "";
                int gridIndex = -1;
                IMyCubeGrid choosenGrid = null;
                if (split.Length == 2)
                {
                    GetEntitiesInSphere(entities, playerCache.GetPosition(), 500);
                    string list = Utils.GetGridsToStore(entities, HangarType.Faction, client.IdentityId);
                    Comms.SendChatMessage($"{list}", "Green", client.IdentityId, Color.Green);
                    //MyVisualScriptLogicProvider.SendChatMessageColored($"{list}", Color.Green, "[FactionHangar]", 0, "Green");

                    return;
                }

                if (split.Length == 3)
                {
                    /*for (int i = 2; i < split.Length; i++)
                    {
                        if (i == 2)
                        {
                            gridName = split[i];
                            continue;
                        }

                        gridName += $" {split[i]}";
                    }*/

                    if (!int.TryParse(split[2], out gridIndex)) gridIndex = -1;

                    //GetEntitiesInSphere(entities, playerCache.GetPosition(), 500);
                    // The index must come from a faction list, not one shown by /ph store
                    if (gridIndex < 0 || gridIndex >= gridListToStore.Count || gridListType != HangarType.Faction)
                    {
                        string list = "Not a valid index...\n";
                        GetEntitiesInSphere(entities, playerCache.GetPosition(), 500);
                        list += Utils.GetGridsToStore(entities, HangarType.Faction, client.IdentityId);
                        Comms.SendChatMessage($"{list}", "Red", client.IdentityId, Color.Red);
                        //MyVisualScriptLogicProvider.SendChatMessageColored($"{list}", Color.Green, "[FactionHangar]", 0, "Green");

                        return;
                    }

                    choosenGrid = gridListToStore[gridIndex];


                    /*foreach (var ent in entities)
                    {
                        
                        IMyCubeGrid grid = ent as IMyCubeGrid;
                        if (grid == null || grid.Physics == null) continue;

                        if (gridName == grid.CustomName)
                        {
                            choosenGrid = grid;
                            break;
                        }
                    }*/

                    if (choosenGrid == null)
                    {
                        Comms.SendChatMessage($"{gridName} is NOT a valid grid.", "Red", client.IdentityId, Color.Red);
                        GetEntitiesInSphere(entities, playerCache.GetPosition(), 500);
                        string list = Utils.GetGridsToStore(entities, HangarType.Faction, client.IdentityId);
                        Comms.SendChatMessage($"{list}", "Red", client.IdentityId, Color.Red);

                        return;
                    }

                    long gridOwner = choosenGrid.BigOwners.FirstOrDefault();

                    if (faction != null)
                    {
                        if (!DoesFactionOwnGrid(choosenGrid, faction))
                        {
                            Comms.SendChatMessage($"Grid {choosenGrid.CustomName} is not owned by you or your faction", "Red", client.IdentityId, Color.Red);
                            //MyVisualScriptLogicProvider.SendChatMessageColored($"Grid {choosenGrid.CustomName} is not owned by you or your faction", Color.Red, "[FactionHangar]", 0, "Red");
                            return;
                        }
                        else
                        {
                            if (!isLeader)
                            {
                                if (!DoesPlayerOwnGrid(choosenGrid, client.IdentityId))
                                {
                                    Comms.SendChatMessage($"Grid {choosenGrid.CustomName} is not owned by you, must be a faction leader to store grids that you don't own", "Red", client.IdentityId, Color.Red);
                                    //MyVisualScriptLogicProvider.SendChatMessageColored($"Grid {choosenGrid.CustomName} is not owned by you, must be a faction leader to store grids that you don't own", Color.Red, "[FactionHangar]", 0, "Red");
                                    return;
                                }
                            }
                        }
                    }

                    if (IsEnemyNearStoring(choosenGrid, HangarType.Faction, client.IdentityId))
                    {
                        Comms.SendChatMessage($"Cannot store when enemy is nearby...", "Red", client.IdentityId, Color.Red);
                        //MyVisualScriptLogicProvider.SendChatMessageColored($"Cannot store when enemy is nearby...", Color.Red, "[FactionHangar]", 0, "Red");
                        return;
                    }

                    gridsToStore = new CacheGridsForStorage();
                    List<IMyCubeGrid> connectedGrids = new List<IMyCubeGrid>();
                    GetConnectedGrids(choosenGrid, connectedGrids);


                    if (connectedGrids.Count > 0)
                    {
                        gridsToStore.ownerId = gridOwner;
                        gridsToStore.requesterId = client.IdentityId;
                        gridsToStore.grids = new List<IMyCubeGrid>() { choosenGrid };

                        //gridsToStore.Add(choosenGrid);
                        string message = "The following grids are connected and will count as slots in the faction hangar:\n\n";
                        foreach (var grid in connectedGrids)
                        {
                            if (!DoesFactionOwnGrid(grid, faction)) continue;
                            if (!isLeader)
                                if (!DoesPlayerOwnGrid(grid, client.IdentityId)) continue;

                            gridsToStore.grids.Add(grid);
                            //gridsToStore.Add(grid);
                            message += $"{grid.CustomName}\n";
                        }

                        message += "\nPress 'Process' to proceed or cancel with the X on the top corner";

                        hangarType = HangarType.Faction;
                        MyAPIGateway.Utilities.ShowMissionScreen("Connected Grids Detected!", "", null, message, ConnectedGridsResult, "Process");
                        return;
                    }

                    List<GridData> gridDatas = new List<GridData>();
                    GridData gridData = new GridData()
                    {
                        gridId = choosenGrid.EntityId,
                        gridName = choosenGrid.CustomName
                    };

                    string ownerName = GetPlayerName(gridOwner);
                    gridDatas.Add(gridData);
                    //storeTimer = config.factionHangarConfig.factionHangarCooldown;
                    //Comms.AddTimer(faction.FactionId, TimerType.StorageCooldown, config.factionHangarConfig.factionHangarCooldown);
                    hangarType = HangarType.Faction;
                    Comms.ClientRequestStoreGrid(client.IdentityId, gridOwner, gridDatas, ownerName, HangarType.Faction);
                }
            }

            if (messageText.StartsWith("/fh load") || messageText.StartsWith("/factionhangar load"))
            {
                sendToOthers = false;
                if (faction == null)
                {
                    Comms.SendChatMessage($"Need to be in a faction to load a grid in faction hangar.", "Red", client.IdentityId, Color.Red);
                    //MyVisualScriptLogicProvider.SendChatMessageColored($"Need to be in a faction to load a grid in faction hangar.", Color.Red, "[FactionHangar]", client.IdentityId, "Red");
                    return;
                }

                if (retrievalTimer > 0)
                {
                    Comms.SendChatMessage($"Must wait {retrievalTimer} seconds before you can load another grid.", "Red", client.IdentityId, Color.Red);
                    //MyVisualScriptLogicProvider.SendChatMessageColored($"Must wait {retrievalTimer} seconds before you can load another grid.", Color.Red, "[FactionHangar]", client.IdentityId, "Red");
                    return;
                }

                var split = messageText.Split(' ');
                bool originalLocation = false;
                bool force = false;
                string gridIndex = "";
                int index = -1;

                if (split.Length <= 2)
                {
                    Comms.SendChatMessage("Invalid index.", "Red", client.IdentityId, Color.Red);
                    //MyVisualScriptLogicProvider.SendChatMessageColored($"Invalid index.", Color.Red, "[FactionHangar]", client.IdentityId, "Red");
                    return;
                }

                gridIndex = split[2];

                if (!int.TryParse(gridIndex, out index))
                {
                    var splitBool = split[2].Split('.');
                    if (splitBool.Length > 1)
                        bool.TryParse(splitBool[1], out originalLocation);

                    if (splitBool.Length > 2)
                        if (splitBool[2].Equals("force", StringComparison.OrdinalIgnoreCase))
                        {
                            if (originalLocation)
                                force = true;
                            else
                            {
                                Comms.SendChatMessage("Force command is only allowed when spawning to original location.", "Red", client.IdentityId, Color.Red);
                                return;
                            }
                        }

                    if (!int.TryParse(splitBool[0], out index))
                    {
                        Comms.SendChatMessage("Invalid index.", "Red", client.IdentityId, Color.Red);
                        return;
                    }
                }

                if (index < 0)
                {
                    Comms.SendChatMessage("Invalid index.", "Red", client.IdentityId, Color.Red);
                    //MyVisualScriptLogicProvider.SendChatMessageColored($"Invalid index.", Color.Red, "[FactionHangar]", client.IdentityId, "Red");
                    return;
                }



                hangarType = HangarType.Faction;
                Comms.ClientRequestGridData(index, client.IdentityId, HangarType.Faction, client.SteamUserId, originalLocation, force);
            }

            if (messageText.StartsWith("/fh transfer") || messageText.StartsWith("/factionhangar transfer"))
            {
                sendToOthers = false;
                if (faction == null)
                {
                    Comms.SendChatMessage("Need to be in a faction to transfer a grid in to private hangar.", "Red", client.IdentityId, Color.Red);
                    //MyVisualScriptLogicProvider.SendChatMessageColored($"Need to be in a faction to transfer a grid in to private hangar.", Color.Red, "[FactionHangar]", client.IdentityId, "Red");
                    return;
                }

                var split = messageText.Split(' ');

                string gridIndex = "";
                int index = -1;

                if (split.Length <= 1)
                {
                    Comms.SendChatMessage("Invalid index.", "Red", client.IdentityId, Color.Red);
                    //MyVisualScriptLogicProvider.SendChatMessageColored($"Invalid index.", Color.Red, "[FactionHangar]", client.IdentityId, "Red");
                    return;
                }

                gridIndex = split[2];
                if (!int.TryParse(gridIndex, out index) || index < 0)
                {
                    Comms.SendChatMessage("Invalid index.", "Red", client.IdentityId, Color.Red);
                    //MyVisualScriptLogicProvider.SendChatMessageColored($"Invalid index.", Color.Red, "[FactionHangar]", client.IdentityId, "Red");
                    return;
                }

                Comms.RequestTransferFactionToPrivate(client.IdentityId, index);
                //allHangarData.TransferFactionToPrivate(faction.FactionId, index, client.IdentityId);
            }

            if (messageText.StartsWith("/fh remove") || messageText.StartsWith("/factionhangar remove"))
            {
                sendToOthers = false;
                if (faction == null)
                {
                    Comms.SendChatMessage("Need to be in a faction to remove grids from hangar.", "Red", client.IdentityId, Color.Red);
                    //MyVisualScriptLogicProvider.SendChatMessageColored($"Need to be in a faction to remove grids from hangar.", Color.Red, "[FactionHangar]", client.IdentityId, "Red");
                    return;
                }

                var split = messageText.Split(' ');

                string gridIndex = "";
                int index = -1;

                if (split.Length <= 1)
                {
                    Comms.SendChatMessage("Invalid index.", "Red", client.IdentityId, Color.Red);
                    //MyVisualScriptLogicProvider.SendChatMessageColored($"Invalid index.", Color.Red, "[FactionHangar]", client.IdentityId, "Red");
                    return;
                }

                gridIndex = split[2];
                if (!int.TryParse(gridIndex, out index) || index < 0)
                {
                    Comms.SendChatMessage("Invalid index.", "Red", client.IdentityId, Color.Red);
                    //MyVisualScriptLogicProvider.SendChatMessageColored($"Invalid index.", Color.Red, "[FactionHangar]", client.IdentityId, "Red");
                    return;
                }

                Comms.RequestGridRemoval(index, client.IdentityId, HangarType.Faction);
            }

            if (messageText.StartsWith("/ph store") || messageText.StartsWith("/privatehangar store"))
            {
                List<MyEntity> entities = new List<MyEntity>();
                sendToOthers = false;
                if (storeTimer > 0)
                {
                    Comms.SendChatMessage($"Must wait {storeTimer} seconds before you can store another grid.", "Red", client.IdentityId, Color.Red);
                    return;
                }

                var split = messageText.Split(' ');
                string gridName = "";
                int gridIndex = -1;
                IMyCubeGrid choosenGrid = null;
                if (split.Length == 2)
                {
                    GetEntitiesInSphere(entities, playerCache.GetPosition(), 500);
                    string list = Utils.GetGridsToStore(entities, HangarType.Private, client.IdentityId);
                    Comms.SendChatMessage($"{list}", "Green", client.IdentityId, Color.Green);

                    return;
                }

                if (split.Length == 3)
                {
                    if (!int.TryParse(split[2], out gridIndex)) gridIndex = -1;

                    if (gridIndex < 0 || gridIndex >= gridListToStore.Count || gridListType != HangarType.Private)
                    {
                        string list = "Not a valid index...\n";
                        GetEntitiesInSphere(entities, playerCache.GetPosition(), 500);
                        list += Utils.GetGridsToStore(entities, HangarType.Private, client.IdentityId);
                        Comms.SendChatMessage($"{list}", "Red", client.IdentityId, Color.Red);

                        return;
                    }

                    choosenGrid = gridListToStore[gridIndex];

                    if (choosenGrid == null)
                    {
                        Comms.SendChatMessage($"{gridName} is NOT a valid grid.", "Red", client.IdentityId, Color.Red);
                        GetEntitiesInSphere(entities, playerCache.GetPosition(), 500);
                        string list = Utils.GetGridsToStore(entities, HangarType.Private, client.IdentityId);
                        Comms.SendChatMessage($"{list}", "Red", client.IdentityId, Color.Red);

                        return;
                    }

                    if (!DoesPlayerOwnGrid(choosenGrid, client.IdentityId))
                    {
                        Comms.SendChatMessage($"Grid {choosenGrid.CustomName} is not owned by you.", "Red", client.IdentityId, Color.Red);
                        //MyVisualScriptLogicProvider.SendChatMessageColored($"Grid {choosenGrid.CustomName} is not owned by you.", Color.Red, "[FactionHangar]", client.IdentityId, "Red");
                        return;
                    }

                    if (IsEnemyNearStoring(choosenGrid, HangarType.Private, client.IdentityId))
                    {
                        Comms.SendChatMessage($"Cannot store when enemy is nearby.", "Red", client.IdentityId, Color.Red);
                        return;
                    }

                    gridsToStore = new CacheGridsForStorage();
                    List<IMyCubeGrid> connectedGrids = new List<IMyCubeGrid>();
                    GetConnectedGrids(choosenGrid, connectedGrids);


                    if (connectedGrids.Count > 0)
                    {
                        gridsToStore.ownerId = client.IdentityId;
                        gridsToStore.requesterId = client.IdentityId;
                        gridsToStore.grids = new List<IMyCubeGrid>() { choosenGrid };

                        //gridsToStore.Add(choosenGrid);
                        string message = "The following grids are connected and will count as slots in the private hangar:\n\n";
                        foreach (var grid in connectedGrids)
                        {
                            // Make sure and check ownership for ALL connected grids
                            if (!DoesPlayerOwnGrid(grid, client.IdentityId)) continue;
                            gridsToStore.grids.Add(grid);
                            //gridsToStore.Add(grid);
                            message += $"{grid.CustomName}\n";
                        }

                        message += "\nPress 'Process' to proceed or cancel with the X on the top corner";

                        hangarType = HangarType.Private;
                        MyAPIGateway.Utilities.ShowMissionScreen("Connected Grids Detected!", "", null, message, ConnectedGridsResult, "Process");
                        return;
                    }

                    List<GridData> gridDatas = new List<GridData>();
                    GridData gridData = new GridData()
                    {
                        gridId = choosenGrid.EntityId,
                        gridName = choosenGrid.CustomName
                    };

                    gridDatas.Add(gridData);
                    hangarType = HangarType.Private;
                    Comms.ClientRequestStoreGrid(client.IdentityId, client.IdentityId, gridDatas, client.DisplayName, HangarType.Private);
                }
            }

            if (messageText.StartsWith("/ph load") || messageText.StartsWith("/privatehangar load"))
            {
                sendToOthers = false;

                if (retrievalTimer > 0)
                {
                    Comms.SendChatMessage($"Must wait {retrievalTimer} seconds before you can load another grid.", "Red", client.IdentityId, Color.Red);
                    //MyVisualScriptLogicProvider.SendChatMessageColored($"Must wait {retrievalTimer} seconds before you can load another grid.", Color.Red, "[FactionHangar]", client.IdentityId, "Red");
                    return;
                }

                var split = messageText.Split(' ');

                bool originalLocation = false;
                bool force = false;
                string gridIndex = "";
                int index = -1;

                if (split.Length <= 2)
                {
                    Comms.SendChatMessage("Invalid index.", "Red", client.IdentityId, Color.Red);
                    return;
                }

                gridIndex = split[2];
                if (!int.TryParse(gridIndex, out index))
                {
                    var splitBool = split[2].Split('.');
                    if (splitBool.Length > 1)
                        bool.TryParse(splitBool[1], out originalLocation);

                    if (splitBool.Length > 2)
                        if (splitBool[2].Equals("force", StringComparison.OrdinalIgnoreCase))
                        {
                            if (originalLocation)
                                force = true;
                            else
                            {
                                Comms.SendChatMessage("Force command is only allowed when spawning to original location.", "Red", client.IdentityId, Color.Red);
                                return;
                            }
                        }

                    if (!int.TryParse(splitBool[0], out index))
                    {
                        Comms.SendChatMessage("Invalid index.", "Red", client.IdentityId, Color.Red);
                        return;
                    }
                }

                if (index < 0)
                {
                    Comms.SendChatMessage("Invalid index.", "Red", client.IdentityId, Color.Red);
                    return;
                }

                hangarType = HangarType.Private;
                Comms.ClientRequestGridData(index, client.IdentityId, HangarType.Private, client.SteamUserId, originalLocation, force);
            }

            if (messageText.StartsWith("/ph transfer") || messageText.StartsWith("/privatehangar transfer"))
            {
                sendToOthers = false;
                if (faction == null)
                {
                    Comms.SendChatMessage("Need to be in a faction to transfer a grid in to faction hangar.", "Red", client.IdentityId, Color.Red);
                    //MyVisualScriptLogicProvider.SendChatMessageColored($"Need to be in a faction to transfer a grid in to faction hangar.", Color.Red, "[FactionHangar]", client.IdentityId, "Red");
                    return;
                }

                var split = messageText.Split(' ');

                string gridIndex = "";
                int index = -1;

                if (split.Length <= 1)
                {
                    Comms.SendChatMessage("Invalid index.", "Red", client.IdentityId, Color.Red);
                    //MyVisualScriptLogicProvider.SendChatMessageColored($"Invalid index.", Color.Red, "[FactionHangar]", client.IdentityId, "Red");
                    return;
                }

                gridIndex = split[2];
                if (!int.TryParse(gridIndex, out index) || index < 0)
                {
                    Comms.SendChatMessage("Invalid index.", "Red", client.IdentityId, Color.Red);
                    //MyVisualScriptLogicProvider.SendChatMessageColored($"Invalid index.", Color.Red, "[FactionHangar]", client.IdentityId, "Red");
                    return;
                }

                Comms.RequestTransferPrivateToFaction(client.IdentityId, index);
                //allHangarData.TransferPrivateToFaction(faction.FactionId, index, client.IdentityId);
            }

            if (messageText.Equals("/ph list") || messageText.Equals("/privatehangar list"))
            {
                sendToOthers = false;
                Comms.ClientRequestPrivateList(client.IdentityId, client.DisplayName);

                return;
            }

            if (messageText.StartsWith("/ph remove") || messageText.StartsWith("/privatehangar remove"))
            {
                sendToOthers = false;

                var split = messageText.Split(' ');

                string gridIndex = "";
                int index = -1;

                if (split.Length <= 1)
                {
                    Comms.SendChatMessage("Invalid index.", "Red", client.IdentityId, Color.Red);
                    //MyVisualScriptLogicProvider.SendChatMessageColored($"Invalid index.", Color.Red, "[FactionHangar]", client.IdentityId, "Red");
                    return;
                }

                gridIndex = split[2];
                if (!int.TryParse(gridIndex, out index) || index < 0)
                {
                    Comms.SendChatMessage("Invalid index.", "Red", client.IdentityId, Color.Red);
                    //MyVisualScriptLogicProvider.SendChatMessageColored($"Invalid index.", Color.Red, "[FactionHangar]", client.IdentityId, "Red");
                    return;
                }

                Comms.RequestGridRemoval(index, client.IdentityId, HangarType.Private);
                //allHangarData.RemovePrivateData(client.IdentityId, index, true);
            }

            if (messageText.Equals("/fh togglesphere"))
            {
                sendToOthers = false;
                drawClientSphereDebug = !drawClientSphereDebug;
                Comms.SendChatMessage($"Client spawn spheres are now set to '{drawClientSphereDebug}'", "Green", client.IdentityId, Color.Green);
                //MyVisualScriptLogicProvider.SendChatMessageColored($"Client spawn spheres are now set to '{drawClientSphereDebug}'", Color.Green, "[FactionHangar]", client.IdentityId, "Green");
            }

            if (messageText.Equals("/fh togglegps"))
            {
                sendToOthers = false;
                spawnClientGPS = !spawnClientGPS;
                Comms.SendChatMessage($"Client spawn gps locations are now set to '{spawnClientGPS}'", "Green", client.IdentityId, Color.Green);
                //MyVisualScriptLogicProvider.SendChatMessageColored($"Client spawn gps locations are now set to '{spawnClientGPS}'", Color.Green, "[FactionHangar]", client.IdentityId, "Green");

            }
        }

        static readonly string[] FactionSubcommands = { "list", "store", "load", "transfer", "remove", "togglesphere", "togglegps" };
        static readonly string[] PrivateSubcommands = { "list", "store", "load", "transfer", "remove" };

        /// <summary>"/fh" or "/ph" for a hangar command word (long forms included), otherwise null.</summary>
        static string GetHangarPrefix(string word)
        {
            word = word.ToLowerInvariant();
            if (word == "/fh" || word == "/factionhangar") return "/fh";
            if (word == "/ph" || word == "/privatehangar") return "/ph";
            return null;
        }

        private void ConnectedGridsResult(ResultEnum result)
        {
            if (result == ResultEnum.OK)
            {
                IMyPlayer client = MyAPIGateway.Session.LocalHumanPlayer;
                List<GridData> gridDatas= new List<GridData>();
                foreach (var grid in gridsToStore.grids)
                {
                    GridData gridData = new GridData()
                    {
                        gridId = grid.EntityId,
                        gridName = grid.CustomName
                    };

                    gridDatas.Add(gridData);
                }

                string ownerName = GetPlayerName(gridsToStore.ownerId);
                //storeTimer = hangarType == HangarType.Faction ? config.factionHangarConfig.factionHangarCooldown : config.privateHangarConfig.privateHangarCooldown;
                
                /*if (hangarType == HangarType.Faction)
                {
                    IMyFaction faction = MyAPIGateway.Session.Factions.TryGetPlayerFaction(client.IdentityId);
                    if (faction != null)
                        Comms.AddTimer(faction.FactionId, TimerType.StorageCooldown, storeTimer);
                }*/
                    
                Comms.ClientRequestStoreGrid(client.IdentityId, gridsToStore.ownerId, gridDatas, ownerName, hangarType);
            }
        }

        public void GetConnectedGrids(IMyCubeGrid targetGrid, List<IMyCubeGrid> connectedGrids)
        {
            List<IMyCubeGrid> logicalGroup = new List<IMyCubeGrid>();
            MyAPIGateway.GridGroups.GetGroup(targetGrid, GridLinkTypeEnum.Logical, logicalGroup);
            foreach (var grid in logicalGroup)
            {
                if (!targetGrid.IsSameConstructAs(grid))
                    connectedGrids.Add(grid);
            }

            // Removing subgrids from hangar slots count ONLY
            for (int i = connectedGrids.Count - 1; i >= 0; i--)
            {
                var blocks = connectedGrids[i].GetFatBlocks<IMyAttachableTopBlock>();
                bool attached = false;
                foreach(var top in blocks)
                {
                    if (top.IsAttached)
                    {
                        attached = true;
                        break;
                    }
                }
                    
                if (attached)
                    connectedGrids.RemoveAt(i);
            }
        }

        public bool DoesPlayerOwnGrid(IMyCubeGrid grid, long playerId)
        {
            if (grid == null) return false;
            long owner = 0;
            if (grid.BigOwners.Count != 0)
                owner = grid.BigOwners[0];

            if (owner != 0 && owner == playerId) return true;

            return false;
        }

        public bool DoesFactionOwnGrid(IMyCubeGrid grid, IMyFaction faction)
        {
            if (grid == null) return false;
            long owner = 0;
            if (grid.BigOwners.Count != 0)
                owner = grid.BigOwners[0];

            IMyFaction gridFaction = MyAPIGateway.Session.Factions.TryGetPlayerFaction(owner);
            if (gridFaction != null && gridFaction == faction) return true;

            return false;
        }

        public void RequestingGridStorage(long requesterId, long ownerId, long gridId, string playerName, bool privateStorage = false, bool autoHangar = false)
        {
            IMyPlayer player = GetPlayerfromID(requesterId);
            IMyPlayer gridOwner = GetPlayerfromID(ownerId);
            /*if (player == null)
            {
                MyLog.Default.WriteLineAndConsole($"[FactionHangar] - Invalid Player");
                return;
            }*/

            IMyEntity entity = null;
            MyAPIGateway.Entities.TryGetEntityById(gridId, out entity);
            if (entity == null)
            {
                if (player != null)
                    MyVisualScriptLogicProvider.SendChatMessageColored($"Failed to store grid", Color.Red, "[FactionHangar]", requesterId, "Red");

                MyLog.Default.WriteLineAndConsole($"[FactionHangar] - Unable to get grid by Id {gridId}");
                return;
            }

            IMyCubeGrid grid = entity as IMyCubeGrid;
            if (grid == null) return;

            // Ownership or faction membership can change during the store delay
            string reason;
            if (!autoHangar && !Utils.CanStoreGrid(grid, requesterId, privateStorage ? HangarType.Private : HangarType.Faction, out reason))
            {
                Utils.Reject(requesterId, $"Failed to store grid. {reason}");
                (grid as MyCubeGrid).OnGridBlockDamaged -= Utils.GridDamageMonitor;
                return;
            }

            IMyFaction faction = MyAPIGateway.Session.Factions.TryGetPlayerFaction(ownerId);

            playerName = RemoveSpecialCharacters(playerName);
            string gridName = RemoveSpecialCharacters(grid.CustomName);

            string path = "";
            path = Path.Combine(MyAPIGateway.Utilities.GamePaths.UserDataPath, $"FactionHangarSaves", $"{playerName}", $"{gridName}_{grid.EntityId}", "bp.sbc");
            //path = Path.Combine("C:\\", $"FactionHangarSaves", $"{playerName}", $"{gridName}_{grid.EntityId}", "bp.sbc");

            /*if (faction != null)
            {
                if (privateStorage)
                    path = Path.Combine(MyAPIGateway.Utilities.GamePaths.ModsPath, $"FactionHangarSaves", $"PrivateHangars", $"{playerName}", $"{grid.CustomName}_{grid.EntityId}","bp.sbc");
                    //path = Path.Combine(MyAPIGateway.Utilities.GamePaths.ModsPath, $"{faction.Name}_{grid.CustomName}_{grid.EntityId}.sbc");

                else
                    path = Path.Combine(MyAPIGateway.Utilities.GamePaths.ModsPath, $"FactionHangarSaves", $"FactionHangars", $"{faction.Name}", $"{grid.CustomName}_{grid.EntityId}", "bp.sbc");
                    //path = Path.Combine(MyAPIGateway.Utilities.GamePaths.ModsPath, $"{faction.Name}_{grid.CustomName}_{grid.EntityId}.sbc");

            }
            else
                path = Path.Combine(MyAPIGateway.Utilities.GamePaths.ModsPath, $"FactionHangarSaves", $"PrivateHangars", $"{playerName}", $"{grid.CustomName}_{grid.EntityId}", "bp.sbc");
                //path = Path.Combine(MyAPIGateway.Utilities.GamePaths.ModsPath, $"{playerName}_{grid.CustomName}_{grid.EntityId}.sbc");*/


            if (path != "")
            {
                Utils.RemovePlayersFromSeats(grid as MyCubeGrid);

                if (CreateShipBlueprint(grid as MyCubeGrid, grid.CustomName, path, autoHangar ? GridLinkTypeEnum.Physical : GridLinkTypeEnum.Mechanical))
                {
                    if (player != null && !autoHangar)
                    {
                        //Comms.AddClientCooldown(player.SteamUserId, privateStorage, TimerType.StorageCooldown);
                        MyVisualScriptLogicProvider.SendChatMessageColored($"Successfully stored grid {grid.CustomName}", Color.Green, "[FactionHangar]", requesterId, "Green");
                        MyLog.Default.WriteLineAndConsole($"[FactionHangar] - Player {playerName} successfully stored grid {grid.CustomName}");
                    }

                    //if (faction != null && !autoHangar)
                        //FactionTimers.AddTimer(faction, TimerType.StorageCooldown, config.factionHangarConfig.factionHangarCooldown);

                    if (autoHangar)
                        MyLog.Default.WriteLineAndConsole($"[FactionHangar] - AutoHangar successfully stored grid {grid.CustomName}");

                    if (!privateStorage && faction != null)
                        allHangarData.AddFactionData(faction.FactionId, grid.CustomName, grid.EntityId, ownerId, path, playerName, autoHangar);
                    else
                        allHangarData.AddPrivateData(grid.CustomName, grid.EntityId, ownerId, path, playerName, autoHangar);

                    if (!autoHangar)
                    {
                        MyCubeGrid cubeGrid = grid as MyCubeGrid;
                        cubeGrid.OnGridBlockDamaged -= Utils.GridDamageMonitor;
                    }
                    
                    CloseGridGroup(grid, autoHangar ? GridLinkTypeEnum.Physical : GridLinkTypeEnum.Mechanical);
                    return;
                }
            }

            var failedGrid = grid as MyCubeGrid;
            if (failedGrid != null)
                failedGrid.OnGridBlockDamaged -= Utils.GridDamageMonitor;

            if (autoHangar)
                MyLog.Default.WriteLineAndConsole($"[FactionHangar] - AutoHangar failed to store grid {grid.CustomName}");
            else
                MyLog.Default.WriteLineAndConsole($"[FactionHangar] - Player {playerName} failed to store grid {grid.CustomName}");

            if (player != null)
                MyVisualScriptLogicProvider.SendChatMessageColored($"Failed to store grid {grid.CustomName}", Color.Red, "[FactionHangar]", requesterId, "Red");
        }

        public string RemoveSpecialCharacters(string str)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in str)
            {
                if ((c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '.' || c == '_')
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        private void CloseGridGroup(IMyCubeGrid grid, GridLinkTypeEnum groupType)
        {
            List<IMyCubeGrid> myCubeGrids= new List<IMyCubeGrid>();
            GetGroupByType(grid, myCubeGrids, groupType);
            foreach(var cubeGrid in myCubeGrids)
            {
                if (!cubeGrid.MarkedForClose)
                    cubeGrid.Close();
            }
        }

        public bool CreateShipBlueprint(MyCubeGrid myCubeGrid, string blueprintName, string path, GridLinkTypeEnum groupType)
        {
            List<MyObjectBuilder_CubeGrid> list = GetGridGroupObs(myCubeGrid, groupType);
            MyObjectBuilder_ShipBlueprintDefinition myObjectBuilder_ShipBlueprintDefinition = MyObjectBuilderSerializer.CreateNewObject<MyObjectBuilder_ShipBlueprintDefinition>();
            myObjectBuilder_ShipBlueprintDefinition.Id = new MyDefinitionId(new MyObjectBuilderType(typeof(MyObjectBuilder_ShipBlueprintDefinition)), MyUtils.StripInvalidChars(blueprintName));
            myObjectBuilder_ShipBlueprintDefinition.CubeGrids = list.ToArray();
            myObjectBuilder_ShipBlueprintDefinition.RespawnShip = false;
            myObjectBuilder_ShipBlueprintDefinition.DisplayName = blueprintName;
            myObjectBuilder_ShipBlueprintDefinition.CubeGrids[0].DisplayName = blueprintName;
            MyObjectBuilder_Definitions myObjectBuilder_Definitions = MyObjectBuilderSerializer.CreateNewObject<MyObjectBuilder_Definitions>();
            myObjectBuilder_Definitions.ShipBlueprints = new MyObjectBuilder_ShipBlueprintDefinition[1];
            myObjectBuilder_Definitions.ShipBlueprints[0] = myObjectBuilder_ShipBlueprintDefinition;

            // The grid is deleted after this, so only report success if the file was really written
            try
            {
                return MyObjectBuilderSerializer.SerializeXML(path, false, myObjectBuilder_Definitions);
            }
            catch (Exception ex)
            {
                MyLog.Default.WriteLineAndConsole($"[FactionHangar] - Could not write stored grid {path}: {ex.Message}");
                return false;
            }
        }

        public  List<MyObjectBuilder_CubeGrid> GetGridGroupObs(MyCubeGrid cubeGrid, GridLinkTypeEnum groupType)
        {
            List<MyObjectBuilder_CubeGrid> tmp = new List<MyObjectBuilder_CubeGrid>();
            List<IMyCubeGrid> groupList = new List<IMyCubeGrid>();
            MyAPIGateway.GridGroups.GetGroup(cubeGrid, groupType, groupList);
            tmp.Add(cubeGrid.GetObjectBuilder() as MyObjectBuilder_CubeGrid);

            foreach (var grid in groupList)
            {
                if (grid == cubeGrid) continue;
                tmp.Add(grid.GetObjectBuilder() as MyObjectBuilder_CubeGrid);
            }

            return tmp;
        }

        public void GetGroupByType(IMyCubeGrid grid, List<IMyCubeGrid> gridList, GridLinkTypeEnum type)
        {
            gridList.Clear();
            MyAPIGateway.GridGroups.GetGroup(grid, type, gridList);
        }

        public MyObjectBuilder_CubeGrid[] GetGridFromGridData(MyObjectBuilder_Base data, int index, HangarType sentHangarType)
        {
            spawnIndex = index;
            hangarType = sentHangarType;
            return Utils.GetBlueprintGrids(data as MyObjectBuilder_Definitions);
        }

        public void SpawnClientSideProjectedGrid(MyObjectBuilder_CubeGrid[] cubeGridObs)
        {
            if (cubeGridObs == null || cubeGridObs.Length == 0) return;

            RotationSpeed = 0.01f;
            previewDistance = 50;
            // Nearby zone is centred on the stored grid's centre, the same point placements are measured from
            original = SpawnRules.GetGridWorldCenter(cubeGridObs[0]);
            hudNotify = MyAPIGateway.Utilities.CreateNotification($"[Valid Spawn] = {allowSpawn} | Cost To Spawn = {spawnCost} SC", int.MaxValue, "White");
            hudNotify.Show();
            hudNotify.ResetAliveTime();
            UpdatePosition();
            DetectSpawnType(true);
            Utils.AddSpawnLocationsClientGPS();

            MyAPIGateway.Entities.RemapObjectBuilderCollection(cubeGridObs);
            MatrixD baseMat = new MatrixD();
            if (cubeGridObs[0].PositionAndOrientation.HasValue)
                baseMat = cubeGridObs[0].PositionAndOrientation.Value.GetMatrix();

            if (baseMat == MatrixD.Zero)
            {
                AbortPreview(null);
                return;
            }

            if (cubeGridObs.Length > 1)
                AssignSubgridSpawnLocation(cubeGridObs, endCoordCache, baseMat);

            cubeGridObs[0].PositionAndOrientation = new MyPositionAndOrientation(endCoordCache, baseMat.Forward, baseMat.Up);
            List<MyCubeGrid> tempGrids = new List<MyCubeGrid>();
            for (int i = 0; i < cubeGridObs.Length; i++)
            {
                MyObjectBuilder_CubeGrid cloneOb = cubeGridObs[i].Clone() as MyObjectBuilder_CubeGrid;
                if (cloneOb == null) continue;

                cloneOb.CreatePhysics = false;

                IMyEntity ent = MyAPIGateway.Entities.CreateFromObjectBuilder(cloneOb);
                var cubeGrid = ent as MyCubeGrid;
                if (cubeGrid == null)
                {
                    AbortPreview(tempGrids);
                    return;
                }

                cubeGrid.Save = false;
                cubeGrid.SyncFlag = false;
                cubeGrid.IsPreview = true;
                tempGrids.Add(cubeGrid);
            }

            var massGatherWorkData = new MassGathererWorkData();
            massGatherWorkData.grids = tempGrids;
            massGatherWorkData.faction = MyAPIGateway.Session.Factions.TryGetPlayerFaction(playerCache.IdentityId);
            var massGatherTask = MyAPIGateway.Parallel.Start(massGatherWorkData.ScanGridMassAction, massGatherWorkData.ScanGridMassCallback, massGatherWorkData);
        }

        /// <summary>Undoes a half-built preview and tells the player, instead of leaving them with nothing.</summary>
        private void AbortPreview(List<MyCubeGrid> builtGrids)
        {
            if (builtGrids != null)
                foreach (var grid in builtGrids)
                    grid.Close();

            Utils.RemoveSpawnLocationsClientGPS();
            RemovePreviewGrids();
            Comms.SendChatMessage("Failed to build the grid preview. Try loading it again.", "Red", playerCache.IdentityId, Color.Red);
        }

        public void AssignSubgridSpawnLocation(MyObjectBuilder_CubeGrid[] cubeGridObs, Vector3D spawnLoc, MatrixD baseMat)
        {
            if (baseMat == MatrixD.Zero) return;

            for (int i = 1; i < cubeGridObs.Length; i++)
            {
                MatrixD subMat = cubeGridObs[i].PositionAndOrientation.Value.GetMatrix();
                MatrixD newMat = MatrixD.CreateWorld(subMat.Translation - baseMat.Translation, subMat.Forward, subMat.Up);
                cubeGridObs[i].PositionAndOrientation = new MyPositionAndOrientation(spawnLoc + newMat.Translation, subMat.Forward, subMat.Up);
            }
        }

        public void GetEntitiesInSphere(List<MyEntity> ents, Vector3D center, double radius)
        {
            ents.Clear();
            //Vector3D center = MyAPIGateway.Session.LocalHumanPlayer.GetPosition();
            BoundingSphereD sphere = new BoundingSphereD(center, radius);
            MyGamePruningStructure.GetAllTopMostEntitiesInSphere(ref sphere, ents);
        }

        public IMyPlayer GetPlayerfromID(long playerId)
        {
            playerBuffer.Clear();
            MyAPIGateway.Players.GetPlayers(playerBuffer);
            IMyPlayer found = null;
            foreach (var player in playerBuffer)
            {
                if (player.IdentityId == playerId)
                {
                    found = player;
                    break;
                }
            }

            playerBuffer.Clear();
            return found;
        }

        public string GetPlayerName(long playerId)
        {
            foreach(var identity in allIdentities)
            {
                if (identity.IdentityId == playerId)
                    return identity.DisplayName;
            }

            // Players who joined after the startup identity snapshot
            return MyVisualScriptLogicProvider.GetPlayersName(playerId) ?? string.Empty;
        }

        /// <summary>
        /// Enemy block check (EnableBlockChecking): keeps a set of the configured block types that exist in the world.
        /// Grids are scanned when added and watched for block changes; cube blocks never raise MyEntities.OnEntityCreate.
        /// </summary>
        private void StartBlockTracking()
        {
            if (blockTracking || config == null || !config.enemyCheckConfig.enableBlockCheck) return;
            blockTracking = true;

            enemyCheckBlockIds.Clear();
            Utils.ParseBlockTypes(config.enemyCheckConfig.blockTypes, enemyCheckBlockIds, true);

            MyAPIGateway.Entities.OnEntityAdd += OnEntityAdded;
            var entities = new HashSet<IMyEntity>();
            MyAPIGateway.Entities.GetEntities(entities);
            foreach (var entity in entities)
                OnEntityAdded(entity);
        }

        private void StopBlockTracking()
        {
            MyAPIGateway.Entities.OnEntityAdd -= OnEntityAdded;
            foreach (var grid in trackedGrids)
            {
                grid.OnBlockAdded -= OnSlimBlockAdded;
                grid.OnBlockRemoved -= OnSlimBlockRemoved;
                grid.OnClose -= OnTrackedGridClosed;
            }

            trackedGrids.Clear();
            enemyBlockCheckList.Clear();
            blockTracking = false;
        }

        private void OnEntityAdded(IMyEntity entity)
        {
            IMyCubeGrid grid = entity as IMyCubeGrid;
            MyCubeGrid cubeGrid = entity as MyCubeGrid;
            if (grid == null || cubeGrid == null || cubeGrid.IsPreview || !trackedGrids.Add(grid)) return;

            grid.OnBlockAdded += OnSlimBlockAdded;
            grid.OnBlockRemoved += OnSlimBlockRemoved;
            grid.OnClose += OnTrackedGridClosed;
            foreach (var block in cubeGrid.GetFatBlocks())
                TrackBlock(block as IMyCubeBlock);
        }

        private void TrackBlock(IMyCubeBlock block)
        {
            if (block != null && enemyCheckBlockIds.Contains(block.BlockDefinition))
                enemyBlockCheckList.Add(block);
        }

        private void OnSlimBlockAdded(IMySlimBlock slim)
        {
            TrackBlock(slim.FatBlock);
        }

        private void OnSlimBlockRemoved(IMySlimBlock slim)
        {
            if (slim.FatBlock != null)
                enemyBlockCheckList.Remove(slim.FatBlock);
        }

        private void OnTrackedGridClosed(IMyEntity entity)
        {
            IMyCubeGrid grid = entity as IMyCubeGrid;
            if (grid == null || !trackedGrids.Remove(grid)) return;

            grid.OnBlockAdded -= OnSlimBlockAdded;
            grid.OnBlockRemoved -= OnSlimBlockRemoved;
            grid.OnClose -= OnTrackedGridClosed;
            enemyBlockCheckList.RemoveWhere(block => block.CubeGrid == grid);
        }

        public void ToolEquipped(long playerId, string typeId, string subTypeId)
        {
            if (isDedicated) return;
            if (previewGrids.Count == 0) return;
            if (!enableInput)
            {
                if (playerCache.Character.EquippedTool == null) return;
                MyAPIGateway.Parallel.StartBackground(() =>
                {
                    var controlEnt = playerCache.Character as Sandbox.Game.Entities.IMyControllableEntity;
                    MyAPIGateway.Utilities.InvokeOnGameThread(() => controlEnt?.SwitchToWeapon(null));
                });
            }
            else
                RemovePreviewGrids();
            
        }

        protected override void UnloadData()
        {
            //Instance = null;
            //MyAPIGateway.Multiplayer.UnregisterMessageHandler(NetworkHandle, MessageHandler);
            StopBlockTracking();
            Utils.ResetCaches();

            MyAPIGateway.Utilities.MessageEntered -= ChatHandler;
            MyAPIGateway.Multiplayer.UnregisterSecureMessageHandler(NetworkHandle, Comms.MessageHandler);
            Instance = null;

            if (!isDedicated)
                MyVisualScriptLogicProvider.ToolEquipped -= ToolEquipped;
        }

        public override void SaveData()
        {
            if (!isServer) return;
            try
            {
                using (var writer = MyAPIGateway.Utilities.WriteFileInWorldStorage("FactionHangarStorage.xml", typeof(AllHangarData)))
                {
                    writer.Write(MyAPIGateway.Utilities.SerializeToXML(allHangarData));
                    writer.Close();
                }
            }
            catch (Exception ex)
            {
                VRage.Utils.MyLog.Default.WriteLineAndConsole($"FactionHangar: Error trying to save hangar data!\n {ex.ToString()}");
                // Keep the removed files until the entries' removal is actually saved
                return;
            }

            // Blank removed grids' files one by one, so one bad path can't block the rest
            foreach (var path in cacheGridPaths)
            {
                try
                {
                    Utils.CreateNullShipBlueprint(path);
                }
                catch (Exception ex)
                {
                    VRage.Utils.MyLog.Default.WriteLineAndConsole($"FactionHangar: Could not clear removed grid file {path}: {ex.Message}");
                }
            }

            cacheGridPaths.Clear();
        }
    }

    public enum SpawnError
    {
        None,
        EnemyNearby,
        InsuffientFunds,
        EntityBlockingPlacement,
        InvalidPlacementInVoxel,
        InvalidSpawningLocation
    }
}