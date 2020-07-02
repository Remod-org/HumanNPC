#define DEBUG
// Requires: PathFinding
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Configuration;
using Oxide.Core.Plugins;
using Oxide.Game.Rust;
using Rust;
using Facepunch;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using Convert = System.Convert;

namespace Oxide.Plugins
{
    [Info("HumanNPC", "Reneb/Nogrod/Calytic/RFC1920/Nikedemos", "0.3.41", ResourceId = 856)]
    [Description("Adds interactive Human NPCs which can be modded by other plugins")]
    public class HumanNPC : RustPlugin
    {
        //////////////////////////////////////////////////////
        ///  Fields
        //////////////////////////////////////////////////////
        [PluginReference]
        private Plugin NPCPlay;

        private static Collider[] colBuffer;
        private int playerLayer;
        private static int targetLayer;
        private static Vector3 Vector3Down;
        private static int groundLayer;

        private Hash<ulong, HumanNPCInfo> humannpcs = new Hash<ulong, HumanNPCInfo>();

        // Nikedemos
        private Hash<ulong, HumanNPCTeamInfo> humannpcteams = new Hash<ulong, HumanNPCTeamInfo>();
        //private FoFLookup fofLookup = new FoFLookup(); //constructor is empty, don't generate yet, wait for all the NPCTeamInfo to be populated first!
        public static readonly Dictionary<HumanNPCAlignment, string> FoFEnumToString = new Dictionary<HumanNPCAlignment, string>();
        public static readonly Dictionary<string, HumanNPCAlignment> FoFStringToEnum = new Dictionary<string, HumanNPCAlignment>();
        // Nikedemos

        static int playerMask = LayerMask.GetMask("Player (Server)");
        //static int obstructionMask = LayerMask.GetMask(new[] { "Player (Server)", "Construction", "Deployed", "Clutter" });
        static int obstructionMask = LayerMask.GetMask(new[] { "Construction", "Deployed", "Clutter" });
        static int constructionMask = LayerMask.GetMask(new[] { "Construction", "Deployed", "Clutter" });
        static int terrainMask = LayerMask.GetMask(new[] { "Terrain", "Tree" });

        private bool save;
        private bool relocateZero = true;
        private StoredData storedData;
        private DynamicConfigFile data;
        private Vector3 eyesPosition;
        private string chat = "<color=#FA58AC>{0}:</color> ";

        [PluginReference]
        private Plugin Kits, Waypoints, Vanish, Pathfinding;

        private static PathFinding PathFinding;

        private class StoredData
        {
            public HashSet<HumanNPCInfo> HumanNPCs = new HashSet<HumanNPCInfo>();
            // Nikedemos
            public HashSet<HumanNPCTeamInfo> HumanNPCTeams = new HashSet<HumanNPCTeamInfo>();
        }

        public class WaypointInfo
        {
            public float Speed;
            public Vector3 Position;

            public WaypointInfo(Vector3 position, float speed)
            {
                Speed = speed;
                Position = position;
            }
        }

        public static bool IsLayerBlocked(Vector3 position, float radius, int mask)
        {
            var colliders = Pool.GetList<Collider>();
            Vis.Colliders<Collider>(position, radius, colliders, mask, QueryTriggerInteraction.Collide);

            bool blocked = colliders.Count > 0;

            Pool.FreeList<Collider>(ref colliders);

            return blocked;
        }

        //////////////////////////////////////////////////////
        ///  class SpawnInfo
        ///  Spawn information, position & rotation
        ///  public => will be saved in the data file
        ///  non public => won't be saved in the data file
        //////////////////////////////////////////////////////
        public class SpawnInfo
        {
            public Vector3 position;
            public Quaternion rotation;

            public SpawnInfo(Vector3 position, Quaternion rotation)
            {
                this.position = position;
                this.rotation = rotation;
            }

            public string String()
            {
                return $"Pos{position} - Rot{rotation}";
            }
            public string ShortString()
            {
                return $"Pos({Math.Ceiling(position.x)},{Math.Ceiling(position.y)},{Math.Ceiling(position.z)})";
            }
        }

        //////////////////////////////////////////////////////
        ///  class HumanTrigger
        /// MonoBehaviour: managed by UnityEngine
        ///  This takes care of all collisions and area management of humanNPCs
        //////////////////////////////////////////////////////
        public class HumanTrigger : MonoBehaviour
        {
            private HumanPlayer npc;

            private readonly HashSet<BasePlayer> triggerPlayers = new HashSet<BasePlayer>();
            private readonly HashSet<BaseAnimalNPC> triggerAnimals = new HashSet<BaseAnimalNPC>();

            public float collisionRadius;

            private void Awake()
            {
                npc = GetComponent<HumanPlayer>();
                collisionRadius = npc.info.collisionRadius;
                InvokeRepeating("UpdateTriggerArea", 2f, 1.5f);
            }

            private void OnDestroy()
            {
                CancelInvoke("UpdateTriggerArea");
            }

            private void UpdateTriggerArea()
            {
                var collidePlayers = new HashSet<BasePlayer>();
                var collideAnimals = new HashSet<BaseAnimalNPC>();

                List<BasePlayer> players = new List<BasePlayer>();
                List<BaseAnimalNPC> animals = new List<BaseAnimalNPC>();
                Vis.Entities<BasePlayer>(npc.player.transform.position, collisionRadius, players, targetLayer);
                Vis.Entities<BaseAnimalNPC>(npc.player.transform.position, collisionRadius, animals, targetLayer);

                foreach(var player in players.Distinct().ToList())
                {
                    //if(player.GetComponentInParent<HumanPlayer>()) continue;
                    collidePlayers.Add(player);
                    if(triggerPlayers.Add(player)) OnEnterCollision(player);
//#if DEBUG
//                    Interface.Oxide.LogInfo("UpdateTriggerArea: {0} found {1}", npc.player.displayName, player.displayName);
//#endif
                }
                foreach(var animal in animals.Distinct().ToList())
                {
                    collideAnimals.Add(animal);
                    if(triggerAnimals.Add(animal)) OnEnterCollision(animal);
#if DEBUG
                    Interface.Oxide.LogInfo("UpdateTriggerArea: {0} found {1}", npc.player.displayName, animal.ShortPrefabName);
#endif
                }

                var removePlayers = new HashSet<BasePlayer>();
                foreach(BasePlayer player in triggerPlayers)
                {
                    if(!collidePlayers.Contains(player)) removePlayers.Add(player);
                }
                foreach(BasePlayer player in removePlayers)
                {
                    triggerPlayers.Remove(player);
                    OnLeaveCollision(player);
                }

                var removeAnimals = new HashSet<BaseAnimalNPC>();
                foreach(BaseAnimalNPC animal in triggerAnimals)
                {
                    if(!collideAnimals.Contains(animal)) removeAnimals.Add(animal);
                }
                foreach(BaseAnimalNPC animal in removeAnimals)
                {
                    triggerAnimals.Remove(animal);
                    OnLeaveCollision(animal);
                }
            }

            private void OnEnterCollision(BasePlayer player)
            {
                Interface.Oxide.CallHook("OnEnterNPC", npc.player, player);
            }

            private void OnLeaveCollision(BasePlayer player)
            {
                Interface.Oxide.CallHook("OnLeaveNPC", npc.player, player);
            }

            private void OnEnterCollision(BaseAnimalNPC animal)
            {
                Interface.Oxide.CallHook("OnEnterNPC", npc.player, animal);
            }

            private void OnLeaveCollision(BaseAnimalNPC animal)
            {
                Interface.Oxide.CallHook("OnLeaveNPC", npc.player, animal);
            }
        }

        //////////////////////////////////////////////////////
        ///  class HumanLocomotion
        /// MonoBehaviour: managed by UnityEngine
        ///  This takes care of all movements and attacks of HumanNPCs
        //////////////////////////////////////////////////////
        public class HumanLocomotion : MonoBehaviour
        {
            private HumanPlayer npc;
            public Vector3 StartPos = new Vector3(0f, 0f, 0f);
            public Vector3 EndPos = new Vector3(0f, 0f, 0f);
            public Vector3 LastPos = new Vector3(0f, 0f, 0f);
            private Vector3 nextPos = new Vector3(0f, 0f, 0f);
            private float waypointDone = 0f;
            public float secondsTaken = 0f;
            private float secondsToTake = 0f;

            public List<WaypointInfo> cachedWaypoints;
            private int currentWaypoint = -1;

            public float followDistance = 3.5f;
            private float lastHit = 0f;

            public int noPath = 0;
            public bool shouldMove = true;

            private float startedReload = 0f;
            private float startedFollow = 0f;
            private bool reloading = false;
            public bool returning = false;
            public bool sitting = false;
            public bool isRiding = false;

            public BaseCombatEntity attackEntity = null;
            public BaseEntity followEntity = null;
            public Vector3 targetPosition = Vector3.zero;

            public List<Vector3> pathFinding;

            private HeldEntity firstWeapon = null;

            public void Awake()
            {
                npc = GetComponent<HumanPlayer>();
                UpdateWaypoints();

                npc.player.modelState.onground = true;
            }

            public void UpdateWaypoints()
            {
                if(string.IsNullOrEmpty(npc.info.waypoint)) return;
                var cwaypoints = Interface.Oxide.CallHook("GetWaypointsList", npc.info.waypoint);
                if(cwaypoints == null) cachedWaypoints = null;
                else
                {
                    cachedWaypoints = new List<WaypointInfo>();
                    var lastPos = npc.info.spawnInfo.position;
                    var speed = GetSpeed();
                    foreach(var cwaypoint in (List<object>)cwaypoints)
                    {
                        foreach(var pair in (Dictionary<Vector3, float>)cwaypoint)
                        {
                            if(HumanNPC.PathFinding == null)
                            {
                                cachedWaypoints.Add(new WaypointInfo(pair.Key, pair.Value));
                                continue;
                            }
                            var temppathFinding = HumanNPC.PathFinding.Go(lastPos, pair.Key);
                            speed = pair.Value;
                            if(temppathFinding != null)
                            {
                                lastPos = pair.Key;
                                foreach(var vector3 in temppathFinding)
                                {
                                    cachedWaypoints.Add(new WaypointInfo(vector3, speed));
                                }
                            }
                            else
                            {
#if DEBUG
                                Interface.Oxide.LogInfo("Blocked waypoint? {0} for {1}, speed {2}", pair.Key, npc.player.displayName, pair.Value);
#endif
                                //cachedWaypoints.Add(new WaypointInfo(pair.Key, speed));
                            }
                        }
                    }
                    if(HumanNPC.PathFinding != null && lastPos != npc.info.spawnInfo.position)
                    {
                        var temppathFinding = HumanNPC.PathFinding.Go(lastPos, npc.info.spawnInfo.position);
                        if(temppathFinding != null)
                        {
                            foreach(var vector3 in temppathFinding)
                            {
                                cachedWaypoints.Add(new WaypointInfo(vector3, speed));
                            }
                        }
                        else
                        {
#if DEBUG
                            Interface.Oxide.LogInfo("Blocked waypoint to spawn? {0} for {1}", lastPos, npc.player.displayName);
#endif
                        }
                    }
                    if(cachedWaypoints.Count <= 0) cachedWaypoints = null;
#if DEBUG
                    Interface.Oxide.LogInfo("Waypoints: {0} for {1}", cachedWaypoints.Count, npc.player.displayName);
#endif
                }
            }
            private void FixedUpdate()
            {
                TryToMove();
            }
            public void TryToMove()
            {
                if(npc.player.IsDead() || npc.player.IsWounded()) return;

                if(attackEntity is BaseCombatEntity)
                {
#if DEBUG
                    Interface.Oxide.LogInfo("TryToMove: ProcessAttack(attackEntity)");
#endif
                    ProcessAttack(attackEntity);
                }
                else if(followEntity is BaseEntity)
                {
#if DEBUG
                    Interface.Oxide.LogInfo("TryToMove: ProcessFollow(followEntity)");
#endif
                    startedFollow = Time.realtimeSinceStartup;
                    ProcessFollow(followEntity.transform.position);
                }
                else if(secondsTaken == 0f)
                {
                    GetNextPath();
                }
                else if(targetPosition != Vector3.zero)// && !(followEntity as BaseCombatEntity).IsDead())
                {
#if DEBUG
                    Interface.Oxide.LogInfo("TryToMove: ProcessFollow(target)");
#endif
                    startedFollow = Time.realtimeSinceStartup;
                    ProcessFollow(targetPosition);
                }

                if(StartPos != EndPos) Execute_Move();
                if(waypointDone >= 1f) secondsTaken = 0f;
            }
            private void Execute_Move()
            {
                if(!shouldMove) return;
                secondsTaken += Time.deltaTime;
                waypointDone = Mathf.InverseLerp(0f, secondsToTake, secondsTaken);
                nextPos = Vector3.Lerp(StartPos, EndPos, waypointDone);
                nextPos.y = GetMoveY(nextPos);
                npc.player.MovePosition(nextPos);
                //npc.player.eyes.position = nextPos + new Vector3(0, 1.6f, 0);
                var newEyesPos = nextPos + new Vector3(0, 1.6f, 0);
                npc.player.eyes.position.Set(newEyesPos.x, newEyesPos.y, newEyesPos.z);
                npc.player.UpdatePlayerCollider(true);

                npc.player.modelState.onground = !IsSwimming();
            }

            public void Evade()
            {
                if(IsSwimming()) return;
                if(npc.info.evade == false) return;
#if DEBUG
                Interface.Oxide.LogInfo("Evading...");
#endif
                //float evd = UnityEngine.Random.Range(-npc.info.evdist/2, npc.info.evdist/2);
                float evd = UnityEngine.Random.Range(-npc.info.evdist, npc.info.evdist);
                //Vector3 ev = new Vector3(UnityEngine.Random.Range(-npc.info.evdist, npc.info.evdist), 0, UnityEngine.Random.Range(-npc.info.evdist, npc.info.evdist));
                Vector3 ev = new Vector3(evd, 0, evd);
                Vector3 newpos = npc.player.transform.position + ev;
#if DEBUG
                Interface.Oxide.LogInfo($"  first trying new position {newpos.ToString()}");
#endif
                RaycastHit hitinfo;
                int i = 0;
                while(Physics.OverlapSphere(newpos, npc.info.evdist, constructionMask) != null)
                {
                    newpos.x = newpos.x + UnityEngine.Random.Range(-0.2f, 0.2f);
                    newpos.y = newpos.y + UnityEngine.Random.Range(-0.1f, 0.1f);
                    newpos.z = newpos.z + UnityEngine.Random.Range(-0.2f, 0.2f);
#if DEBUG
                    Interface.Oxide.LogInfo($"  trying new position {newpos.ToString()}");
#endif
                    if(Physics.Raycast(newpos, Vector3Down, out hitinfo, 0.1f, groundLayer))
                    {
#if DEBUG
                        Interface.Oxide.LogInfo($"  found ground or construction at {newpos.ToString()}");
#endif
                        break;
                    }
                    else
                    {
                        newpos.y = npc.locomotion.GetGroundY(newpos);
#if DEBUG
                        Interface.Oxide.LogInfo($"  fell through floor, relocating to {newpos.ToString()}");
#endif
                    }

                    i++;
                    if(i > 100) break;
                }
                npc.player.MovePosition(newpos);
            }

            public bool IsSwimming()
            {
                return WaterLevel.Test(npc.player.transform.position + new Vector3(0, 0.65f, 0));
            }

            private bool CanSit()
            {
                if(isRiding)
                {
                    return false;
                }
                if(sitting)
                {
                    return false;
                }
                return npc.info.allowsit;
            }

            private bool CanRide()
            {
                if(isRiding)
                {
                    return false;
                }
                return npc.info.allowride;
            }

            public void Sit()
            {
                if(sitting) return;
                npc.Invoke("AllowMove",0);
                // Find a place to sit
                List<BaseChair> chairs = new List<BaseChair>();
                List<StaticInstrument> pidrxy = new List<StaticInstrument>();
                Vis.Entities<BaseChair>(npc.player.transform.position, 10f, chairs);
                Vis.Entities<StaticInstrument>(npc.player.transform.position, 1f, pidrxy);
                foreach(var mountable in chairs.Distinct().ToList())
                {
#if DEBUG
                    Interface.Oxide.LogInfo($"HumanNPC {npc.player.displayName} trying to sit...");
#endif
                    if(mountable.IsMounted())
                    {
#if DEBUG
                        Interface.Oxide.LogInfo($"Someone is sitting here.");
#endif
                        continue;
                    }
#if DEBUG
                    Interface.Oxide.LogInfo($"Found an empty chair.");
#endif
                    mountable.MountPlayer(npc.player);
                    npc.player.OverrideViewAngles(mountable.mountAnchor.transform.rotation.eulerAngles);
                    npc.player.eyes.NetworkUpdate(mountable.mountAnchor.transform.rotation);
                    npc.player.ClientRPCPlayer<Vector3>(null, npc.player, "ForcePositionTo", npc.player.transform.position);
                    mountable.SetFlag(BaseEntity.Flags.Busy, true, false);
                    sitting = true;
                    break;
                }
                foreach(var mountable in pidrxy.Distinct().ToList())
                {
#if DEBUG
                    Interface.Oxide.LogInfo($"HumanNPC {npc.player.displayName} trying to sit at instrument");
#endif
                    if(mountable.IsMounted())
                    {
#if DEBUG
                        Interface.Oxide.LogInfo($"Someone is sitting here.");
#endif
                        continue;
                    }
#if DEBUG
                    Interface.Oxide.LogInfo($"Found an empty instrument.");
#endif
                    mountable.MountPlayer(npc.player);
                    npc.player.OverrideViewAngles(mountable.mountAnchor.transform.rotation.eulerAngles);
                    npc.player.eyes.NetworkUpdate(mountable.mountAnchor.transform.rotation);
                    npc.player.ClientRPCPlayer<Vector3>(null, npc.player, "ForcePositionTo", npc.player.transform.position);
                    mountable.SetFlag(BaseEntity.Flags.Busy, true, false);
                    sitting = true;
                    Interface.Oxide.LogInfo($"Setting instrument for {npc.player.displayName} to {mountable.ShortPrefabName}");
                    npc.info.instrument = mountable.ShortPrefabName;
                    npc.ktool = mountable;//.GetParentEntity() as StaticInstrument;
                    break;
                }
            }

            public void Stand()
            {
                //if(CanSit() && sitting)
                if(sitting)
                {
#if DEBUG
                    Interface.Oxide.LogInfo($"HumanNPC {npc.player.displayName} trying to stand...");
#endif
//                    npc.Invoke("AllowMove",0);
                    var mounted = npc.player.GetMounted();
                    mounted.DismountPlayer(npc.player);
                    mounted.SetFlag(BaseEntity.Flags.Busy, false, false);
                    sitting = false;
                }
            }

            public void Ride()
            {
//#if DEBUG
//                Interface.Oxide.LogInfo($"Ride() invoked for {npc.player.displayName}.");
//#endif
                if(npc.info.allowride == false) return;
                var horse = npc.player.GetMountedVehicle() as RidableHorse;
                if(horse == null)
                {
                    // Find a place to sit
                    List<RidableHorse> horses = new List<RidableHorse>();
                    Vis.Entities<RidableHorse>(npc.player.transform.position, 15f, horses);
                    foreach(var mountable in horses.Distinct().ToList())
                    {
#if DEBUG
                        Interface.Oxide.LogInfo($"HumanNPC {npc.player.displayName} trying to ride...");
#endif
                        if(mountable.GetMounted() != null)
                        {
#if DEBUG
                            Interface.Oxide.LogInfo($"Someone is riding it.");
#endif
                            continue;
                        }
#if DEBUG
                        Interface.Oxide.LogInfo($"Found an available horse.");
#endif
                        mountable.AttemptMount(npc.player);
                        npc.player.SetParent(mountable, true, true);
                        isRiding = true;
                        break;
                    }
                }

                if(horse == null)
                {
                    isRiding = false;
                    npc.player.SetParent(null, true, true);
                    npc.locomotion.PathFinding();
                    return;
                }
                Vector3 targetDir = new Vector3();
                Vector3 targetLoc = new Vector3();
                Vector3 targetHorsePos = new Vector3();
                float distance = 0f;
                bool rand = true;

                if(attackEntity != null)
                {
                    distance = Vector3.Distance(npc.player.transform.position, attackEntity.transform.position);
                    targetDir = attackEntity.transform.position;
#if DEBUG
                    Interface.Oxide.LogInfo($"Riding towards attackEntity at {attackEntity.transform.position.ToString()}");
#endif
                }
                else
                {
                    distance = Vector3.Distance(npc.player.transform.position, StartPos);
                    targetDir = StartPos - horse.transform.position;
//                    rand = true;
#if DEBUG
                    Interface.Oxide.LogInfo($"Riding towards nowhere in particular...");
#endif
                }

                bool hasMoved = targetDir != Vector3.zero && Vector3.Distance(horse.transform.position, npc.player.transform.position) > 0.5f;
                bool isVisible = attackEntity != null && attackEntity.IsVisible(npc.player.eyes.position, (attackEntity as BasePlayer).eyes.position, 200);
                var randompos = UnityEngine.Random.insideUnitCircle * npc.info.damageDistance;
                if(attackEntity != null)
                {
                    if(isVisible)
                    {
                        targetLoc = attackEntity.transform.position;
                        rand = false;
                    }
                    else
                    {
                        if(Vector3.Distance(npc.player.transform.position, targetHorsePos) > 10 && !hasMoved)
                        {
                            attackEntity = null;
                            targetLoc = new Vector3(randompos.x, 0, randompos.y);
                            targetLoc += npc.info.spawnInfo.position;
                            targetHorsePos = targetLoc;
                        }
                        else
                        {
                            targetLoc = attackEntity.transform.position;
                        }
                    }
                }
                else
                {
                    if(Vector3.Distance(npc.player.transform.position, targetHorsePos) > 10 && hasMoved)
                    {
                        targetLoc = targetHorsePos;
                    }
                    else
                    {
                        targetLoc = new Vector3(randompos.x, 0, randompos.y);
                        targetLoc += npc.player.transform.position;
                        targetHorsePos = targetLoc;
                    }
                }

                float angle = Vector3.SignedAngle(targetDir, horse.transform.forward, Vector3.up);
                //float angle = Vector3.SignedAngle(npc.player.transform.forward, targetDir, Vector3.forward);
                //float angle = Vector3.SignedAngle(targetDir, horse.transform.forward, Vector3.forward);

                InputMessage message = new InputMessage() { buttons = 0 };
                if(distance > npc.info.damageDistance)
                {
                    message.buttons = 2; // FORWARD
                }
                if(distance > 40 && !rand)
                {
                    message.buttons = 130; // SPRINT FORWARD
                }
                if(horse.currentRunState == BaseRidableAnimal.RunState.sprint && distance < npc.info.maxDistance)
                {
                    message.buttons = 0; // STOP ?
                }
                if(angle > 30 && angle < 180)
                {
                    message.buttons += 8; // LEFT
                }
                if(angle < -30 && angle > -180)
                {
                    message.buttons += 16; // RIGHT
                }
#if DEBUG
                Interface.Oxide.LogInfo($"Sending input to horse: {message.buttons.ToString()}");
#endif
                horse.RiderInput(new InputState() { current = message }, npc.player);
            }

            private float GetSpeed(float speed = -1)
            {
                if(sitting)
                    speed = 0;
                if(returning)
                    speed = 7;
                else if(speed == -1)
                    speed = npc.info.speed;

                if(IsSwimming())
                    speed = speed / 2f;

                return speed;
            }

            private void GetNextPath()
            {
                if(npc == null) npc = GetComponent<HumanPlayer>();

                if(CanSit() && sitting == false)
                {
                    Sit();
                }
                else if(CanRide())// && isRiding == false)
                {
                    InvokeRepeating("Ride", 0, 0.5f);
                }

                LastPos = Vector3.zero;
                if(cachedWaypoints == null)
                {
                    shouldMove = false;
                    return;
                }
                shouldMove = true;
                Interface.Oxide.CallHook("OnNPCPosition", npc.player, npc.player.transform.position);
                if(currentWaypoint + 1 >= cachedWaypoints.Count)
                {
                    UpdateWaypoints();
                    currentWaypoint = -1;
                }
                if(cachedWaypoints == null)
                {
                    shouldMove = false;
                    return;
                }
                currentWaypoint++;

                var wp = cachedWaypoints[currentWaypoint];
                SetMovementPoint(npc.player.transform.position, wp.Position, GetSpeed(wp.Speed));
                if(npc.player.transform.position == wp.Position)
                {
                    npc.DisableMove();
                    npc.Invoke("AllowMove", GetSpeed(wp.Speed));
                    return;
                }
            }

            public void SetMovementPoint(Vector3 startpos, Vector3 endpos, float s)
            {
                StartPos = startpos;

                if(endpos != Vector3.zero)
                {
                    EndPos = endpos;
                    EndPos.y = Math.Max(EndPos.y, TerrainMeta.HeightMap.GetHeight(EndPos));
                    if(StartPos != EndPos)
                    {
                        secondsToTake = Vector3.Distance(EndPos, StartPos) / s;
                    }
                    npc.LookTowards(EndPos);
                }
                else
                {
                    if(IsInvoking("PathFinding")) { CancelInvoke("PathFinding"); }
                }

                secondsTaken = 0f;
                waypointDone = 0f;
            }

            private bool HitChance(float chance = -1f)
            {
                if(chance < 0)
                {
                    chance = npc.info.hitchance;
                }
                if(chance > 1)
                {
                    chance = 0.75f;
                }
                return UnityEngine.Random.Range(1, 100) < (int)(chance * 100);
            }

            private void Move(Vector3 position, float speed = -1)
            {
                if(speed == -1)
                {
                    speed = npc.info.speed;
                }

                if(waypointDone >= 1f)
                {
                    if(pathFinding != null && pathFinding.Count > 0) pathFinding.RemoveAt(pathFinding.Count - 1);
                    waypointDone = 0f;
                }
                if(pathFinding == null || pathFinding.Count < 1) return;
                shouldMove = true;

                if(waypointDone == 0f) SetMovementPoint(position, pathFinding[pathFinding.Count - 1], GetSpeed(speed));
            }

            private void ProcessAttack(BaseCombatEntity entity)
            {
#if DEBUG
                Interface.Oxide.LogInfo("ProcessAttack: {0} -> {1}", npc.player.displayName, entity.name);
#endif
                float c_attackDistance = 0f;
                if(entity != null && entity.IsAlive())
                {
                    if(entity is BaseAnimalNPC)
                    {
                        c_attackDistance = Vector3.Distance(entity.transform.position + new Vector3(0, 1.6f, 0), npc.player.transform.position + new Vector3(0, 0.3f, 0));
                    }
                    else
                    {
                        c_attackDistance = Vector3.Distance(entity.transform.position + new Vector3(0, 1.6f, 0), npc.player.transform.position + new Vector3(0, 1.6f, 0));
                    }
                    shouldMove = false;

                    bool validAttack = Vector3.Distance(LastPos, npc.player.transform.position) < npc.info.maxDistance && noPath < 5;

#if DEBUG
                    Interface.Oxide.LogInfo("  Entity: Type {0}, alive {1}, valid {2}, distance {3}, noPath {4}", entity.GetType().FullName, entity.IsAlive(), validAttack, c_attackDistance.ToString(), noPath.ToString());
#endif
                    if(validAttack)
                    {
                        bool range = false;
                        if(npc.info.follow)
                        {
                            range = c_attackDistance < npc.info.damageDistance;
                        }
                        else
                        {
                            range = c_attackDistance < npc.info.maxDistance;
                        }
                        var see = CanSee(npc, entity);
#if DEBUG
                        Interface.Oxide.LogInfo("  validAttack Entity: Type {0}, ranged {1}, cansee {2}", entity.GetType().FullName, range, see);
#endif
                        if(range && see)
                        {
                            AttemptAttack(entity);
                            return;
                        }
                        if(GetSpeed() <= 0)
                        {
                            npc.EndAttackingEntity();
                            npc.EndFollowingEntity();
                        }
                        else if(!npc.info.follow)
                        {
                        }
                        else
                        {
                            Move(npc.player.transform.position);
                        }
                    }
                    else
                    {
                        npc.EndFollowingEntity();
                        npc.EndAttackingEntity();
                    }
                }
                else
                {
                    npc.EndAttackingEntity();
                }
            }

            public void ProcessFollow(Vector3 target)
            {
                //startedFollow += Time.deltaTime;
                if(Time.realtimeSinceStartup - startedFollow > npc.info.followtime)
                {
#if DEBUG
                    Interface.Oxide.LogInfo($"ProcessFollow() Took too long...");
#endif
                    npc.EndFollowingEntity(noPath < 5);
                    return;
                }
#if DEBUG
                Interface.Oxide.LogInfo($"ProcessFollow() called for {target.ToString()}");
#endif
                var c_followDistance = Vector3.Distance(target, npc.player.transform.position);
                shouldMove = false;
#if DEBUG
                Interface.Oxide.LogInfo($"ProcessFollow() distance {c_followDistance.ToString()}");
#endif
                if(followEntity == null)
                {
                    npc.EndFollowingEntity(false);
                    return;
                }
                if((followEntity as BaseCombatEntity).IsDead())
                {
                    npc.EndFollowingEntity(false);
                    return;
                }
                //if(c_followDistance > 0)// && Vector3.Distance(LastPos, npc.player.transform.position) < followDistance)// && noPath < 5)
                if(c_followDistance > followDistance && Vector3.Distance(LastPos, npc.player.transform.position) < npc.info.maxDistance && noPath < 5)
                {
                    Move(npc.player.transform.position, npc.info.speed);
                }
                else
                {
                    if(followEntity is BaseEntity)
                    {
#if DEBUG
                        Interface.Oxide.LogInfo($"ProcessFollow() bailing out - is BaseEntity");
#endif
                        npc.EndFollowingEntity(noPath < 5);
                    }
                    else if(targetPosition != Vector3.zero)
                    {
#if DEBUG
                        Interface.Oxide.LogInfo($"ProcessFollow() bailing out");
#endif
                        npc.EndGo(noPath < 5);
                        npc.locomotion.GetBackToLastPos();
                    }
                }
            }

            public void PathFinding()
            {
                Vector3 target = Vector3.zero;

                if(attackEntity != null)
                {
                    //Vector3 diff = new Vector3(Core.Random.Range(-npc.info.attackDistance, npc.info.attackDistance), 0, Core.Random.Range(-npc.info.attackDistance, npc.info.attackDistance));
                    target = attackEntity.transform.position;// + diff;
                }
                else if(followEntity != null)
                {
                    target = followEntity.transform.position;
                }
                else if(targetPosition != Vector3.zero)
                {
                    target = targetPosition;
                }

                if(target != Vector3.zero)
                {
                    PathFinding(new Vector3(target.x, GetMoveY(target), target.z));
                }
            }

            public void PathFinding(Vector3 targetPos)
            {
                if(gameObject == null) return;
                if(IsInvoking("PathFinding")) { CancelInvoke("PathFinding"); }
                if(GetSpeed() <= 0) return;

                var temppathFinding = HumanNPC.PathFinding?.Go(npc.player.transform.position, targetPos);

                if(temppathFinding == null)
                {
                    if(pathFinding == null || pathFinding.Count == 0)
                    {
                        noPath++;
                    }
                    else
                    {
                        noPath = 0;
                    }
                    if(noPath < 5)
                    {
                        Invoke("PathFinding", 2);
                    }
                    else if(returning)
                    {
                        returning = false;
                        SetMovementPoint(npc.player.transform.position, LastPos, 7f);
                        secondsTaken = 0.01f;
                    }
                }
                else
                {
                    noPath = 0;

                    pathFinding = temppathFinding;
                    pathFinding.Reverse();
                    waypointDone = 0f;
                    Invoke("PathFinding", pathFinding.Count / GetSpeed(npc.info.speed));
                }
            }

            public void GetBackToLastPos()
            {
                if(npc.player.transform.position == LastPos) return;
                if(LastPos == Vector3.zero) LastPos = npc.info.spawnInfo.position;
                if(Vector3.Distance(npc.player.transform.position, LastPos) < 5)
                {
                    SetMovementPoint(npc.player.transform.position, LastPos, 7f);
                    secondsTaken = 0.01f;
                    return;
                }
                returning = true;
                npc.StartGo(LastPos);
            }

            public void Enable()
            {
                //if(GetSpeed() <= 0) return;
                enabled = true;
            }
            public void Disable()
            {
                enabled = false;
            }

            public float GetMoveY(Vector3 position)
            {
                if(IsSwimming())
                {
                    float point = TerrainMeta.WaterMap.GetHeight(position) - 0.65f;
                    float groundY = GetGroundY(position);
                    if(groundY > point)
                    {
                        return groundY;
                    }

                    return point - 0.65f;
                }

                return GetGroundY(position);
            }

            public float GetGroundY(Vector3 position)
            {
                position = position + Vector3.up;
                RaycastHit hitinfo;
                if(Physics.Raycast(position, Vector3Down, out hitinfo, 100f, groundLayer))
                {
                    return hitinfo.point.y;
                }
                return position.y - .5f;
            }

            public void CreateProjectileEffect(BaseCombatEntity target, BaseProjectile baseProjectile, float dmg, bool miss = false)
            {
                if(baseProjectile.primaryMagazine.contents <= 0)
                {
#if DEBUG
                    Interface.Oxide.LogInfo("Attack failed(empty): {0} - {1}", npc.player.displayName, attackEntity.name);
#endif
                    return;
                }
                var component = baseProjectile.primaryMagazine.ammoType.GetComponent<ItemModProjectile>();
                if(component == null)
                {
#if DEBUG
                    Interface.Oxide.LogInfo("Attack failed(Component): {0} - {1}", npc.player.displayName, attackEntity.name);
#endif
                    return;
                }
                npc.LookTowards(target.transform.position);

                var source = npc.player.transform.position + npc.player.GetOffset();
                if(baseProjectile.MuzzlePoint != null)
                {
                    source += Quaternion.LookRotation(target.transform.position - npc.player.transform.position) * baseProjectile.MuzzlePoint.position;
                }
                var dir = (target.transform.position + npc.player.GetOffset() - source).normalized;
                var vector32 = dir * (component.projectileVelocity * baseProjectile.projectileVelocityScale);

                Vector3 hit;
                RaycastHit raycastHit;
                if(Vector3.Distance(npc.player.transform.position, target.transform.position) < 0.5)
                {
                    hit = target.transform.position + npc.player.GetOffset(true);
                }
                else if(!Physics.SphereCast(source, .01f, vector32, out raycastHit, float.MaxValue, targetLayer))
                {
#if DEBUG
                    Interface.Oxide.LogInfo("Attack failed: {0} - {1}", npc.player.displayName, attackEntity.name);
#endif
                    return;
                }
                else
                {
                    hit = raycastHit.point;
                    target = raycastHit.GetCollider().GetComponent<BaseCombatEntity>();
#if DEBUG
                    Interface.Oxide.LogInfo("Attack failed: {0} - {1}", raycastHit.GetCollider().name, (Rust.Layer)raycastHit.GetCollider().gameObject.layer);
#endif
                    miss = miss || target == null;
                }
                baseProjectile.primaryMagazine.contents--;
                npc.ForceSignalAttack();

                if(miss)
                {
                    var aimCone = baseProjectile.GetAimCone();
                    vector32 += Quaternion.Euler(UnityEngine.Random.Range((float)(-aimCone * 0.5), aimCone * 0.5f), UnityEngine.Random.Range((float)(-aimCone * 0.5), aimCone * 0.5f), UnityEngine.Random.Range((float)(-aimCone * 0.5), aimCone * 0.5f)) * npc.player.eyes.HeadForward();
                }

                Effect.server.Run(baseProjectile.attackFX.resourcePath, baseProjectile, StringPool.Get(baseProjectile.handBone), Vector3.zero, Vector3.forward);
                var effect = new Effect();
                effect.Init(Effect.Type.Projectile, source, vector32.normalized);
                effect.scale = vector32.magnitude;
                effect.pooledString = component.projectileObject.resourcePath;
                effect.number = UnityEngine.Random.Range(0, 2147483647);
                EffectNetwork.Send(effect);

                Vector3 dest;
                if(miss)
                {
                    dmg = 0;
                    dest = hit;
                }
                else
                {
                    dest = target.transform.position;
                }
                var hitInfo = new HitInfo(npc.player, target, DamageType.Bullet, dmg, dest)
                {
                    DidHit = !miss,
                    HitEntity = target,
                    PointStart = source,
                    PointEnd = hit,
                    HitPositionWorld = dest,
                    HitNormalWorld = -dir,
                    WeaponPrefab = GameManager.server.FindPrefab(StringPool.Get(baseProjectile.prefabID)).GetComponent<AttackEntity>(),
                    Weapon = (AttackEntity)firstWeapon,
                    HitMaterial = StringPool.Get("Flesh")
                };
                target?.OnAttacked(hitInfo);
                Effect.server.ImpactEffect(hitInfo);
            }

            public void AttemptAttack(BaseCombatEntity entity)
            {
                var weapon = firstWeapon as BaseProjectile;
                if(weapon != null)
                {
                    if(!reloading && weapon.primaryMagazine.contents <= 0)
                    {
                        reloading = true;
                        npc.player.SignalBroadcast(BaseEntity.Signal.Reload, string.Empty);
                        startedReload = Time.realtimeSinceStartup;
                        return;
                    }
                    if(reloading && Time.realtimeSinceStartup > startedReload + (npc.info.reloadDuration > 0 ? npc.info.reloadDuration : weapon.reloadTime))
                    {
                        reloading = false;
                        if(npc.info.needsAmmo)
                        {
                            weapon.primaryMagazine.Reload(npc.player);
                            npc.player.inventory.ServerUpdate(0f);
                        }
                        else
                        {
                            weapon.primaryMagazine.contents = weapon.primaryMagazine.capacity;
                        }
                    }
                    if(reloading) return;
                }
                if(!(Time.realtimeSinceStartup > lastHit + npc.info.damageInterval)) return;
                lastHit = Time.realtimeSinceStartup;
                DoAttack(entity, !HitChance());
            }

            public void DoAttack(BaseCombatEntity target, bool miss = false)
            {
                if(npc == null) return;
                var weapon = firstWeapon as BaseProjectile;
                if(firstWeapon == null || (firstWeapon != null && (firstWeapon.IsDestroyed || weapon != null && weapon.primaryMagazine.contents == 0)))
                {
                    firstWeapon = npc.EquipFirstWeapon();
                    weapon = firstWeapon as BaseProjectile;
                    npc.SetActive(0);
                }

                var attackitem = firstWeapon?.GetItem();
                if(attackitem == null)
                {
                    npc.EndAttackingEntity();
                    return;
                }
                if(attackitem.uid != npc.player.svActiveItemID)
                {
                    npc.SetActive(attackitem.uid);
                }

                float dmg = npc.info.damageAmount * UnityEngine.Random.Range(0.8f, 1.2f);
                if(target is BaseNpc || target is BaseAnimalNPC)
                {
                    dmg *= 1.5f;
                }
                else if(target is AutoTurret)
                {
                    dmg *= 3f;
                }

                if(weapon != null)
                {
                    //npc.ForceSignalGesture();
                    CreateProjectileEffect(target, weapon, dmg, miss);
                }
                else
                {
                    var hitInfo = new HitInfo(npc.player, target, DamageType.Stab, dmg, target.transform.position)
                    {
                        PointStart = npc.player.transform.position,
                        PointEnd = target.transform.position
                    };
                    target.SendMessage("OnAttacked", hitInfo, SendMessageOptions.DontRequireReceiver);
                    npc.ForceSignalAttack();
                }
            }
        }

        //////////////////////////////////////////////////////
        ///  class HumanPlayer : MonoBehaviour
        ///  MonoBehaviour: managed by UnityEngine
        /// Takes care of all the sub categories of the HumanNPCs
        //////////////////////////////////////////////////////
        public class HumanPlayer : MonoBehaviour
        {
            public HumanNPCInfo info;
            public HumanLocomotion locomotion;
            public HumanTrigger trigger;
            public ProtectionProperties protection;
            public InstrumentTool itool;
            public StaticInstrument ktool;

            public BasePlayer player;

            public float lastMessage;

            private void Awake()
            {
                player = GetComponent<BasePlayer>();
                protection = ScriptableObject.CreateInstance<ProtectionProperties>();
            }

            public void SetInfo(HumanNPCInfo info, bool update = false)
            {
                this.info = info;
                if(info == null) return;
                player.displayName = info.displayName;
                SetViewAngle(info.spawnInfo.rotation);
                player.syncPosition = true;
                if(!update)
                {
                    //player.xp = ServerMgr.Xp.GetAgent(info.userid);
                    player.stats = new PlayerStatistics(player);
                    player.userID = info.userid;
                    player.UserIDString = player.userID.ToString();
                    player.MovePosition(info.spawnInfo.position);
                    player.eyes = player.eyes ?? player.GetComponent<PlayerEyes>();
                    //player.eyes.position = info.spawnInfo.position + new Vector3(0, 1.6f, 0);
                    var newEyes = info.spawnInfo.position + new Vector3(0, 1.6f, 0);
                    player.eyes.position.Set(newEyes.x, newEyes.y, newEyes.z);
                    player.EndSleeping();
                    protection.Clear();
                    foreach(var pro in info.protections)
                    {
                        protection.Add(pro.Key, pro.Value);
                    }
                }
                if(locomotion != null) Destroy(locomotion);
                locomotion = player.gameObject.AddComponent<HumanLocomotion>();
                if(trigger != null) Destroy(trigger);
                trigger = player.gameObject.AddComponent<HumanTrigger>();
                lastMessage = Time.realtimeSinceStartup;
                DisableMove();
                AllowMove();
            }

            public void UpdateHealth(HumanNPCInfo info)
            {
                player.InitializeHealth(info.health, info.health);
                player.health = info.health;
            }

            public void Evade()
            {
                this.locomotion.Evade();
            }

            public void AllowMove()
            {
                locomotion?.Enable();
            }
            public void DisableMove()
            {
                locomotion?.Disable();
            }
            public void TemporaryDisableMove(float thetime = -1f)
            {
                if(thetime == -1f) thetime = info.stopandtalkSeconds;
                DisableMove();
                if(gameObject == null) return;
                if(IsInvoking("AllowMove")) CancelInvoke("AllowMove");
                Invoke("AllowMove", thetime);
            }
            public void EndAttackingEntity(bool trigger = true)
            {
                if(locomotion.gameObject != null && locomotion.IsInvoking("PathFinding")) locomotion.CancelInvoke("PathFinding");
                locomotion.noPath = 0;
                locomotion.shouldMove = true;
                if(trigger)
                {
                    Interface.Oxide.CallHook("OnNPCStopTarget", player, locomotion.attackEntity);
                }
                locomotion.attackEntity = null;
                player.health = info.health;
                locomotion.GetBackToLastPos();
                SetActive(0);
            }
            public void EndFollowingEntity(bool trigger = true)
            {
                if(locomotion.IsInvoking("PathFinding")) locomotion.CancelInvoke("PathFinding");

                locomotion.noPath = 0;
                locomotion.shouldMove = true;
                if(trigger)
                {
                    Interface.Oxide.CallHook("OnNPCStopFollow", player, locomotion.followEntity);
                }
                locomotion.followEntity = null;
                //locomotion.returning = true;
                locomotion.GetBackToLastPos();
                SetActive(0);
            }

            public void EndGo(bool trigger = true)
            {
                if(locomotion.IsInvoking("PathFinding")) locomotion.CancelInvoke("PathFinding");

                locomotion.noPath = 0;
                locomotion.shouldMove = true;

                if(trigger)
                {
                    Interface.Oxide.CallHook("OnNPCStopGo", player, locomotion.targetPosition);
                }
                if(locomotion.returning)
                {
                    locomotion.returning = false;
                    locomotion.SetMovementPoint(player.transform.position, locomotion.LastPos, 7f);
                    locomotion.secondsTaken = 0.01f;
                }
                locomotion.targetPosition = Vector3.zero;
            }

            public void StartAttackingEntity(BaseCombatEntity entity)
            {
                if(locomotion.attackEntity != null && UnityEngine.Random.Range(0f, 1f) < 0.75f) return;
                if(Interface.Oxide.CallHook("OnNPCStartTarget", player, entity) == null)
                {
                    var item = GetFirstWeaponItem();
                    if(item != null) SetActive(item.uid);

                    locomotion.attackEntity = entity;
                    locomotion.pathFinding = null;

                    if(locomotion.LastPos == Vector3.zero) locomotion.LastPos = player.transform.position;
                    if(gameObject != null && IsInvoking("AllowMove"))
                    {
                        CancelInvoke("AllowMove");
                        AllowMove();
                    }
                    locomotion.Invoke("PathFinding", 0);
                }
            }

            public void StartFollowingEntity(BaseEntity entity, string pname = "player")
            {
#if DEBUG
                Interface.Oxide.LogInfo($"StartFollowingEntity() called for {pname}");
#endif
                if(locomotion.targetPosition != Vector3.zero)
                {
                    EndGo(false);
                }
                player.SendNetworkUpdate();
                locomotion.followEntity = entity;
                locomotion.pathFinding = null;

                if(locomotion.LastPos == Vector3.zero) locomotion.LastPos = player.transform.position;
//                if(IsInvoking("AllowMove")) { CancelInvoke("AllowMove"); AllowMove(); }
                locomotion.Invoke("PathFinding", 0);
            }

            public void StartGo(Vector3 position)
            {
                if(locomotion.followEntity != null)
                {
                    EndFollowingEntity(false);
                }
                player.SendNetworkUpdate();
                locomotion.targetPosition = position;
                locomotion.pathFinding = null;

                if(locomotion.LastPos == Vector3.zero) locomotion.LastPos = player.transform.position;
                if(IsInvoking("AllowMove")) { CancelInvoke("AllowMove"); AllowMove(); }
                locomotion.Invoke("PathFinding", 0);
            }

            public HeldEntity GetCurrentWeapon()
            {
                foreach(Item item in player.inventory.containerBelt.itemList)
                {
                    BaseEntity heldEntity = item.GetHeldEntity();
                    if(heldEntity is HeldEntity && !heldEntity.HasFlag(BaseEntity.Flags.Disabled))
                    {
                        return(HeldEntity)heldEntity;
                    }
                }
                return null;
            }

            public Item GetFirstWeaponItem()
            {
                return GetFirstWeapon()?.GetItem();
            }

            public Item GetFirstInstrumentItem()
            {
                return GetFirstInstrument()?.GetItem();
            }

            public HeldEntity GetFirstWeapon()
            {
                foreach(Item item in player.inventory.containerBelt.itemList)
                {
                    if(item.CanBeHeld() && HasAmmo(item) && (item.info.category == ItemCategory.Weapon))
                    {
                        return item.GetHeldEntity() as HeldEntity;
                    }
                }
                return null;
            }

            public HeldEntity GetFirstTool()
            {
                foreach(Item item in player.inventory.containerBelt.itemList)
                {
                    if(item.CanBeHeld() && item.info.category == ItemCategory.Tool)
                    {
                        return item.GetHeldEntity() as HeldEntity;
                    }
                }
                return null;
            }

            public HeldEntity GetFirstMisc()
            {
                foreach(Item item in player.inventory.containerBelt.itemList)
                {
                    if(item.CanBeHeld() && item.info.category != ItemCategory.Tool && item.info.category != ItemCategory.Weapon)
                    {
                        return item.GetHeldEntity() as HeldEntity;
                    }
                }
                return null;
            }

            public HeldEntity GetFirstInstrument()
            {
#if DEBUG
                Interface.Oxide.LogInfo("GetFirstInstrument() called.");
#endif
                foreach(Item item in player.inventory.containerBelt.itemList)
                {
#if DEBUG
                    Interface.Oxide.LogInfo("Checking for instrument...");
#endif
                    if(item.CanBeHeld() && item.info.category == ItemCategory.Fun)
                    {
#if DEBUG
                        Interface.Oxide.LogInfo("Found one!");
#endif
                        return item.GetHeldEntity() as HeldEntity;
                    }
                }
                return null;
            }

            public List<Item> GetAmmo(Item item)
            {
                var ammos = new List<Item>();
                AmmoTypes ammoType;
                if(!ammoTypes.TryGetValue(item.info.shortname, out ammoType)) return ammos;
                player.inventory.FindAmmo(ammos, ammoType);
                return ammos;
            }

            public bool HasAmmo(Item item)
            {
                if(!info.needsAmmo) return true;
                var weapon = item.GetHeldEntity() as BaseProjectile;
                if(weapon == null) return true;
                return weapon.primaryMagazine.contents > 0 || weapon.primaryMagazine.CanReload(player);
            }

            public void UnequipAll()
            {
                if(player.inventory?.containerBelt == null) return;
                foreach(Item item in player.inventory.containerBelt.itemList)
                {
                    if(item.CanBeHeld())
                    {
                        (item.GetHeldEntity() as HeldEntity)?.SetHeld(false);
                    }
                }
            }

            public HeldEntity EquipFirstWeapon()
            {
                HeldEntity weapon = GetFirstWeapon();
                if(weapon != null)
                {
                    UnequipAll();
                    weapon.SetHeld(true);
                }
                return weapon;
            }

            public HeldEntity EquipFirstTool()
            {
                HeldEntity tool = GetFirstTool();
                if(tool != null)
                {
                    UnequipAll();
                    tool.SetHeld(true);
                }
                return tool;
            }

            public HeldEntity EquipFirstMisc()
            {
                HeldEntity misc = GetFirstMisc();
                if(misc != null)
                {
                    UnequipAll();
                    misc.SetHeld(true);
                }
                return misc;
            }

            public HeldEntity EquipFirstInstrument()
            {
                HeldEntity instr = GetFirstInstrument();
                if(instr != null)
                {
                    UnequipAll();
                    instr.SetOwnerPlayer(this.player);
                    instr.SetVisibleWhileHolstered(true);
                    instr.SetHeld(true);
                    instr.UpdateHeldItemVisibility();
                    var item = instr.GetItem();
                    SetActive(item.uid);
                    this.itool = instr as InstrumentTool;
                    this.info.instrument = instr.ShortPrefabName;
                }
                return instr;
            }

            public void SetActive(uint id)
            {
                player.svActiveItemID = id;
                player.SendNetworkUpdate();
                player.SignalBroadcast(BaseEntity.Signal.Reload, string.Empty);
            }

            private void OnDestroy()
            {
                Destroy(locomotion);
                Destroy(trigger);
                Destroy(protection);
            }

            public void LookTowards(Vector3 pos)
            {
                if(pos != player.transform.position)
                {
                    SetViewAngle(Quaternion.LookRotation(pos - player.transform.position));
                }
            }

            public void ForceSignalGesture()
            {
                player.SignalBroadcast(BaseEntity.Signal.Gesture, "pickup_item");
            }

            public void ForceSignalAttack()
            {
                player.SignalBroadcast(BaseEntity.Signal.Attack, string.Empty);
            }

            public void SetViewAngle(Quaternion viewAngles)
            {
                if(viewAngles.eulerAngles == default(Vector3)) return;
                player.viewAngles = viewAngles.eulerAngles;
                player.SendNetworkUpdate();
            }
        }

        // Nikedemos
        public class HumanNPCTeamMember
        {
            public ulong userid;
            public HumanNPCTeamMember(ulong userid)
            {
                this.userid = userid;
            }

            public bool IsNPC()
            {
                return !(this.userid >= 76560000000000000L || this.userid <= 0L);
            }

            public string TryGetMemberName()
            {
                string foundName = null;

                if (IsNPC())
                {
                    //check if we can grab a HumanPlayer
                    HumanPlayer maybeHumanPlayer = TryGetHumanPlayer();

                    if (maybeHumanPlayer != null)
                    {
                        foundName = maybeHumanPlayer.info.displayName;
                    }
                }
                else
                {
                    //check if we can grab a BasePlayer
                    BasePlayer maybeBasePlayer = TryGetBasePlayer();

                    if (maybeBasePlayer != null)
                    {
                        foundName = maybeBasePlayer.displayName;
                    }
                }

                return foundName;
            }
            public HumanPlayer TryGetHumanPlayer() //because team members can be either real humans or npcs (counter-intuitively named HumanPlayers)
            {
                HumanPlayer foundHumanPlayer = null;

                if (IsNPC())
                {
                    //okay, it is an NPC based on the userid, let's see if it exists/is accessible

                    foreach (HumanPlayer maybeHumanPlayer in UnityEngine.Object.FindObjectsOfType<HumanPlayer>())
                    {
                        if (maybeHumanPlayer.info.userid == this.userid)
                        {
                            foundHumanPlayer = maybeHumanPlayer;
                            break;
                        }
                    }
                } //if it's not an NPC, you get a null.
                return foundHumanPlayer;
            }

            public BasePlayer TryGetBasePlayer()
            {
                BasePlayer foundBasePlayer = null;

                if (IsNPC())
                {
                    HumanPlayer maybeHumanPlayer = TryGetHumanPlayer();

                    if (maybeHumanPlayer != null)
                    {
                        foundBasePlayer = maybeHumanPlayer.GetComponent<BasePlayer>();
                    }
                }
                else
                {
                    foreach (BasePlayer maybeBasePlayer in UnityEngine.Object.FindObjectsOfType<BasePlayer>())
                    {
                        if (maybeBasePlayer.userID == this.userid)
                        {
                            foundBasePlayer = maybeBasePlayer;
                            break;
                        }
                    }
                }

                return foundBasePlayer;
            }
        }

        public Hash<ulong, HumanNPCTeamInfo> GetAllTeamsContainingUserID(ulong userid)
        {
            Hash<ulong, HumanNPCTeamInfo> foundTeams = new Hash<ulong, HumanNPCTeamInfo>();

            foreach (var team in humannpcteams)
            {
                if (team.Value.members.ContainsKey(userid))
                {
                    foundTeams.Add(team);
                }
            }

            return foundTeams;
        }

        public bool CheckIfUsersShareTeam(ulong userid1, ulong userid2)
        {
            //there's no point in checking if the users actually exist here.
            //we're just comparing numbers in hash sets.

            var teams1 = GetAllTeamsContainingUserID(userid1);
            var teams2 = GetAllTeamsContainingUserID(userid2);

            //check if the two hash sets share at least 1 member
            bool overlap = false;

            foreach (var current_team in teams1)
            {
                if (teams2.ContainsKey(current_team.Key))
                {
                    overlap = true;
                    break;
                }
            }

            return overlap;
        }
        public enum HumanNPCAlignment
        {
            Foe,
            Neutral,
            Friend
        }

        //This is the top-level method for deciding whether you consider someone a friend, foe, or neutral.
        //Remember, relationships can be asymmetrical, so the order of the arguments matters.

        public HumanNPCAlignment GetAlignment(ulong userid1, ulong userid2)
        {
            //if you're neither a friend or a foe, you're neutral
            return HumanNPCAlignment.Neutral;
        }

        //This is an intermediary cache-like structure for fast lookup.
        //because team data doesn't change that often, there's no need for a bunch of foreach loops
        //every time you want to check if two players are friends, enemies or neutral.
        //so for the sake of performance, for every player there will exist a list
        //of friends and enemies

        //This data is not saved, it's generated on plugin load and every time team information changes
        //
        //Considering players/teams friends/foes is asymmetrical for maximum flexibility.
        //It means that, for example, 1 NPC will consider an entire team their friends and not attack them
        //and try to call for help within its alarm radius (if set) - while they consider him all an enemy and
        //will attack on sight.
        //When an NPC alarms other NPCs within their radius, the alarm request is only "heard" by those NPCs
        //that the calling NPC considers friends or neutral (no relationship info). And it's only "answered" when the responding NPC considers
        //the calling NPC friend.
        //

        //alignment set to "Neutral" is the same as having no alignment entry (applies to team/user)
        public static Dictionary<Tuple<ulong, ulong>, HumanNPCAlignment> relationshipCache;
        public static Dictionary<Tuple<ulong, ulong>, bool> conflictCache;

        //N^3*(2N) computational complexity. But totally worth it considered time saved every time you need to look up whether you should attack a particular player/NPC or not.
        //Remember, this only gets regenerated when something about team data changes (which should not be very often).
        //
        //I wonder if it's possible to delegate that to a separate thread, though? Just in case. Could display a message, like "Team data regenerating, please wait..." or w/e

        public HumanNPCAlignment GetRelationshipFromCache(ulong whoUserID, ulong whomUserID)
        {
            HumanNPCAlignment relationship = HumanNPCAlignment.Neutral;
            Tuple<ulong, ulong> relationshipTuple = new Tuple<ulong, ulong>(whoUserID, whomUserID);

            if (relationshipCache.ContainsKey(relationshipTuple)) //that means it's either friend or foe
            {
                relationship = relationshipCache[relationshipTuple];
            }

            return relationship;
        }

        public void GenerateRelationshipCache()
        {
            PrintWarning("Generating relationship cache... ");
            //step 0: clear the caches
            relationshipCache = new Dictionary<Tuple<ulong, ulong>, HumanNPCAlignment>();
            conflictCache = new Dictionary<Tuple<ulong, ulong>, bool>();

            //create a temporary Hashes: goodBois, badBois and conflictedBois where the first index is the team id and the second - a user/NPC id
            Dictionary<Tuple<ulong, ulong>, bool> goodBois = new Dictionary<Tuple<ulong, ulong>, bool>();
            Dictionary<Tuple<ulong, ulong>, bool> badBois = new Dictionary<Tuple<ulong, ulong>, bool>();
            Dictionary<Tuple<ulong, ulong>, bool> conflictedBois = new Dictionary<Tuple<ulong, ulong>, bool>();

            //Step 1: all all the teammates to their respective teams' goodBois.
            PrintWarning("[PHASE 1] Populating all the teams' friend lists with team members IDs");
            foreach (var team in humannpcteams)
            {
                PrintWarning($"          Processing team {team.Key}");
                foreach (var teamMember in team.Value.members)
                {
                    PrintWarning($"             Adding user {teamMember.Key} to the friend list of the team {team.Key}");
                    //Test if this one works, too, just curious
                    //goodBois.Add(Tuple.Create(team.Key, teamMember.Key), true);

                    goodBois.Add(new Tuple<ulong, ulong>(team.Key, teamMember.Key), true);
                }
            }

            //Step 2: get all the possible friend, foe and conflicted user ids for that particular team.
            PrintWarning("[PHASE 2] Populating every team's friend and foe lists according to that team's alignments");
            {
                foreach (var team in humannpcteams)
                {
                    PrintWarning($"           Processing team {team.Key}");
                    ulong teamId = team.Key;

                    if (team.Value.TeamAlignmentCount() == 0)
                    {
                        PrintWarning($"             Team {team.Key} has no alignments with other teams, doing nothing");
                    }

                    foreach (var teamAlignment in team.Value.teamAlignments)
                    {
                        string thisAlignment = FoFEnumToString[teamAlignment.Value];
                        PrintWarning($"             Processing a {thisAlignment} alignment of the team {team.Key} with team {teamAlignment.Key}");
                        ulong otherTeamId = teamAlignment.Key;

                        //get all the members of other team - if it exists! what if the entry exists, but the team has been deleted?
                        if (FindTeamNameByID(otherTeamId) != null)
                        {
                            PrintWarning($"             Team {otherTeamId} has been found, processing its members");
                            foreach (var otherTeamMember in humannpcteams[otherTeamId].members)
                            {
                                ulong otherTeamMemberId = otherTeamMember.Value.userid;
                                PrintWarning($"                 Processing member {otherTeamMemberId}");
                                var relationshipTupleKey = new Tuple<ulong, ulong>(teamId, otherTeamMemberId); //to identify the relationship between the team considering and that particular member
                                //check if it exists in conflictedBois for that team
                                if (conflictedBois.ContainsKey(relationshipTupleKey))
                                {
                                    //for now, do nothing
                                    PrintWarning($"                     Member {otherTeamMemberId} is already on the conflict list for the team {team.Key}, doing nothing");

                                }
                                else
                                {
                                    PrintWarning($"                     {team.Key} has no conflicted entries for {otherTeamMemberId}, continuing");
                                    //so far no conflicts. Good.
                                    //now check if the following situation is true:
                                    //if there exists an entry in goodBois for that relationship tuple key, mark goodBoiExists as true.
                                    //if there's an entry in badBois, mark badBoiExists as true.
                                    //if both are true, mark this for immediate conflict. This situation shouldn't have occured in first place
                                    //and if it ever happens, it is highly likely the server admin has tampered with stored data JSON by hand.

                                    bool goodBoiExists = (goodBois.ContainsKey(relationshipTupleKey));
                                    bool badBoiExists = (badBois.ContainsKey(relationshipTupleKey));

                                    if (goodBoiExists && badBoiExists) //again, I can't stress how ridiculously wrong idiot-proofing must've gone if this ever turns out to be true!
                                    {
                                        goodBois.Remove(relationshipTupleKey);
                                        badBois.Remove(relationshipTupleKey);

                                        conflictedBois.Add(relationshipTupleKey, true); //this will neutralise the relationship again
                                        PrintWarning($"[DISASTER]           {otherTeamMemberId} Something very, very wrong has just happened. If you haven't messed with the HumanNPCs.json file, contact the mod creator citing the code \"oopsie 384\".");
                                    }
                                    else
                                    {
                                        //what if just one of them exists,then?
                                        if (goodBoiExists && !badBoiExists)
                                        {
                                            if (teamAlignment.Value.Equals(HumanNPCAlignment.Friend))
                                            {
                                                //we do nothing here because it wouldn't change anything: you're a friend and you're requesting the same
                                                PrintWarning($"                     Member {otherTeamMemberId} is already on the friend list of the team {team.Key}, doing nothing");
                                            }
                                            else
                                            {
                                                //no more goodboi. this is now a conflict.
                                                goodBois.Remove(relationshipTupleKey);
                                                conflictedBois.Add(relationshipTupleKey, true);
                                                PrintWarning($"                    Conflict found: team {teamId} considers user {otherTeamMemberId} friendly, but membership in team {otherTeamId} is trying to set it to a foe. Adding to the conflict list (no longer on the friend list from now on)");
                                            }

                                        }
                                        else if (!goodBoiExists && badBoiExists) //maybe the other one?
                                        {
                                            if (teamAlignment.Value.Equals(HumanNPCAlignment.Foe))
                                            {
                                                PrintWarning($"                     Member {otherTeamMemberId} is already on the foe list of the team {team.Key}, doing nothing");
                                            }
                                            else
                                            {
                                                //no more badboi. this is now a conflict.
                                                badBois.Remove(relationshipTupleKey);
                                                conflictedBois.Add(relationshipTupleKey, true);
                                                PrintWarning($"                    Conflict found: team {teamId} considers user {otherTeamMemberId} a foe, but membership in team {otherTeamId} is trying to set it to friendly. Adding to the conflict list (no longer on the foe list from now on)");
                                            }

                                        }
                                        else //none of them exist (neutral) so far. make an appropriate entry, according to what the alignment is.
                                        {
                                            PrintWarning($"                     {team.Key} has no friend or foe entries for {otherTeamMemberId}, continuing");
                                            if (teamAlignment.Value.Equals(HumanNPCAlignment.Friend))
                                            {
                                                goodBois.Add(relationshipTupleKey, true);
                                                PrintWarning($"                     Adding {otherTeamMemberId} to the friend list of the team {team.Key}.");
                                            }
                                            else if (teamAlignment.Value.Equals(HumanNPCAlignment.Foe))
                                            {
                                                badBois.Add(relationshipTupleKey, true);
                                                PrintWarning($"                     Adding {otherTeamMemberId} to the foe list of the team {team.Key}.");
                                            }
                                            else
                                            {
                                                //it's something else than 0 (Friend) or 2 (Foe), so just do nothing. This doesn't happen "in nature".
                                                //Someone tampered with stored data JSON, maybe?
                                                PrintWarning($"                     Found alignment other than \"friend\" or \"foe\", doing nothing");
                                            }

                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            //Display conflicted bois now
            if (conflictedBois.Count == 0)
            {
                PrintWarning("No conflicts found.");
            }

            //Step 3: Now apply group friendship and animosities to individual users. Iterate through all team relationships.

            //Good bois go first, not because it matters (they're mutually exclusive with badbois)
            //but because they're good bois and they deserve to go first.

            PrintWarning("[PHASE 3] Applying the friend and foe lists to the teams' members");

            foreach (KeyValuePair<Tuple<ulong, ulong>, bool> teamFriendshipWithUser in goodBois)
            {
                var teamId = teamFriendshipWithUser.Key.Item1;
                var userTwoId = teamFriendshipWithUser.Key.Item2;
                PrintWarning($"          Processing friendship of the team {teamId} with the user {userTwoId} ");
                //iterate through that particular team...
                foreach (var userOne in humannpcteams[teamId].members)
                {
                    var userOneId = userOne.Key;
                    //FINALLY. You've got both keys, the value is always true... DO IT
                    PrintWarning($"             Processing team member {userOneId}");
                    if (userOneId != userTwoId) //don't add yourself, you're always neutral towards yourself
                    {
                        var relationshipTuple = new Tuple<ulong, ulong>(userOneId, userTwoId);

                        //check for existing conflicts first

                        if (conflictCache.ContainsKey(relationshipTuple))
                        {
                            PrintWarning($"             There already exists a conflict between {userOneId} and {userTwoId}, doing nothing");
                        }
                        else
                        {
                            if (!relationshipCache.ContainsKey(relationshipTuple))
                            {
                                relationshipCache.Add(relationshipTuple, HumanNPCAlignment.Friend);
                                PrintWarning($"             {userOneId} will now consider {userTwoId} a friend");
                            }
                            else
                            {
                                //check what sort of relationship is there. if it's still "friend", do nothing, but if it's "foe",
                                //we have a conflict. Add that relationship to conflicted so it's ignored from now on
                                if (relationshipCache[relationshipTuple].Equals(HumanNPCAlignment.Friend))
                                {
                                    //it's fine, was a friend, gonna stay a friend, do nothing
                                    PrintWarning($"            {userOneId} already considers {userTwoId} a friend, doing nothing");
                                }
                                else
                                {
                                    //new conflict discovered. don't add to friends, add to conflicts, remove from foes
                                    conflictCache.Add(relationshipTuple, true);
                                    relationshipCache.Remove(relationshipTuple);

                                    PrintWarning($"[CONFLICT]  {userOneId} considers {userTwoId} a foe, but {userTwoId} is on the friend list of team {teamId}. Relationship between the two users set to neutral.");
                                }
                            }
                        }
                    }
                    else
                    {
                        PrintWarning($"             The two user ids are the same: {userOneId} and {userTwoId}, doing nothing");
                    }
                }
            }

            //same with bad bois
            foreach (KeyValuePair<Tuple<ulong, ulong>, bool> teamFriendshipWithUser in badBois)
            {
                var teamId = teamFriendshipWithUser.Key.Item1;
                var userTwoId = teamFriendshipWithUser.Key.Item2;
                PrintWarning($"          Processing animosity of the team {teamId} with the user {userTwoId} ");
                //iterate through that particular team...
                foreach (var userOne in humannpcteams[teamId].members)
                {
                    var userOneId = userOne.Key;
                    //FINALLY. You've got both keys, the value is always true... DO IT
                    PrintWarning($"             Processing team member {userOneId}");
                    if (userOneId != userTwoId) //don't add yourself, you're always neutral towards yourself
                    {
                        var relationshipTuple = new Tuple<ulong, ulong>(userOneId, userTwoId);

                        //check for existing conflicts first

                        if (conflictCache.ContainsKey(relationshipTuple))
                        {
                            PrintWarning($"             There already exists a conflict between {userOneId} and {userTwoId}, doing nothing");
                        }
                        else
                        {
                            if (!relationshipCache.ContainsKey(relationshipTuple))
                            {
                                relationshipCache.Add(relationshipTuple, HumanNPCAlignment.Foe);
                                PrintWarning($"             {userOneId} will now consider {userTwoId} a foe");
                            }
                            else
                            {
                                //check what sort of relationship is there. if it's still "friend", do nothing, but if it's "foe",
                                //we have a conflict. Add that relationship to conflicted so it's ignored from now on
                                if (relationshipCache[relationshipTuple].Equals(HumanNPCAlignment.Foe))
                                {
                                    //it's fine, was a friend, gonna stay a friend, do nothing
                                    PrintWarning($"            {userOneId} already considers {userTwoId} a foe, doing nothing");
                                }
                                else
                                {
                                    //new conflict discovered. don't add to friends, add to conflicts, remove from foes
                                    conflictCache.Add(relationshipTuple, true);
                                    relationshipCache.Remove(relationshipTuple);

                                    PrintWarning($"[CONFLICT]  {userOneId} considers {userTwoId} a friend, but {userTwoId} is on the foe list of team {teamId}. Relationship between the two users set to neutral.");
                                }
                            }
                        }
                    }
                    else
                    {
                        PrintWarning($"             The two user ids are the same: {userOneId} and {userTwoId}, doing nothing");
                    }
                }
            }
        }

        // Nikedemos
        public class HumanNPCTeamInfo
        {
            public ulong teamid;
            public string teamName;
            //this is going to be Oxide.Plugins.Hash: because HumanNPCTeamMember can have additional properties (like isAdmin or w/e)
            //I'm trying to future-proof it
            public Hash<ulong, HumanNPCTeamMember> members;
            public Hash<ulong, HumanNPCAlignment> teamAlignments;

            public HumanNPCTeamInfo(string teamName)
            {
                this.teamName = teamName;
                this.teamid = (ulong)UnityEngine.Random.Range(1, 2147483647); //0 is used when no team is found

                this.members = new Hash<ulong, HumanNPCTeamMember>();
                this.teamAlignments = new Hash<ulong, HumanNPCAlignment>();
            }

            public bool TeamAlignmentSet(ulong teamid, HumanNPCAlignment alignment)
            {
                bool success = false;
                //Here we're assuming that teamid exists, it's all meaningless numbers until it's cached, anyway.
                //Check if you're not setting alignment with yourself
                if (teamid != this.teamid)
                {
                    if (alignment.Equals(HumanNPCAlignment.Neutral))
                    {
                        //if an entry exists, get rid of it
                        if (this.teamAlignments.ContainsKey(teamid))
                        {
                            this.teamAlignments.Remove(teamid);
                            success = true;
                        }
                    }
                    else
                    {
                        //set the entry
                        this.teamAlignments[teamid] = alignment;
                        success = true;
                    }
                }

                return success;
            }

            public HumanNPCAlignment TeamAlignmentGet(ulong teamid)
            {
                HumanNPCAlignment alignment = HumanNPCAlignment.Neutral;

                if (this.teamAlignments.ContainsKey(teamid))
                {
                    alignment = this.teamAlignments[teamid];
                }

                return alignment;
            }

            public int MemberCount()
            {
                int foundMembers = 0;
                if (this.members != null)
                    foundMembers = this.members.Count();

                return foundMembers;
            }

            public int TeamAlignmentCount()
            {
                int foundTeamAlignments = 0;
                if (this.members != null)
                    foundTeamAlignments = this.teamAlignments.Count();

                return foundTeamAlignments;
            }

            public BasePlayer MemberAdd(ulong userid)
            {
                BasePlayer playerFound = null;

                //we need to check if the player/NPC with that ID exists.

                //first, BasePlayers...

                foreach (BasePlayer player in UnityEngine.Object.FindObjectsOfType<BasePlayer>())
                {
                    if (player.userID == userid)
                    {
                        playerFound = player;

                        break;
                    }
                }

                if (playerFound != null)
                {
                    this.members.Add(playerFound.userID, new HumanNPCTeamMember(playerFound.userID));
                }
                else
                {
                    //okay maybe HumanPlayers?
                    foreach (HumanPlayer humanplayer in UnityEngine.Object.FindObjectsOfType<HumanPlayer>())
                    {
                        var playerComponent = humanplayer.GetComponent<BasePlayer>();
                        if (humanplayer.info.userid == userid)
                        {
                            playerFound = playerComponent;
                            this.members.Add(playerFound.userID, new HumanNPCTeamMember(playerFound.userID));
                            break;
                        }
                    }
                }

                return playerFound;
            }

            public bool MemberRemove(ulong userid)
            {
                //we're removing, we don't even care if that player is online, so less checks here tha when adding
                bool success = false;

                if (this.members.ContainsKey(userid))
                {
                    this.members.Remove(userid);
                    success = true;
                }
                return success;
            }
        }
        // Nikedemos

        //////////////////////////////////////////////////////
        ///  class HumanNPCInfo
        ///  NPC information that will be saved inside the datafile
        ///  public => will be saved in the data file
        ///  non public => won't be saved in the data file
        //////////////////////////////////////////////////////
        public class HumanNPCInfo
        {
            public ulong userid;
            public string displayName;
            public bool invulnerability;
            public float health;
            public bool respawn;
            public float respawnSeconds;
            public SpawnInfo spawnInfo;
            public string waypoint;
            public float collisionRadius;
            public string spawnkit;
            public float damageAmount;
            public float damageDistance;
            public float damageInterval;
            public float attackDistance;
            public float maxDistance;
            public bool hostile;
            public bool ahostile;
            public float speed;
            public bool stopandtalk;
            public float stopandtalkSeconds;
            public bool enable;
            public bool persistent;
            public bool lootable;
            public float hitchance;
            public float reloadDuration;
            public bool needsAmmo;
            public bool dropWeapon;
            public bool defend;
            public bool evade;
            public bool follow;
            public float followtime;
            public float evdist;
            public bool allowsit;
            public bool allowride;
            public float band = 0;

            public List<string> message_hello;
            public List<string> message_bye;
            public List<string> message_use;
            public List<string> message_hurt;
            public List<string> message_kill;
            public Dictionary<DamageType, float> protections = new Dictionary<DamageType, float>();

            // Nikedemos
            public bool hostileTowardsArmed;
            public bool hostileTowardsArmedHard;
            public bool raiseAlarm;
            public List<string> message_armed;
            public List<string> message_alarm;

            public string instrument;

            public HumanNPCInfo(ulong userid, Vector3 position, Quaternion rotation)
            {
                this.userid = userid;
                displayName = "NPC";
                invulnerability = true;
                health = 50;
                hostile = false;
                ahostile = false;
                needsAmmo = true;
                dropWeapon = true;
                respawn = true;
                respawnSeconds = 60;
                spawnInfo = new SpawnInfo(position, rotation);
                collisionRadius = 10;
                damageDistance = 3;
                damageAmount = 10;
                attackDistance = 100;
                maxDistance = 200;
                hitchance = 0.75f;
                speed = 3;
                stopandtalk = true;
                stopandtalkSeconds = 3;
                enable = true;
                persistent = true;
                lootable = true;
                defend = false;
                evade = false;
                evdist = 0f;
                follow = true;
                followtime = 30f;
                allowsit = false;
                allowride = false;
                damageInterval = 2;

                // Nikedemos
                hostileTowardsArmed = false;
                hostileTowardsArmedHard = false;
                raiseAlarm = false;
                instrument = null;
                band = 0;

                for(var i = 0; i < (int)DamageType.LAST; i++)
                {
                    protections[(DamageType)i] = 0f;
                }
            }

            public HumanNPCInfo Clone(ulong userid)
            {
                return new HumanNPCInfo(userid, spawnInfo.position, spawnInfo.rotation)
                {
                    displayName = displayName,
                    invulnerability = invulnerability,
                    health = health,
                    respawn = respawn,
                    respawnSeconds = respawnSeconds,
                    waypoint = waypoint,
                    collisionRadius = collisionRadius,
                    spawnkit = spawnkit,
                    damageAmount = damageAmount,
                    damageDistance = damageDistance,
                    attackDistance = attackDistance,
                    maxDistance = maxDistance,
                    hostile = hostile,
                    ahostile = ahostile,
                    speed = speed,
                    stopandtalk = stopandtalk,
                    stopandtalkSeconds = stopandtalkSeconds,
                    lootable = lootable,
                    defend = defend,
                    evade = evade,
                    follow = follow,
                    followtime = followtime,
                    evdist = evdist,
                    allowsit = allowsit,
                    allowride = allowride,
                    damageInterval = damageInterval,
                    message_hello = message_hello?.ToList(),
                    message_bye = message_bye?.ToList(),
                    message_use = message_use?.ToList(),
                    message_hurt = message_hurt?.ToList(),
                    message_kill = message_kill?.ToList(),
                    needsAmmo = needsAmmo,
                    dropWeapon = dropWeapon,
                    hitchance = hitchance,
                    reloadDuration = reloadDuration,
                    protections = protections?.ToDictionary(p => p.Key, p => p.Value),
                    // Nikedemos
                    hostileTowardsArmed = hostileTowardsArmed,
                    hostileTowardsArmedHard = hostileTowardsArmedHard,
                    raiseAlarm = raiseAlarm,
                    message_armed = message_armed?.ToList(),
                    message_alarm = message_alarm?.ToList(),
                    instrument = instrument,
                    band = band
                };
            }
        }

        private class NPCEditor : MonoBehaviour
        {
            public BasePlayer player;
            public HumanPlayer targetNPC;
            private void Awake()
            {
                player = GetComponent<BasePlayer>();
            }
        }

        public static Dictionary<string, AmmoTypes> ammoTypes = new Dictionary<string, AmmoTypes>();
        //{
        //    {"bow.hunting", AmmoTypes.BOW_ARROW},
        //    {"crossbow", AmmoTypes.BOW_ARROW},
        //    {"pistol.eoka", AmmoTypes.HANDMADE_SHELL},
        //    {"pistol.semiauto", AmmoTypes.PISTOL_9MM},
        //    {"pistol.revolver", AmmoTypes.PISTOL_9MM},
        //    {"rifle.ak", AmmoTypes.RIFLE_556MM},
        //    {"rifle.bolt", AmmoTypes.RIFLE_556MM},
        //    {"shotgun.pump", AmmoTypes.SHOTGUN_12GUAGE},
        //    {"shotgun.waterpipe", AmmoTypes.HANDMADE_SHELL},
        //    {"smg.2", AmmoTypes.PISTOL_9MM},
        //    {"smg.thompson", AmmoTypes.PISTOL_9MM}
        //};

        private static Dictionary<string, BaseProjectile> weaponProjectile = new Dictionary<string, BaseProjectile>();

        protected override void LoadDefaultConfig()
        {
        }

        private void CheckCfg<T>(string Key, ref T var)
        {
            if(Config[Key] is T) var = (T)Config[Key];
            else Config[Key] = var;
        }

        private void Init()
        {
            ammoTypes = new Dictionary<string, AmmoTypes>();
            weaponProjectile = new Dictionary<string, BaseProjectile>();
            CheckCfg("Chat", ref chat);
            CheckCfg("Relocate to Zero at Wipe", ref relocateZero);
            SaveConfig();

            // Nikedemos
            FoFEnumToString.Add(HumanNPCAlignment.Foe, "foe");
            FoFEnumToString.Add(HumanNPCAlignment.Neutral, "neutral");
            FoFEnumToString.Add(HumanNPCAlignment.Friend, "friend");

            FoFStringToEnum.Add("foe", HumanNPCAlignment.Foe);
            FoFStringToEnum.Add("neutral", HumanNPCAlignment.Neutral);
            FoFStringToEnum.Add("friend", HumanNPCAlignment.Friend);
        }

        private static bool GetBoolValue(string value)
        {
            if(value == null) return false;
            value = value.Trim().ToLower();
            switch(value)
            {
                case "t":
                case "true":
                case "1":
                case "yes":
                case "y":
                case "on":
                    return true;
                case "false":
                case "0":
                case "n":
                case "no":
                case "off":
                default:
                    return false;
            }
        }

        private void Loaded()
        {
            LoadData();

            var filter = RustExtension.Filter.ToList();
            filter.Add("Look rotation viewing vector is zero");
            RustExtension.Filter = filter.ToArray();

            //Nikedemos
            foreach(var theteam in storedData.HumanNPCTeams)
            {
                humannpcteams[theteam.teamid] = theteam;
            }

            GenerateRelationshipCache();
        }

        private void Unload()
        {
            //var HumanNPCMono = UnityEngine.Object.FindObjectsOfType<HumanPlayer>();
            var HumanNPCMono = Resources.FindObjectsOfTypeAll<HumanPlayer>();
            foreach(var mono in HumanNPCMono)
            {
                PrintWarning($"Deleting {mono.info.displayName} ({mono.info.userid})");
                mono.GetComponent<BasePlayer>().Kill();
            }

            var npcEditors = UnityEngine.Object.FindObjectsOfType<NPCEditor>();
            foreach(var gameObj in npcEditors)
            {
                UnityEngine.Object.Destroy(gameObj);
            }
            SaveData();
        }

        private void SaveData()
        {
            if(storedData == null || !save) return;
            data.WriteObject(storedData);
            save = false;
        }

        private void LoadData()
        {
            data = Interface.Oxide.DataFileSystem.GetFile(nameof(HumanNPC));
            data.Settings.ReferenceLoopHandling = ReferenceLoopHandling.Ignore;
            data.Settings.Converters = new JsonConverter[] { new SpawnInfoConverter(), new UnityQuaternionConverter(), new UnityVector3Converter() };

            try
            {
                storedData = data.ReadObject<StoredData>();
            }
            catch
            {
                storedData = new StoredData();
            }
            data.Clear();
            foreach(var thenpc in storedData.HumanNPCs)
            {
                humannpcs[thenpc.userid] = thenpc;
            }
            //new stuff
            foreach (var theteam in storedData.HumanNPCTeams)
            {
                humannpcteams[theteam.teamid] = theteam;
            }

            GenerateRelationshipCache();
        }

        //////////////////////////////////////////////////////
        ///  Oxide Hooks
        //////////////////////////////////////////////////////

        //////////////////////////////////////////////////////
        ///  OnServerInitialized()
        ///  called when the server is done being initialized
        //////////////////////////////////////////////////////
        private void OnServerInitialized()
        {
            colBuffer = Vis.colBuffer;
            eyesPosition = new Vector3(0f, 0.5f, 0f);
            Vector3Down = new Vector3(0f, -1f, 0f);
            PathFinding = (PathFinding)plugins.Find(nameof(PathFinding));
            playerLayer = LayerMask.GetMask("Player (Server)");
            targetLayer = LayerMask.GetMask("Player (Server)", "AI", "Deployed", "Construction");
            groundLayer = LayerMask.GetMask("Construction", "Terrain", "World");

            foreach(var info in ItemManager.itemList)
            {
                var baseProjectile = info.GetComponent<ItemModEntity>()?.entityPrefab.Get().GetComponent<BaseProjectile>();
                if(baseProjectile == null) continue;
                weaponProjectile.Add(info.shortname, baseProjectile);

                var projectile = baseProjectile.primaryMagazine.ammoType.GetComponent<ItemModProjectile>();
                if(projectile != null && !ammoTypes.ContainsKey(info.shortname))
                {
                    ammoTypes.Add(info.shortname, projectile.ammoType);
                }
            }

            RefreshAllNPC();
        }

        ///////////////////////////////////////////////////////
        ///  OnNewSave()
        ///  called when a server performs a save on a new map
        ///////////////////////////////////////////////////////
        void OnNewSave(string strFilename)
        {
            if(!relocateZero) return;
            //if (ConVar.Server.levelurl != "") return; // Skip custom maps which may persist month over month.
            // Relocate NPCs to Vector0
            foreach(var thenpc in storedData.HumanNPCs)
            {
                thenpc.spawnInfo = new SpawnInfo(new Vector3(), new Quaternion());
            }
            SaveData();
            RefreshAllNPC();
        }

        //////////////////////////////////////////////////////
        ///  OnServerSave()
        ///  called when a server performs a save
        //////////////////////////////////////////////////////
        private void OnServerSave() => SaveData();

        private void OnServerShutdown() => SaveData();

        //////////////////////////////////////////////////////
        /// OnPlayerInput(BasePlayer player, InputState input)
        /// Called when a plugin or player presses a button
        //////////////////////////////////////////////////////
        private void OnPlayerInput(BasePlayer player, InputState input)
        {
            if(player == null || input == null) return;
//            if(input.current.buttons > 0)
//                Puts($"OnPlayerInput: {input.current.buttons}");
            if(!input.WasJustPressed(BUTTON.USE)) return;

            Quaternion currentRot;
            TryGetPlayerView(player, out currentRot);
            var hitpoints = Physics.RaycastAll(new Ray(player.transform.position + eyesPosition, currentRot * Vector3.forward), 5f, playerLayer);
            Array.Sort(hitpoints, (a, b) => a.distance == b.distance ? 0 : a.distance > b.distance ? 1 : -1);
            for(var i = 0; i < hitpoints.Length; i++)
            {
#if DEBUG
                Interface.Oxide.LogInfo("OnPlayerInput: {0} ({1})", player.displayName, hitpoints[i].collider.name);
#endif
                var humanPlayer = hitpoints[i].collider.GetComponentInParent<HumanPlayer>();
                if(humanPlayer != null)
                {
                    if(humanPlayer.locomotion.sitting && !humanPlayer.locomotion.isRiding)
                    {
                        humanPlayer.locomotion.Stand();
                    }
                    if(humanPlayer.info.stopandtalk && humanPlayer.locomotion.attackEntity == null)
                    {
                        humanPlayer.LookTowards(player.transform.position);
                        humanPlayer.TemporaryDisableMove();
                    }
                    if(humanPlayer.info.message_use != null && humanPlayer.info.message_use.Count != 0)
                    {
                        SendMessage(humanPlayer, player, GetRandomMessage(humanPlayer.info.message_use));
                    }
                    Interface.Oxide.CallHook("OnUseNPC", humanPlayer.player, player);
                    break;
                }
            }
        }

        //////////////////////////////////////////////////////
        /// OnEntityTakeDamage(BaseCombatEntity entity, HitInfo hitinfo)
        /// Called when an entity gets attacked (can be anything, building, animal, player ..)
        //////////////////////////////////////////////////////
        private void OnEntityTakeDamage(BaseCombatEntity entity, HitInfo hitinfo)
        {
            var humanPlayer = entity.GetComponent<HumanPlayer>();
            if(humanPlayer != null)
            {
#if DEBUG
                Interface.Oxide.LogInfo($"OnEntityTakeDamage(by {entity.name})");
#endif
                // Nikedemos
                //if you're supposed to retaliate against your team members,
                //check if the initiator is part of the same team
                if(hitinfo.Initiator is BaseCombatEntity && !(hitinfo.Initiator is Barricade) && humanPlayer.info.defend)
                {
                    humanPlayer.StartAttackingEntity((BaseCombatEntity)hitinfo.Initiator);
                    if(humanPlayer.info.raiseAlarm == true)
                    {
                        RaiseAlarm((BasePlayer)entity, (BasePlayer)hitinfo.Initiator);
                    }
                }
                if(humanPlayer.info.message_hurt != null && humanPlayer.info.message_hurt.Count != 0)
                {
                    if(hitinfo.InitiatorPlayer != null)
                    {
                        SendMessage(humanPlayer, hitinfo.InitiatorPlayer, GetRandomMessage(humanPlayer.info.message_hurt));
                    }
                }
                Interface.Oxide.CallHook("OnHitNPC", entity.GetComponent<BaseCombatEntity>(), hitinfo);
                if(humanPlayer.info.invulnerability)
                {
                    hitinfo.damageTypes = new DamageTypeList();
                    hitinfo.DoHitEffects = false;
                    hitinfo.HitMaterial = 0;
                }
                else
                {
                    humanPlayer.protection.Scale(hitinfo.damageTypes);
                }

                if(humanPlayer.locomotion.sitting && !humanPlayer.locomotion.isRiding)
                {
                    humanPlayer.locomotion.Stand();
                }
                humanPlayer.locomotion.Evade();
            }
        }

        private bool CanDropActiveItem(BasePlayer player)
        {
            var humanPlayer = player.GetComponent<HumanPlayer>();
            if(humanPlayer?.info == null) return true;
#if DEBUG
            Puts($"Item dropped by NPC {player.displayName}");
#endif
            if(humanPlayer.info.dropWeapon) return true;
            return false;
        }

        //////////////////////////////////////////////////////
        /// OnEntityDeath(BaseEntity entity, HitInfo hitinfo)
        /// Called when an entity gets killed (can be anything, building, animal, player ..)
        //////////////////////////////////////////////////////
        private void OnEntityDeath(BaseCombatEntity entity, HitInfo hitinfo)
        {
            try
            {
                var killer = (entity.lastAttacker ?? hitinfo?.Initiator).GetComponent<HumanPlayer>();
                if(killer != null)
                {
                    killer.EndFollowingEntity();
                    killer.EndAttackingEntity();
                }
            }
            catch {}
            var humanPlayer = entity.GetComponent<HumanPlayer>();
            if(humanPlayer?.info == null) return;
            if(!humanPlayer.info.lootable)
            {
                humanPlayer.player.inventory?.Strip();
            }
            var player = hitinfo?.InitiatorPlayer;
            if(player != null)
            {
                if(humanPlayer.info.message_kill != null && humanPlayer.info.message_kill.Count > 0)
                {
                    SendMessage(humanPlayer, player, GetRandomMessage(humanPlayer.info.message_kill));
                }
                //if(humanPlayer.info.xp > 0)
                //    player.xp.Add(Definitions.Cheat, humanPlayer.info.xp);
            }
            Interface.Oxide.CallHook("OnKillNPC", entity.GetComponent<BasePlayer>(), hitinfo);
            if(humanPlayer.info.respawn)
            {
                timer.Once(humanPlayer.info.respawnSeconds, () => SpawnOrRefresh(humanPlayer.info.userid));
            }
        }

        private object CanLootPlayer(BasePlayer target, BasePlayer looter)
        {
            try
            {
                var humanPlayer = target.GetComponent<HumanPlayer>();
                if(humanPlayer != null && !humanPlayer.info.lootable)
                {
                    NextTick(looter.EndLooting);
                    return false;
                }
            }
            catch {}
            return null;
        }

        private void OnLootPlayer(BasePlayer looter, BasePlayer target)
        {
            if(humannpcs[target.userID] != null)
            {
                Interface.Oxide.CallHook("OnLootNPC", looter.inventory.loot, target, target.userID);
            }
        }

        private void OnLootEntity(BasePlayer looter, BaseEntity entity)
        {
            if(looter == null || !(entity is PlayerCorpse)) return;
            var userId = ((PlayerCorpse)entity).playerSteamID;
            if(humannpcs[userId] != null)
            {
                Interface.Oxide.CallHook("OnLootNPC", looter.inventory.loot, entity, userId);
            }
        }

        //////////////////////////////////////////////////////
        /// End of Oxide Hooks
        //////////////////////////////////////////////////////
        // Nikedemos

        //can use substrings (Contains instead of Equals), so names with spaces etc should be okay
        public ulong FindTeamIDByName(string name)
        {
            //returns the id of the team or 0 if the team doesn't exist
            ulong foundId = 0;

            foreach (var pair in humannpcteams) //will return the first found, so in case of teamA and teamB, when using just "team", it will return teamA ID
            {
                if (pair.Value.teamName.Contains(name))
                {
                    foundId = pair.Value.teamid;
                    break;
                }
            }

            return foundId;
        }

        public string FindTeamNameByID(ulong teamId)
        {
            //will return the name or null if wrong ID

            string foundName = null;

            if (humannpcteams.ContainsKey(teamId))
            {
                foundName = humannpcteams[teamId].teamName;
            }

            return foundName;
        }

        //sort of reverse wrapper for TeamNameToId, really
        public ulong AddTeamByName(string name)
        {
            //returns newly added teamid or 0 if a team with that name already exists
            ulong teamId = 0;

            //first, check if the team with that name already exists!

            if (FindTeamIDByName(name) == 0) //0 means no ID by that name found
            {
                HumanNPCTeamInfo npcTeamInfo = new HumanNPCTeamInfo(name);
                teamId = npcTeamInfo.teamid;

                humannpcteams[teamId] = npcTeamInfo;
                storedData.HumanNPCTeams.Add(npcTeamInfo);
                save = true;
            }

            return teamId; //will return 0 on failure to add a team
        }
        // Nikedemos

        private Dictionary<ulong, HumanPlayer> cache = new Dictionary<ulong, HumanPlayer>();

        public HumanPlayer FindHumanPlayerByID(ulong userid)
        {
            HumanPlayer humanPlayer;
            if(cache.TryGetValue(userid, out humanPlayer)) return humanPlayer;
            var allBasePlayer = Resources.FindObjectsOfTypeAll<HumanPlayer>();
            foreach(var humanplayer in allBasePlayer)
            {
                if(humanplayer.player.userID != userid) continue;
                cache[userid] = humanplayer;
                return humanplayer;
            }
            return null;
        }

        public HumanPlayer FindHumanPlayer(string nameOrId)
        {
            if(string.IsNullOrEmpty(nameOrId)) return null;
            var allBasePlayer = Resources.FindObjectsOfTypeAll<HumanPlayer>();
            foreach(var humanplayer in allBasePlayer)
            {
                if(!nameOrId.Equals(humanplayer.player.UserIDString) && !humanplayer.player.displayName.Contains(nameOrId, CompareOptions.OrdinalIgnoreCase)) continue;
                return humanplayer;
            }
            return null;
        }

        private BasePlayer FindPlayerByID(ulong userid)
        {
            var allBasePlayer = Resources.FindObjectsOfTypeAll<BasePlayer>();
            foreach(BasePlayer player in allBasePlayer)
            {
                if(player.userID == userid) return player;
            }
            return null;
        }

        public BasePlayer FindPlayer(string nameOrId)
        {
            if (string.IsNullOrEmpty(nameOrId)) return null;
            var allBasePlayer = Resources.FindObjectsOfTypeAll<BasePlayer>();
            foreach (var humanplayer in allBasePlayer)
            {
                if (!nameOrId.Equals(humanplayer.UserIDString) && !humanplayer.displayName.Contains(nameOrId, CompareOptions.OrdinalIgnoreCase)) continue;
                return humanplayer;
            }
            return null;
        }

        private void RefreshAllNPC()
        {
            List<ulong> npcspawned = new List<ulong>();
            foreach(KeyValuePair<ulong, HumanNPCInfo> pair in humannpcs)
            {
                if (!pair.Value.enable) continue;
                if (!pair.Value.persistent) continue;
                npcspawned.Add(pair.Key);
                SpawnOrRefresh(pair.Key);
            }
        }

        private void SpawnOrRefresh(ulong userid)
        {
            BasePlayer findplayer = FindPlayerByID(userid);

            if(findplayer == null || findplayer.IsDestroyed)
            {
                cache.Remove(userid);
                SpawnNPC(userid, false);
            }
            else RefreshNPC(findplayer, false);
        }

        private void SpawnNPC(ulong userid, bool isediting)
        {
            HumanNPCInfo info;
            if(!humannpcs.TryGetValue(userid, out info)) return;
            if(!isediting && !info.enable) return;
            var newPlayer = GameManager.server.CreateEntity("assets/prefabs/player/player.prefab", info.spawnInfo.position, info.spawnInfo.rotation).ToPlayer();
            var humanPlayer = newPlayer.gameObject.AddComponent<HumanPlayer>();
            humanPlayer.SetInfo(info);
            newPlayer.Spawn();

            humanPlayer.UpdateHealth(info);
            cache[userid] = humanPlayer;
            UpdateInventory(humanPlayer);
            Interface.Oxide.CallHook("OnNPCRespawn", newPlayer);
            Puts($"Spawned NPC: {humanPlayer.player.displayName}/{userid}");
        }

        private void UpdateInventory(HumanPlayer humanPlayer)
        {
            humanPlayer.player.inventory.DoDestroy();
            humanPlayer.player.inventory.ServerInit(humanPlayer.player);
            if(!string.IsNullOrEmpty(humanPlayer.info.spawnkit))
            {
                //player.inventory.Strip();
                Kits?.Call("GiveKit", humanPlayer.player, humanPlayer.info.spawnkit);
                if(humanPlayer.EquipFirstInstrument() == null)
                {
                    if(humanPlayer.EquipFirstWeapon() == null && humanPlayer.EquipFirstTool() == null)// && humanPlayer.EquipFirstInstrument() == null)
                    {
                        humanPlayer.EquipFirstMisc();
                    }
                }
            }
            /*player.SV_ClothingChanged();
            if(humanPlayer.info.protections != null)
            {
                player.baseProtection.Clear();
                foreach(var protection in info.protections)
                    player.baseProtection.Add(protection.Key, protection.Value);
            }*/
            humanPlayer.player.inventory.ServerUpdate(0f);
        }

        private void KillNpc(BasePlayer player)
        {
            if(player.userID >= 76560000000000000L || player.userID <= 0L || player.IsDestroyed) return;
            cache.Remove(player.userID);
            player.KillMessage();
        }

        public void RefreshNPC(BasePlayer player, bool isediting)
        {
            HumanNPCInfo info;
            if(!humannpcs.TryGetValue(player.userID, out info)) return;
            KillNpc(player);
            if(!info.enable && !isediting)
            {
                Puts($"NPC was killed because he is disabled: {player.userID}");
                return;
            }
            SpawnOrRefresh(player.userID);
        }

        public void UpdateNPC(BasePlayer player, bool isediting)
        {
            HumanNPCInfo info;
            if(!humannpcs.TryGetValue(player.userID, out info)) return;
            if(!info.enable && !isediting)
            {
                KillNpc(player);
                Puts($"NPC was killed because he is disabled: {player.userID}");
                return;
            }
            if(player.GetComponent<HumanPlayer>() != null)
            {
                UnityEngine.Object.Destroy(player.GetComponent<HumanPlayer>());
            }
            var humanplayer = player.gameObject.AddComponent<HumanPlayer>();
            humanplayer.SetInfo(info, true);
            cache[player.userID] = humanplayer;
            Puts("Refreshed NPC: " + player.userID);
        }

        public HumanPlayer CreateNPC(Vector3 position, Quaternion currentRot, string name = "NPC", ulong clone = 0)
        {
            HumanNPCInfo npcInfo = null;
            var userId = (ulong)UnityEngine.Random.Range(0, 2147483647);
            if(clone != 0)
            {
                HumanNPCInfo tempInfo;
                if(humannpcs.TryGetValue(clone, out tempInfo))
                {
                    npcInfo = tempInfo.Clone(userId);
                    npcInfo.spawnInfo = new SpawnInfo(position, currentRot);
                }
            }
            if(npcInfo == null) npcInfo = new HumanNPCInfo(userId, position, currentRot);
            npcInfo.displayName = name;
            RemoveNPC(userId);

            humannpcs[userId] = npcInfo;
            storedData.HumanNPCs.Add(npcInfo);
            save = true;

            SpawnNPC(userId, true);

            return FindHumanPlayerByID(userId);
        }

        public void RemoveNPC(ulong npcid)
        {
            if(humannpcs.ContainsKey(npcid))
            {
                storedData.HumanNPCs.Remove(humannpcs[npcid]);
                humannpcs[npcid] = null;
            }
            cache.Remove(npcid);
            var npc = FindHumanPlayerByID(npcid);
            if(npc?.player != null && !npc.player.IsDestroyed)
            {
                npc.player.KillMessage();
            }
            save = true;
            SaveData();
        }

        private bool hasAccess(BasePlayer player)
        {
            if(player.net.connection.authLevel < 1)
            {
                SendReply(player, "You don't have access to this command");
                return false;
            }
            return true;
        }

        private bool TryGetPlayerView(BasePlayer player, out Quaternion viewAngle)
        {
            viewAngle = new Quaternion(0f, 0f, 0f, 0f);
            if(player.serverInput?.current == null) return false;
            viewAngle = Quaternion.Euler(player.serverInput.current.aimAngles);
            return true;
        }

        private bool TryGetClosestRayPoint(Vector3 sourcePos, Quaternion sourceDir, out object closestEnt, out Vector3 closestHitpoint)
        {
            Vector3 sourceEye = sourcePos + new Vector3(0f, 1.5f, 0f);
            Ray ray = new Ray(sourceEye, sourceDir * Vector3.forward);

            var hits = Physics.RaycastAll(ray);
            float closestdist = 999999f;
            closestHitpoint = sourcePos;
            closestEnt = false;
            for(var i = 0; i < hits.Length; i++)
            {
                var hit = hits[i];
                if(hit.collider.GetComponentInParent<TriggerBase>() == null && hit.distance < closestdist)
                {
                    closestdist = hit.distance;
                    closestEnt = hit.collider;
                    closestHitpoint = hit.point;
                }
            }

            if(closestEnt is bool) return false;
            return true;
        }

        private static bool CanSee(HumanPlayer npc, BaseEntity target)
        {
#if DEBUG
            Interface.Oxide.LogInfo($"CanSee(): {npc.transform.position} looking at {target.transform.position}");
#endif
            var source = npc.player;
            var weapon = source.GetActiveItem()?.GetHeldEntity() as BaseProjectile;
            var pos = source.transform.position + source.GetOffset();
            if(weapon?.MuzzlePoint != null)
            {
                pos += Quaternion.LookRotation(target.transform.position - source.transform.position) * weapon.MuzzlePoint.position;
#if DEBUG
                Interface.Oxide.LogInfo($"CanSee(): MuzzlePoint NULL");
#endif
            }
            else
            {
#if DEBUG
                Interface.Oxide.LogInfo($"CanSee(): MuzzlePoint NOT null");
#endif
            }

            //if(Physics.Linecast(source.transform.position + new Vector3(0, 1.6f, 0), target.transform.position + new Vector3(0, 1.6f, 0), obstructionMask))
            if(Physics.Linecast(source.transform.position, target.transform.position, obstructionMask))
            {
#if DEBUG
                Interface.Oxide.LogInfo($"CanSee(): Blocked by some obstruction.");
#endif
                return false;
            }
            if(Vector3.Distance(source.transform.position, target.transform.position) <  npc.info.damageDistance)
            {
#if DEBUG
                Interface.Oxide.LogInfo($"CanSee(): In range!");
#endif
                //if(!IsLayerBlocked(target.transform.position, npc.info.attackDistance, obstructionMask))
                if(!IsLayerBlocked(target.transform.position, 10f, obstructionMask))
                {
//                    npc.Evade();
                }

                npc.LookTowards(target.transform.position);
//                var animal = target as BaseAnimalNPC;
//                if(animal)
//                {
//                    npc.LookTowards(target.transform.position - new Vector3(0, 0.5f, 0));
//                }
//                else
//                {
//                    npc.LookTowards(target.transform.position);
//                }
                return true;
            }
            List<BasePlayer> nearPlayers = new List<BasePlayer>();
            Vis.Entities<BasePlayer>(pos, npc.info.maxDistance, nearPlayers, playerMask);
            foreach(var player in nearPlayers)
            {
                if(player == target)
                {
#if DEBUG
                    Interface.Oxide.LogInfo($"CanSee(): I can see them!");
#endif
                    //if(!IsLayerBlocked(target.transform.position, npc.info.attackDistance, obstructionMask))
                    if(!IsLayerBlocked(target.transform.position, 10f, obstructionMask))
                    {
//                        npc.Evade();
                    }

                    npc.LookTowards(target.transform.position);
                    return true;
                }
            }
#if DEBUG
            Interface.Oxide.LogInfo($"CanSee(): NOPE");
#endif
            return false;
        }

        private static string GetRandomMessage(List<string> messagelist) => messagelist[GetRandom(0, messagelist.Count)];
        private static int GetRandom(int min, int max) => UnityEngine.Random.Range(min, max);

        private List<string> ListFromArgs(string[] args, int from)
        {
            var newlist = new List<string>();
            for(var i = from; i < args.Length; i++)
            {
                newlist.Add(args[i]);
            }
            return newlist;
        }

        // Nikedemos
        private string ArgConcat(string[] args, bool withoutFirstElement)
        {
            string built = "";
            if (withoutFirstElement == true)
            {
                args = args.Skip(1).ToArray();
            }

            //this will ensure any argument after "add" goes in the team name separated by spaces
            for (var t = 0; t < args.Length; t++) //skipping the first argument "add"
            {
                if (!args[t].Contains(" ")) //so we don't accidentally put in double spaces or whatever
                {
                    if (t > 0) //no space before the first one
                        built = String.Concat(built, " ");

                    built = String.Concat(built, args[t]);
                }
            }

            return built;
        }
        // Nikedemos

        //////////////////////////////////////////////////////////////////////////////
        /// Chat Commands
        //////////////////////////////////////////////////////////////////////////////
        [ChatCommand("npc_add")]
        private void cmdChatNPCAdd(BasePlayer player, string command, string[] args)
        {
            if(!hasAccess(player)) return;
            if(player.GetComponent<NPCEditor>() != null)
            {
                SendReply(player, "NPC Editor: Already editing an NPC, say /npc_end first");
                return;
            }
            Quaternion currentRot;
            if(!TryGetPlayerView(player, out currentRot))
            {
                SendReply(player, "Couldn't get player rotation");
                return;
            }

            HumanPlayer humanPlayer;
            if(args.Length > 0)
            {
                ulong targetId;
                if(!ulong.TryParse(args[0], out targetId))
                {
                    SendReply(player, "/npc_add [TARGETID]");
                    return;
                }
                HumanNPCInfo tempInfo;
                if(!humannpcs.TryGetValue(targetId, out tempInfo))
                {
                    SendReply(player, "Couldn't find the NPC");
                    return;
                }
                humanPlayer = CreateNPC(player.transform.position, currentRot, "NPC", targetId);
            }
            else
            {
                humanPlayer = CreateNPC(player.transform.position, currentRot);
            }
            if(humanPlayer == null)
            {
                SendReply(player, "Couldn't spawn the NPC");
                return;
            }
            var npcEditor = player.gameObject.AddComponent<NPCEditor>();
            npcEditor.targetNPC = humanPlayer;
        }

        // Nikedemos
        [ChatCommand("npc_team")]
        private void cmdChatNPCTeam(BasePlayer player, string command, string[] args)
        {
            if (!hasAccess(player)) return;

            if (args.Length == 0)
            {
                //check if there's any teams
                if (humannpcteams.Count() == 0)
                    SendReply(player, "There's currently no teams defined, type /npc_team add [team_name] first!");
                else
                {
                    string plural = (humannpcteams.Count() == 1) ? "" : "s";

                    SendReply(player, $"There's currently {humannpcteams.Count()} team{plural} defined:");
                    foreach (var theteam in humannpcteams)
                    {
                        string plural2 = (theteam.Value.MemberCount() == 1) ? "" : "s";
                        SendReply(player, $"{theteam.Value.teamid}: {theteam.Value.teamName} ({theteam.Value.MemberCount()} member{plural2})");
                    }
                }
            }
            else
            {
                switch (args[0])
                {
                    case "add":
                        {
                            if (args.Length > 1)
                            {
                                string proposedName = ArgConcat(args, true);

                                ulong newteamid = AddTeamByName(proposedName);
                                if (newteamid == 0)
                                {
                                    SendReply(player, $"Sorry, a team with that name already exists (ID {FindTeamIDByName(proposedName)})");
                                }
                                else
                                {
                                    SendReply(player, $"Team \"{proposedName}\" added with ID {newteamid}");
                                    GenerateRelationshipCache();
                                }
                            }
                            else
                            {
                                SendReply(player, "Please provide a name for the team! Try /npc_team add [team_name]");
                            }
                        }
                        break;
                    case "remove":
                        {
                            if (args.Length > 1)
                            {
                                //first, check if it's a number. If not, then it must be a team name.
                                ulong requestedId = TryGetTeamIDFromString(args[1]);
                                string requestedName = "";

                                if (requestedId != 0) //ID found (>0) based on provided argument(s)
                                {
                                    requestedName = FindTeamNameByID(requestedId);

                                    //remove that team
                                    storedData.HumanNPCTeams.Remove(humannpcteams[requestedId]);
                                    humannpcteams.Remove(requestedId);

                                    save = true;

                                    SendReply(player, $"Team \"{requestedName}\" (ID {requestedId}) has been removed.");
                                    GenerateRelationshipCache();
                                }
                                else
                                {
                                    SendReply(player, "Wrong team ID or name. Type /npc_team to see a rundown of all teams.");

                                }
                            }
                            else
                            {
                                SendReply(player, "Please provide a team name or ID. Type /npc_team to see a rundown of all teams.");
                            }
                        }
                        break;
                    case "fof": //fof stands for Friend or Foe, this is where team alignments are set/gotten
                        {
                            //usage: /npc_team fof [team_id1] [foe/neutral/friend] [team_id2]
                            //when [foe/neutral/friend] are skipped, it just shows friendly/foe teams for that particular team id.

                            if (args.Length > 1)
                            {
                                ulong requestedId = TryGetTeamIDFromString(args[1]);
                                string requestedName = "";

                                if (requestedId != 0) //ID found (>0) based on provided argument(s)
                                {
                                    requestedName = FindTeamNameByID(requestedId);

                                    if (args.Length > 2)
                                    {
                                        string alignArg = args[2].ToLower();
                                        if (alignArg.Equals("friend") || alignArg.Equals("neutral") || alignArg.Equals("foe"))
                                        {
                                            if (args.Length > 3)
                                            {
                                                ulong requestedOtherId = TryGetTeamIDFromString(args[3]);

                                                if (requestedOtherId != 0)//if that team exists)
                                                {

                                                    //this will also check if you're trying to set a team's alignment to itself. will return 0 on fail
                                                    if (humannpcteams[requestedId].TeamAlignmentSet(requestedOtherId, FoFStringToEnum[alignArg]))
                                                    {
                                                        string requestedOtherName = FindTeamNameByID(requestedOtherId); //we know it exists, it will never be null in this context
                                                        string maybePreposition = alignArg.Equals("neutral") ? "" : "a ";
                                                        save = true;

                                                        //something about relationships between teams has changed, so regenerate the relationship cache
                                                        SendReply(player, $"Team \"{requestedName}\" will now consider the team \"{requestedOtherName}\" {maybePreposition}{alignArg}");

                                                        GenerateRelationshipCache();
                                                    }
                                                    else
                                                    {
                                                        SendReply(player, $"You cannot set a team's alignment to itself. Try with two different teams.");
                                                    }
                                                }
                                                else
                                                {
                                                    SendReply(player, "Wrong other team ID or name. Type /npc_team to see a rundown of all teams.");
                                                }
                                            }
                                            else
                                            {
                                                SendReply(player, $"Please provide the name or ID of the other team. Try /npc_team fof {args[1]} {args[2]} [team name or ID]");
                                            }
                                        }
                                        else
                                        {
                                            SendReply(player, $"Wrong team alignment. Try /npc_team fof {args[1]} [foe/neutral/friend] [team name or ID]");
                                        }
                                    }
                                    else
                                    {
                                        //display all friends and enemies from the point of view of team 1. Neutrals are naturally skipped.
                                        if (humannpcteams[requestedId].TeamAlignmentCount() > 0)
                                        {
                                            SendReply(player, $"Team \"{requestedName}\" has the following alignments with other teams: (considers all other teams neutral)");
                                            foreach (var alignment in humannpcteams[requestedId].teamAlignments)
                                            {
                                                //There might exist an entry for a team alignment after the team has been removed - or when the user has typed rubbish into the JSON.
                                                //In that case, keep the entry, don't delete it,
                                                //maybe the user's testing something?
                                                //For this reason, try to find out if the team exists first (if so, you will receive a valid name)
                                                //Otherwise display ???
                                                ulong maybeOtherTeamID = alignment.Key;
                                                string maybeOtherTeamName = FindTeamNameByID(maybeOtherTeamID);

                                                if (maybeOtherTeamName == null)
                                                {
                                                    maybeOtherTeamName = "???";
                                                }
                                                SendReply(player, $"{maybeOtherTeamName} (ID {maybeOtherTeamID.ToString()}) is considered a {FoFEnumToString[alignment.Value]}");
                                                //don't worry about "(...) is considered a neutral", neutrals are not displayed. Take that, Grammarjugend!
                                            }
                                        }
                                        else
                                        {
                                            SendReply(player, $"Team \"{requestedName}\" has no alignments with other teams (considers all teams neutral).");
                                        }
                                    }

                                }
                                else
                                {
                                    SendReply(player, "Wrong team ID or name. Type /npc_team to see a rundown of all teams.");
                                }
                            }
                            else
                            {
                                SendReply(player, "Usage: /npc_team fof [team 1 name or ID] [foe/neutral/friend] [team 2 name or ID]");
                            }
                        }
                        break;
                    case "uniform":
                        {
                            //only if Kits are loaded!

                        }
                        break;
                    case "rename":
                        {
                            //because of the ambiguity (team names can contain spaces, so we don't know where one name begins, and the other ends),
                            //we're only going to take args[1] into account for the first argument, not a concated version of args after "rename".
                            //the consequence is that if you want to rename a team that already contains spaces in its name,
                            //you will only be able to address it for renaming by its ID

                            if (args.Length > 1)
                            {
                                //first argument goes into args1, the remaining into args2
                                //even when there's only 1 element it still has to be in the array
                                //because of the argument that TryGetTeamIDFromArgs takes

                                //oh and here we can ignore args[0] which is just "rename"
                                string[] args1 = new[] { args[1] };
                                string[] args2 = args.Skip(2).ToArray();

                                ulong requestedId = TryGetTeamIDFromString(args[1]);
                                string requestedName = "";

                                if (requestedId != 0)
                                {
                                    //team exists, now check if the new name has been provided
                                    if (args2.Length > 0)
                                    {
                                        requestedName = FindTeamNameByID(requestedId);
                                        string newName = ArgConcat(args2, false);

                                        //rename that team
                                        humannpcteams[requestedId].teamName = newName;
                                        save = true;

                                        SendReply(player, $"Team \"{requestedName}\" (ID {requestedId}) has been renamed to \"{newName}\"");
                                    }
                                    else
                                    {
                                        SendReply(player, "Please provide the new name for the team.");
                                    }
                                }
                                else
                                {
                                    SendReply(player, "Wrong team ID or name. Type /npc_team to see a rundown of all teams.");

                                }
                            }
                            else
                            {
                                SendReply(player, "Please provide a team name or ID and the new name for the team. Type /npc_team to see a rundown of all teams.");
                            }

                        }
                        break;
                    case "member":
                        {
                            if (args.Length > 1)
                            {
                                switch (args[1])
                                {
                                    case "add":
                                        {
                                            if (args.Length > 2)
                                            {
                                                //try to see if it's a correct ulong number, if so, user it! If not, inform the player.
                                                ulong maybeUserId = FindWhateverUserIDFromString(args[2]);
                                                //string maybeUserName = FindWhateverUserNameFromString(args[2]);

                                                if (maybeUserId != 0) //this works for actual players and humans
                                                {
                                                    //let's see if the final argument has been provided...

                                                    if (args.Length > 3)
                                                    {
                                                        //we got an argument. let's skip args[0] (member), args[1] (add), and args[2] (whatever user ID provided) - 3 total.
                                                        //and feed that to the method.

                                                        string[] subArgs = args.Skip(3).ToArray();

                                                        ulong requestedId = TryGetTeamIDFromString(subArgs[0]);

                                                        if (requestedId != 0) //0 means not found
                                                        {
                                                            string requestedName = FindTeamNameByID(requestedId);
                                                            //penultimate check: check if the user ID is already registered as a part of that team

                                                            //now check if the last argument has been provided

                                                            if (!humannpcteams[requestedId].members.ContainsKey(maybeUserId))
                                                            {
                                                                //final check: can we actually add the user? Bool true means it was added succesfully, so a player/NPC with that id must've existed.
                                                                //Otherwise fail
                                                                BasePlayer maybePlayer = humannpcteams[requestedId].MemberAdd(maybeUserId); //BasePlaer of a HumanPlayer has, presumably, a different ID?

                                                                if (maybePlayer != null)
                                                                {
                                                                    SendReply(player, $"{maybePlayer.displayName} (user ID {maybePlayer.UserIDString}) has been added to team ID {requestedId})");
                                                                    save = true;

                                                                    //something about relationships between teams has changed, so regenerate the relationship cache
                                                                    GenerateRelationshipCache();
                                                                }
                                                                else
                                                                {
                                                                    SendReply(player, $"Error for member add {args[2]} {ArgConcat(subArgs, false)}: player or NPC with that ID not found. Try member add {args[2]} [team name or id]");
                                                                }
                                                            }
                                                            else
                                                            {
                                                                SendReply(player, $"This player is already a member of the team {requestedId}");
                                                            }
                                                        }
                                                        else
                                                        {
                                                            SendReply(player, $"Error for member add {args[2]} {ArgConcat(subArgs, false)}: team not found by name nor ID. Try member add {args[2]} [team name or id]");
                                                        }
                                                    }
                                                    else
                                                        SendReply(player, $"Not enough arguments for member add {args[2]}. Try member add {args[2]} [team name or id]");
                                                }
                                                else
                                                    SendReply(player, $"No user found with ID or name \"{args[2]}\"");
                                            }
                                            else
                                            {
                                                SendReply(player, "Not enough arguments for member add. Try member add [user id] [team name or id]");
                                            }
                                        }
                                        break;
                                    case "remove": //structurally identical to "add" case, just 1 less check, I guess
                                        {
                                            if (args.Length > 2)
                                            {
                                                ulong maybeUserId = FindWhateverUserIDFromString(args[2]);

                                                if (maybeUserId != 0) //this works for actual players and humans
                                                {
                                                    if (args.Length > 3)
                                                    {
                                                        string[] subArgs = args.Skip(3).ToArray();

                                                        ulong requestedId = TryGetTeamIDFromString(subArgs[0]);

                                                        if (requestedId != 0)
                                                        {
                                                            string requestedName = FindTeamNameByID(requestedId);
                                                            bool removedSuccessfully = humannpcteams[requestedId].MemberRemove(maybeUserId);

                                                            if (removedSuccessfully)
                                                            {
                                                                SendReply(player, $"User with ID {maybeUserId}) has been removed from the team {requestedName} (ID {requestedId})");
                                                            save = true;

                                                                //something about relationships between teams has changed, so regenerate the relationship cache
                                                                GenerateRelationshipCache();
                                                            }
                                                            else
                                                            {
                                                                SendReply(player, $"Error for member remove {args[2]} {ArgConcat(subArgs, false)}: player or NPC with that ID is not a member of the team. Try member remove {args[2]} [team name or id]");
                                                            }
                                                        }
                                                        else
                                                        {
                                                            SendReply(player, $"Error for member remove {args[2]} {ArgConcat(subArgs, false)}: team not found by name nor ID. Try member remove {args[2]} [team name or id]");
                                                        }
                                                    }
                                                    else
                                                        SendReply(player, $"Not enough arguments for member remove {args[2]}. Try member remove {args[2]} [team name or id]");
                                                }
                                                else
                                                    SendReply(player, $"No user found with ID or name \"{args[2]}\"");
                                            }
                                            else
                                            {
                                                SendReply(player, "Not enough arguments for member remove. Try member remove [user id] [team name or id]");
                                            }
                                        }
                                        break;
                                    case "list":
                                        {
                                            if (args.Length > 2)
                                            {
                                                string[] subArgs = args.Skip(2).ToArray();
                                                ulong maybeTeamId = TryGetTeamIDFromString(subArgs[0]);

                                                if (maybeTeamId != 0)
                                                {
                                                    var memCount = humannpcteams[maybeTeamId].MemberCount();
                                                    string plural = memCount == 1 ? "" : "s";
                                                    SendReply(player, $"Team \"{humannpcteams[maybeTeamId].teamName}\" has {memCount} member{plural}");

                                                    foreach (var member in humannpcteams[maybeTeamId].members)
                                                    {
                                                        //bool offline = false;
                                                        string maybeName = member.Value.TryGetMemberName();
                                                        string symbol = "???";
                                                        string memberPositionStr = "(?,?,?)";
                                                        string distance = "?";

                                                        if (maybeName == null)
                                                        {
                                                            maybeName = "[OFFLINE]";
                                                            //offline = true;
                                                        }
                                                        else
                                                        {
                                                            symbol = member.Value.IsNPC() ? "NPC" : "USR";

                                                            //will never be null here since since the name is not null (so the BasePlayer/HumanPlayer with component BasePlayer exists, that's where we got the name from). If it is, something went wrong somewhere else
                                                            BasePlayer memberPlayer = member.Value.TryGetBasePlayer();

                                                            Vector3 playerPos = player.transform.position;
                                                            Vector3 memberPlayerPos = memberPlayer.transform.position;
                                                            distance = Vector3.Distance(playerPos, memberPlayerPos).ToString("0.##");
                                                            memberPositionStr = $"({memberPlayerPos.x.ToString("0.##")},{memberPlayerPos.y.ToString("0.##")},{memberPlayerPos.z.ToString("0.##")})";

                                                        }
                                                        SendReply(player, $"{symbol} {maybeName} (ID {member.Key}) at {memberPositionStr} ({distance} m)");
                                                    }
                                                }
                                                else
                                                {
                                                    SendReply(player, $"Error for member list {ArgConcat(subArgs, false)}: team not found by name nor ID. If you want a rundown of all teams, type /npc_team");
                                                }
                                            }
                                            else
                                            {
                                                SendReply(player, "Not enough arguments for member list. Try member list [team name or id]. If you want a rundown of all teams, type /npc_team");
                                            }
                                        }
                                        break;
                                    default:
                                        {
                                            SendReply(player, "Wrong argument for member. Try /npc_team member [add/remove] [user id] [team name or id] or /npc_team member list [team name or id]");
                                        }
                                        break;
                                }
                            }
                            else
                            {
                                SendReply(player, $"Usage: /npc_team member [add/remove] [user id] [team name or id]. Your user ID is {player.userID}");
                            }
                        }
                        break;
                    case "empty":
                        {
                            if (args.Length > 1)
                            {
                                ulong requestedId = TryGetTeamIDFromString(args[1]);
                                string requestedName = "";

                                if (requestedId != 0) //ID found (>0) based on provided argument(s)
                                {
                                    requestedName = FindTeamNameByID(requestedId);

                                    //remove all members within that team
                                    humannpcteams[requestedId].members.Clear();

                                    save = true;

                                    SendReply(player, $"Team \"{requestedName}\" (ID {requestedId}) has been emptied of all the members.");
                                    GenerateRelationshipCache();
                                }
                                else
                                {
                                    SendReply(player, "Wrong team ID or name. Type /npc_team to see a rundown of all teams.");

                                }
                            }
                            else
                            {
                                SendReply(player, "Please provide a team name or ID. Type /npc_team to see a rundown of all teams.");
                            }
                        }
                        break;
                    case "purge":
                        {
                            storedData.HumanNPCTeams.Clear();
                            humannpcteams.Clear();

                            save = true;

                            SendReply(player, "All team data has been purged.");
                            GenerateRelationshipCache();
                        }
                        break;
                    default:
                        {
                            //SendReply(player, $"Received these arguments: {ArgConcat(args, false)}");
                            SendReply(player, "Wrong argument. Try: /npc_team add [name], remove [name or id], rename [id] [new name], clear [name or id], member [add/remove] [user id] [team name or id] , empty [name or id], or purge (USE WITH CAUTION)");
                        }
                        break;
                }
            }
        }

        private ulong TryGetTeamIDFromString(string arg)
        {
            ulong requestedId = 0;
            string requestedName = null;

            if (ulong.TryParse(arg, out requestedId)) //we got a valid id, but it still doesn't mean it exists.
            {
                requestedName = FindTeamNameByID(requestedId);
                if (requestedName == null) //no team with ID found
                {
                    requestedId = 0;
                }
            }
            else  //not a 64-bit unsigned number, so use that string as a teamname
            {
                requestedId = FindTeamIDByName(arg);
            }

            return requestedId;
        }

        private ulong FindWhateverUserIDFromString(string arg) //this wrapper will work for both actual and HumanPlayers
        {
            ulong requestedId = 0;

            if (ulong.TryParse(arg, out requestedId)) //is it an ID?
            {
                if (!(requestedId >= 76560000000000000L || requestedId <= 0L)) //NPCs.
                {
                    var requestedPlayer = FindHumanPlayerByID(requestedId);
                    if (requestedPlayer != null) //no HumanPlayer with that ID found
                    {
                        requestedId = requestedPlayer.info.userid;
                    }
                }
                else
                {
                    var requestedPlayer = FindPlayerByID(requestedId);
                    if (requestedPlayer != null)
                    {
                        requestedId = requestedPlayer.userID;
                    }
                }
            }
            else
            {
                //first, try NPC players
                HumanPlayer requestedPlayer = FindHumanPlayer(arg);
                if (requestedPlayer != null)
                {
                    requestedId = requestedPlayer.info.userid;
                } //not found? try actual players
                else
                {
                    BasePlayer requestedPlayer2 = FindPlayer(arg);
                    if (requestedPlayer2 != null)
                    {
                        requestedId = requestedPlayer2.userID;
                    }
                }
            }

            return requestedId;
        }

        private string FindWhateverUserNameFromString(string arg) //this wrapper will work for both actual and HumanPlayers
        {
            string requestedName = "";
            ulong requestedId = 0;

            if (ulong.TryParse(arg, out requestedId)) //is it an ID?
            {
                if (!(requestedId >= 76560000000000000L || requestedId <= 0L)) //NPCs.
                {
                    var requestedPlayer = FindHumanPlayerByID(requestedId);
                    if (requestedPlayer != null) //no HumanPlayer with that ID found
                    {
                        requestedName = requestedPlayer.info.displayName;
                    }
                }
                else
                {
                    var requestedPlayer = FindPlayerByID(requestedId);
                    if (requestedPlayer != null)
                    {
                        requestedName = requestedPlayer.displayName;
                    }
                }
            }
            else
            {
                //first, try NPC players
                HumanPlayer requestedPlayer = FindHumanPlayer(arg);
                if (requestedPlayer != null)
                {
                    requestedName = requestedPlayer.info.displayName;
                } //not found? try actual players
                else
                {
                    BasePlayer requestedPlayer2 = FindPlayer(arg);
                    if (requestedPlayer != null)
                    {
                        requestedName = requestedPlayer2.displayName;
                    }
                }
            }

            return requestedName;
        }

        private ulong TryGetUserIDFromString(string arg)
        {
            ulong requestedId = 0;

            if (ulong.TryParse(arg, out requestedId)) //we got a valid id, but it still doesn't mean it exists.
            {
                var requestedPlayer = FindHumanPlayerByID(requestedId);
                if (FindHumanPlayerByID(requestedId) != null) //no player with that ID found
                {
                    requestedId = requestedPlayer.info.userid;
                }
            }
            else  //not a 64-bit unsigned number, so use that string as a teamname
            {
                var requestedPlayer = FindHumanPlayer(arg);
                if (FindHumanPlayer(arg) != null) //no player with that ID found
                {
                    requestedId = requestedPlayer.info.userid;
                }
            }

            return requestedId;
        }
        // Nikedemos

        [ChatCommand("npc_way")]
        private void cmdChatNPCWay(BasePlayer player, string command, string[] args)
        {
            if(!hasAccess(player)) return;

            HumanPlayer humanPlayer;
            if(args.Length == 0)
            {
                Quaternion currentRot;
                if(!TryGetPlayerView(player, out currentRot)) return;
                object closestEnt;
                Vector3 closestHitpoint;
                if(!TryGetClosestRayPoint(player.transform.position, currentRot, out closestEnt, out closestHitpoint)) return;
                humanPlayer = ((Collider)closestEnt).GetComponentInParent<HumanPlayer>();
                if(humanPlayer == null)
                {
                    SendReply(player, "This is not an NPC");
                    return;
                }
            }
            else if(args.Length > 0)
            {
                humanPlayer = FindHumanPlayer(args[0]);
                if(humanPlayer == null)
                {
                    ulong userid;
                    if(!ulong.TryParse(args[0], out userid))
                    {
                        SendReply(player, "/npc_way TargetId/Name");
                        return;
                    }
                    SpawnNPC(userid, true);
                    humanPlayer = FindHumanPlayerByID(userid);
                }
                if(humanPlayer == null)
                {
                    SendReply(player, "Couldn't Spawn the NPC");
                    return;
                }
            }
            else
            {
                SendReply(player, "You are not looking at an NPC or this userid doesn't exist");
                return;
            }
            if(humanPlayer.locomotion.cachedWaypoints == null)
            {
                SendReply(player, "The NPC has no waypoints");
                return;
            }
            var eyes = new Vector3(0, 1.6f, 0);
            var lastPos = humanPlayer.info.spawnInfo.position + eyes;
            for(var i = 0; i < humanPlayer.locomotion.cachedWaypoints.Count; i++)
            {
                var pos = humanPlayer.locomotion.cachedWaypoints[i].Position + eyes;
                //player.SendConsoleCommand("ddraw.sphere", 30f, Color.black, lastPos, .5f);
                player.SendConsoleCommand("ddraw.line", 30f, i % 2 == 0 ? Color.blue : Color.red, lastPos, pos);
                lastPos = pos;
            }
        }

        [ChatCommand("npc_edit")]
        private void cmdChatNPCEdit(BasePlayer player, string command, string[] args)
        {
            if(!hasAccess(player)) return;
            if(player.GetComponent<NPCEditor>() != null)
            {
                SendReply(player, "NPC Editor: Already editing an NPC, say /npc_end first");
                return;
            }

            HumanPlayer humanPlayer;
            if(args.Length == 0)
            {
                Quaternion currentRot;
                if(!TryGetPlayerView(player, out currentRot)) return;
                object closestEnt;
                Vector3 closestHitpoint;
                if(!TryGetClosestRayPoint(player.transform.position, currentRot, out closestEnt, out closestHitpoint)) return;
                humanPlayer = ((Collider)closestEnt).GetComponentInParent<HumanPlayer>();
                if(humanPlayer == null)
                {
                    SendReply(player, "This is not an NPC");
                    return;
                }
            }
            else if(args.Length > 0)
            {
                humanPlayer = FindHumanPlayer(args[0]);
                if(humanPlayer == null)
                {
                    ulong userid;
                    if(!ulong.TryParse(args[0], out userid))
                    {
                        SendReply(player, "/npc_edit TargetId/Name");
                        return;
                    }
                    SpawnNPC(userid, true);
                    humanPlayer = FindHumanPlayerByID(userid);
                }
                if(humanPlayer == null)
                {
                    SendReply(player, "Couldn't Spawn the NPC");
                    return;
                }
            }
            else
            {
                SendReply(player, "You are not looking at an NPC or this userid doesn't exist");
                return;
            }

            var npceditor = player.gameObject.AddComponent<NPCEditor>();
            npceditor.targetNPC = humanPlayer;
            SendReply(player, $"NPC Editor: Start Editing {npceditor.targetNPC.player.displayName} - {npceditor.targetNPC.player.userID}");
        }

        [ChatCommand("npc_list")]
        private void cmdChatNPCList(BasePlayer player, string command, string[] args)
        {
            if(!hasAccess(player)) return;
            if(humannpcs.Count == 0)
            {
                SendReply(player, "No NPC created yet");
                return;
            }

            string message = "==== NPCs ====\n";
            foreach(var pair in humannpcs)
            {
                message += $"{pair.Key} - {pair.Value.displayName} - {pair.Value.spawnInfo.ShortString()} {(pair.Value.enable ? "" : "- Disabled")}\n";
            }
            SendReply(player, message);
        }

        [ChatCommand("npc")]
        private void cmdChatNPC(BasePlayer player, string command, string[] args)
        {
            if(!hasAccess(player)) return;
            var npcEditor = player.GetComponent<NPCEditor>();
            if(npcEditor == null)
            {
                SendReply(player, "NPC Editor: You need to be editing an NPC, say /npc_add or /npc_edit");
                return;
            }
            if(args.Length == 0)
            {
                SendReply(player, "<color=#81F781>/npc attackdistance</color><color=#F2F5A9> XXX </color>=> <color=#D8D8D8>Distance between him and the target needed for the NPC to ignore the target and go back to spawn</color>");
                SendReply(player, "<color=#81F781>/npc bye</color> reset/<color=#F2F5A9>\"TEXT\" \"TEXT2\" \"TEXT3\" </color>=><color=#D8D8D8> Don't forg4t the \", this is what NPC will say when a player gets away, multiple texts are possible</color>");
                SendReply(player, "<color=#81F781>/npc damageamount</color> <color=#F2F5A9>XXX </color>=> <color=#D8D8D8>Damage done by that NPC when he hits a player</color>");
                SendReply(player, "<color=#81F781>/npc damagedistance</color> <color=#F2F5A9>XXX </color>=> <color=#D8D8D8>Min distance for the NPC to hit a player (3 is default, maybe 20-30 needed for snipers?)</color>");
                SendReply(player, "<color=#81F781>/npc damageinterval</color> <color=#F2F5A9>XXX </color>=> <color=#D8D8D8>Time to wait before attacking again (2 seconds is default)</color>");
                SendReply(player, "<color=#81F781>/npc enable</color> <color=#F2F5A9>true</color>/<color=#F6CECE>false</color><color=#D8D8D8>Enable/Disable the NPC, maybe save it for later?</color>");
                SendReply(player, "<color=#81F781>/npc health</color> <color=#F2F5A9>XXX </color>=> <color=#D8D8D8>To set the Health of the NPC</color>");
                SendReply(player, "<color=#81F781>/npc hello</color> <color=#F6CECE>reset</color>/<color=#F2F5A9>\"TEXT\" \"TEXT2\" \"TEXT3\" </color>=> <color=#D8D8D8>Don't forget the \", this what will be said when the player gets close to the NPC</color>");
                SendReply(player, "<color=#81F781>/npc hostile</color> <color=#F2F5A9>true</color>/<color=#F6CECE>false</color> <color=#F2F5A9>XX </color>=> <color=#D8D8D8>To set it if the NPC is Hostile</color>");
                SendReply(player, "<color=#81F781>/npc ahostile</color> <color=#F2F5A9>true</color>/<color=#F6CECE>false</color> <color=#F2F5A9>XX </color>=> <color=#D8D8D8>To set it if the NPC is Hostile to animals</color>");
                SendReply(player, "<color=#81F781>/npc hurt</color> <color=#F6CECE>reset</color>/<color=#F2F5A9>\"TEXT\" \"TEXT2\" \"TEXT3\"</color> => <color=#D8D8D8>Don't forget the \", set a message to tell the player when he hurts the NPC</color>");
                SendReply(player, "<color=#81F781>/npc invulnerable</color> <color=#F2F5A9>true</color>/<color=#F6CECE>false </color>=> <color=#D8D8D8>To set the NPC invulnerable or not</color>");
                SendReply(player, "<color=#81F781>/npc kill</color> <color=#F6CECE>reset</color>/<color=#F2F5A9>\"TEXT\" \"TEXT2\" \"TEXT3\" </color>=> <color=#D8D8D8>Don't forget the \", set a message to tell the player when he kills the NPC</color>");
                SendReply(player, "<color=#81F781>/npc kit</color> <color=#F6CECE>reset</color>/<color=#F2F5A9>\"KitName\" </color>=> <color=#D8D8D8>To set the kit of this NPC, requires the Kit plugin</color>");
                SendReply(player, "<color=#81F781>/npc lootable</color> <color=#F2F5A9>true</color>/<color=#F6CECE>false</color> <color=#F2F5A9>XX </color>=> <color=#D8D8D8>To set it if the NPC corpse is lootable or not</color>");
                SendReply(player, "<color=#81F781>/npc maxdistance</color> <color=#F2F5A9>XXX </color>=><color=#D8D8D8> Max distance from the spawn point that the NPC can run from (while attacking a player)</color>");
                SendReply(player, "<color=#81F781>/npc name</color> <color=#F2F5A9>\"THE NAME\"</color> =><color=#D8D8D8> To set a name to the NPC</color>");
                SendReply(player, "<color=#81F781>/npc radius</color> <color=#F2F5A9>XXX</color> =><color=#D8D8D8> Radius of which the NPC will detect the player</color>");
                SendReply(player, "<color=#81F781>/npc respawn</color> <color=#F2F5A9>true</color>/<color=#F6CECE>false</color> <color=#F2F5A9>XX </color>=> <color=#D8D8D8>To set it to respawn on death after XX seconds, default is instant respawn</color>");
                SendReply(player, "<color=#81F781>/npc spawn</color> <color=#F2F5A9>\"new\" </color>=> <color=#D8D8D8>To set the new spawn location</color>");
                SendReply(player, "<color=#81F781>/npc speed</color><color=#F2F5A9> XXX </color>=> <color=#D8D8D8>To set the NPC running speed (while chasing a player)</color>");
                SendReply(player, "<color=#81F781>/npc stopandtalk</color> <color=#F2F5A9>true</color>/<color=#F6CECE>false</color> XX <color=#F2F5A9>XX </color>=> <color=#D8D8D8>To choose if the NPC should stop & look at the player that is talking to him</color>");
                SendReply(player, "<color=#81F781>/npc use</color> <color=#F6CECE>reset</color>/<color=#F2F5A9>\"TEXT\" \"TEXT2\" \"TEXT3\"</color> => <color=#D8D8D8>Don't forg4t the \", this what will be said when the player presses USE on the NPC</color>");
                SendReply(player, "<color=#81F781>/npc waypoints</color> <color=#F6CECE>reset</color>/<color=#F2F5A9>\"Waypoint list Name\" </color>=> <color=#D8D8D8>To set waypoints of an NPC, /npc_help for more information</color>");
                // Nikedemos
                SendReply(player, "<color=#81F781>/npc hostiletowardsarmed</color> <color=#F2F5A9>true</color>/<color=#F6CECE>false</color> <color=#F2F5A9>XX </color>=> <color=#D8D8D8>To set it if the NPC is Hostile towards armed</color>");
                SendReply(player, "<color=#81F781>/npc hostiletowardsarmedhard</color> <color=#F2F5A9>true</color>/<color=#F6CECE>false</color> <color=#F2F5A9>XX </color>=> <color=#D8D8D8>When the NPC is hostile towards armed, true means whole inventory is searched, not just the belt</color>");
                SendReply(player, "<color=#81F781>/npc raisealarm</color> <color=#F2F5A9>true</color>/<color=#F6CECE>false</color> <color=#F2F5A9>XX </color>=> <color=#D8D8D8>To set it if the NPC should make other NPCs in its radius attack aswell</color>");

                SendReply(player, "<color=#81F781>/npc armed</color> <color=#F6CECE>reset</color>/<color=#F2F5A9>\"TEXT\" \"TEXT2\" \"TEXT3\" </color>=> <color=#D8D8D8>Dont forgot the \", this is what will be said when the NPC catches the player with weapons</color>");
                SendReply(player, "<color=#81F781>/npc alarm</color> <color=#F6CECE>reset</color>/<color=#F2F5A9>\"TEXT\" \"TEXT2\" \"TEXT3\" </color>=> <color=#D8D8D8>Dont forgot the \", this is what will be said when the NPC raises alarm</color>");
                return;
            }
            var param = args[0].ToLower();
            if(args.Length == 1)
            {
                string message;
                switch(param)
                {
                    // Nikedemos
                    case "hostiletowardsarmed":
                        message = $"This NPC hostility towards armed is set to: {npcEditor.targetNPC.info.hostileTowardsArmed}";
                        break;
                    case "hostiletowardsarmedhard":
                        message = $"Whether this NPC will perform deep search when hostile towards armed is set to: {npcEditor.targetNPC.info.hostileTowardsArmedHard}";
                        break;

                    case "armed":
                        if (npcEditor.targetNPC.info.message_armed == null || (npcEditor.targetNPC.info.message_armed.Count == 0))
                            message = "No armed message set yet";
                        else
                            message = $"This NPC will say when caught with a weapon: {npcEditor.targetNPC.info.message_armed.Count} different messages";
                        break;
                    case "raisealarm":
                        message = $"This NPC will raise alarm: {npcEditor.targetNPC.info.raiseAlarm}";
                        break;
                    // Nikedemos
                    case "name":
                        message = $"This NPC name is: {npcEditor.targetNPC.info.displayName}";
                        break;
                    case "enable":
                    case "enabled":
                        message = $"This NPC enabled: {npcEditor.targetNPC.info.enable}";
                        break;
                    case "invulnerable":
                    case "invulnerability":
                        message = $"This NPC invulnerability is set to: {npcEditor.targetNPC.info.invulnerability}";
                        break;
                    case "lootable":
                        message = $"This NPC lootable is set to: {npcEditor.targetNPC.info.lootable}";
                        break;
                    case "hostile":
                        message = $"This NPC hostility is set to: {npcEditor.targetNPC.info.hostile}";
                        break;
                    case "ahostile":
                        message = $"This NPC animal hostility is set to: {npcEditor.targetNPC.info.ahostile}";
                        break;
                    case "defend":
                        message = $"This NPC defend is set to: {npcEditor.targetNPC.info.defend}";
                        break;
                    case "evade":
                        message = $"This NPC evade is set to: {npcEditor.targetNPC.info.evade}";
                        break;
                    case "evdist":
                        message = $"This NPC evade distance is set to: {npcEditor.targetNPC.info.evade}";
                        break;
                    case "follow":
                        message = $"This NPC follow is set to: {npcEditor.targetNPC.info.follow}";
                        break;
                    case "followtime":
                        message = $"This NPC follow time is set to: {npcEditor.targetNPC.info.followtime}";
                        break;
                    case "allowsit":
                        message = $"This NPC allowsit is set to: {npcEditor.targetNPC.info.allowsit}";
                        break;
                    case "allowride":
                        message = $"This NPC allowride is set to: {npcEditor.targetNPC.info.allowride}";
                        break;
                    case "needsammo":
                        message = $"This NPC needsAmmo is set to: {npcEditor.targetNPC.info.needsAmmo}";
                        break;
                    case "dropweapon":
                        message = $"This NPC dropWeapon is set to: {npcEditor.targetNPC.info.dropWeapon}";
                        break;
                    case "health":
                        message = $"This NPC Initial health is set to: {npcEditor.targetNPC.info.health}";
                        break;
                    case "attackdistance":
                        message = $"This Max Attack Distance is: {npcEditor.targetNPC.info.attackDistance}";
                        break;
                    case "damageamount":
                        message = $"This Damage amount is: {npcEditor.targetNPC.info.damageAmount}";
                        break;
                    case "damageinterval":
                        message = $"This Damage interval is: {npcEditor.targetNPC.info.damageInterval} seconds";
                        break;
                    case "maxdistance":
                        message = $"The Max Distance from spawn is: {npcEditor.targetNPC.info.maxDistance}";
                        break;
                    case "damagedistance":
                        message = $"This Damage distance is: {npcEditor.targetNPC.info.damageDistance}";
                        break;
                    case "radius":
                        message = $"This NPC Collision radius is set to: {npcEditor.targetNPC.info.collisionRadius}";
                        break;
                    case "respawn":
                        message = $"This NPC Respawn is set to: {npcEditor.targetNPC.info.respawn} after {npcEditor.targetNPC.info.respawnSeconds} seconds";
                        break;
                    case "spawn":
                        message = $"This NPC Spawn is set to: {npcEditor.targetNPC.info.spawnInfo.String()}";
                        break;
                    case "speed":
                        message = $"This NPC Chasing speed is: {npcEditor.targetNPC.info.speed}";
                        break;
                    case "stopandtalk":
                        message = $"This NPC stop to talk is set to: {npcEditor.targetNPC.info.stopandtalk} for {npcEditor.targetNPC.info.stopandtalkSeconds} seconds";
                        break;
                    case "waypoints":
                    case "waypoint":
                        message = string.IsNullOrEmpty(npcEditor.targetNPC.info.waypoint) ? "No waypoints set for this NPC yet" : $"This NPC waypoints are: {npcEditor.targetNPC.info.waypoint}";
                        break;
                    case "kit":
                    case "kits":
                        message = string.IsNullOrEmpty(npcEditor.targetNPC.info.spawnkit) ? "No spawn kits set for this NPC yet" : $"This NPC spawn kit is: {npcEditor.targetNPC.info.spawnkit}";
                        break;
                    case "hello":
                        if(npcEditor.targetNPC.info.message_hello == null || (npcEditor.targetNPC.info.message_hello.Count == 0))
                            message = "No hello message set yet";
                        else
                            message = $"This NPC will say hi: {npcEditor.targetNPC.info.message_hello.Count} different messages";
                        break;
                    case "bye":
                        if(npcEditor.targetNPC.info.message_bye == null || npcEditor.targetNPC.info.message_bye.Count == 0)
                            message = "No bye message set yet";
                        else
                            message = $"This NPC will say bye: {npcEditor.targetNPC.info.message_bye.Count} different messages ";
                        break;
                    case "use":
                        if(npcEditor.targetNPC.info.message_use == null || npcEditor.targetNPC.info.message_use.Count == 0)
                            message = "No bye message set yet";
                        else
                            message = $"This NPC will say bye: {npcEditor.targetNPC.info.message_use.Count} different messages";
                        break;
                    case "hurt":
                        if(npcEditor.targetNPC.info.message_hurt == null || npcEditor.targetNPC.info.message_hurt.Count == 0)
                            message = "No hurt message set yet";
                        else
                            message = $"This NPC will say ouch: {npcEditor.targetNPC.info.message_hurt.Count} different messages";
                        break;
                    case "kill":
                        if(npcEditor.targetNPC.info.message_kill == null || npcEditor.targetNPC.info.message_kill.Count == 0)
                            message = "No kill message set yet";
                        else
                            message = $"This NPC will say a death message: {npcEditor.targetNPC.info.message_kill.Count} different messages";
                        break;
                    case "hitchance":
                        message = $"This NPC hit chance is: {npcEditor.targetNPC.info.hitchance}";
                        break;
                    case "reloadduration":
                        message = $"This NPC reload duration is: {npcEditor.targetNPC.info.reloadDuration}";
                        break;
                    case "sit":
                        message = $"Sitting!";
                        npcEditor.targetNPC.info.allowsit = true;
                        npcEditor.targetNPC.locomotion.Sit();
                        break;
                    case "stand":
                        message = $"Standing!";
                        npcEditor.targetNPC.info.allowsit = false;
                        npcEditor.targetNPC.locomotion.Stand();
                        break;
                    case "band":
                        message = $"This NPC's band is band {npcEditor.targetNPC.info.band.ToString()}";
                        break;
                    case "info":
                        message = $" {npcEditor.targetNPC.info.displayName}\n"
                            + $"\tenabled: {npcEditor.targetNPC.info.enable}\n"
                            + $"\tinvulnerability: {npcEditor.targetNPC.info.invulnerability}\n"
                            + $"\tlootable: {npcEditor.targetNPC.info.lootable}\n"
                            + $"\thostility (player): {npcEditor.targetNPC.info.hostile}\n"
                            + $"\thostility (animal): {npcEditor.targetNPC.info.ahostile}\n"
                            + $"\tdefend: {npcEditor.targetNPC.info.defend}\n"
                            + $"\tevade: {npcEditor.targetNPC.info.evade}\n"
                            + $"\tevdist: {npcEditor.targetNPC.info.evdist}\n"
                            + $"\tfollow: {npcEditor.targetNPC.info.follow}\n"
                            + $"\tfollowtime: {npcEditor.targetNPC.info.followtime}\n"
                            + $"\tallowsit: {npcEditor.targetNPC.info.allowsit}\n"
                            + $"\tallowride: {npcEditor.targetNPC.info.allowride}\n"
                            + $"\tsitting: {npcEditor.targetNPC.locomotion.sitting}\n"
                            + $"\tisRiding: {npcEditor.targetNPC.locomotion.isRiding}\n"
                            + $"\tneedsAmmo: {npcEditor.targetNPC.info.needsAmmo}\n"
                            + $"\tdropWeapon: {npcEditor.targetNPC.info.dropWeapon}\n"
                            + $"\tinitial health: {npcEditor.targetNPC.info.health}\n"
                            + $"\tmax attack distance: {npcEditor.targetNPC.info.attackDistance}\n"
                            + $"\tdamage amount: {npcEditor.targetNPC.info.damageAmount}\n"
                            + $"\tdamage interval: {npcEditor.targetNPC.info.damageInterval} seconds\n"
                            + $"\tmax Distance from spawn: {npcEditor.targetNPC.info.maxDistance}\n"
                            + $"\tdamage distance: {npcEditor.targetNPC.info.damageDistance}\n"
                            + $"\tcollision radius: {npcEditor.targetNPC.info.collisionRadius}\n"
                            + $"\trespawn: {npcEditor.targetNPC.info.respawn} after {npcEditor.targetNPC.info.respawnSeconds} seconds\n"
                            + $"\tspawn:\n\t\t{npcEditor.targetNPC.info.spawnInfo.String()}\n"
                            + $"\tposition:\n\t\t{npcEditor.targetNPC.player.transform.position.ToString()}\n"
                            + $"\tchasing speed: {npcEditor.targetNPC.info.speed}\n"
                            + $"\tstop to talk: {npcEditor.targetNPC.info.stopandtalk} for {npcEditor.targetNPC.info.stopandtalkSeconds} seconds\n"
                            + $"\tInstrument: currently set to {npcEditor.targetNPC.info.instrument}\n"
                            + $"\tBand: currently set to {npcEditor.targetNPC.info.band.ToString()}\n"
                            // Nikedemos
                            + $"\thostile towards armed: {npcEditor.targetNPC.info.hostileTowardsArmed}\n"
                            + $"\thostile towards armed hard: {npcEditor.targetNPC.info.hostileTowardsArmedHard}\n"
                            + $"\traise alarm: {npcEditor.targetNPC.info.raiseAlarm}\n";
                        // Nikedemos
                        if (npcEditor.targetNPC.info.message_armed == null || (npcEditor.targetNPC.info.message_armed.Count == 0))
                        {
                            message += "\tNo armed message set yet\n";
                        }
                        else
                        {
                            message += $"\twill say when caught armed: {npcEditor.targetNPC.info.message_armed.Count} different messages\n";
                        }
                        // Nikedemos

                        if(npcEditor.targetNPC.info.waypoint == null)
                        {
                            message += "\tNo waypoints";
                        }
                        else
                        {
                            message += $"\twaypoints: {npcEditor.targetNPC.info.waypoint}\n";
                        }
                        if(npcEditor.targetNPC.info.spawnkit == null)
                        {
                            message += "\tNo kits\n";
                        }
                        else
                        {
                            message += $"\tspawn kit: {npcEditor.targetNPC.info.spawnkit}\n";
                        }
                        if(npcEditor.targetNPC.info.message_hello == null || (npcEditor.targetNPC.info.message_hello.Count == 0))
                        {
                            message += "\tNo hello message set yet\n";
                        }
                        else
                        {
                            message += $"\twill say hi: {npcEditor.targetNPC.info.message_hello.Count} different messages\n";
                        }
                        if(npcEditor.targetNPC.info.message_bye == null || npcEditor.targetNPC.info.message_bye.Count == 0)
                        {
                            message += "\tNo bye message set yet\n";
                        }
                        else
                        {
                            message += $"\twill say bye: {npcEditor.targetNPC.info.message_bye.Count} different messages\n";
                        }
                        if(npcEditor.targetNPC.info.message_use == null || npcEditor.targetNPC.info.message_use.Count == 0)
                        {
                            message += "\tNo bye message set yet\n";
                        }
                        else
                        {
                            message += $"\twill say bye: {npcEditor.targetNPC.info.message_use.Count} different messages\n";
                        }
                        if(npcEditor.targetNPC.info.message_hurt == null || npcEditor.targetNPC.info.message_hurt.Count == 0)
                        {
                            message += "\tNo hurt message set yet\n";
                        }
                        else
                        {
                            message += $"\twill say ouch: {npcEditor.targetNPC.info.message_hurt.Count} different messages\n";
                        }
                        if(npcEditor.targetNPC.info.message_kill == null || npcEditor.targetNPC.info.message_kill.Count == 0)
                        {
                            message += "\tNo kill message set yet\n";
                        }
                        else
                        {
                            message += $"\twill say a death message: {npcEditor.targetNPC.info.message_kill.Count} different messages\n";
                        }
                        message += $"\thit chance: {npcEditor.targetNPC.info.hitchance}\n";
                        message += $"\treload duration: {npcEditor.targetNPC.info.reloadDuration}\n";

                        SendReply(player, $"NPC Info: {message}\n\n");
                        break;
                    default:
                        message = "Wrong Argument.  /npc for more information.";
                        break;
                }
                SendReply(player, message);
                return;
            }
            switch(param)
            {
                // Nikedemos
                case "hostiletowardsarmed":
                    npcEditor.targetNPC.info.hostileTowardsArmed = GetBoolValue(args[1]);
                    break;
                case "band":
                    npcEditor.targetNPC.info.band = Convert.ToSingle(args[1]);
                    break;
                case "hostiletowardsarmedhard":
                    npcEditor.targetNPC.info.hostileTowardsArmedHard = GetBoolValue(args[1]);
                    break;
                case "armed":
                    npcEditor.targetNPC.info.message_armed = args[1] == "reset" ? new List<string>() : ListFromArgs(args, 1);
                    break;
                case "raisealarm":
                    npcEditor.targetNPC.info.raiseAlarm = GetBoolValue(args[1]);
                    break;
                case "alarm":
                    npcEditor.targetNPC.info.message_alarm = args[1] == "reset" ? new List<string>() : ListFromArgs(args, 1);
                    break;
                // Nikedemos
                case "name":
                    npcEditor.targetNPC.info.displayName = args[1];
                    break;
                case "enable":
                case "enabled":
                    npcEditor.targetNPC.info.enable = GetBoolValue(args[1]);
                    break;
                case "invulnerable":
                case "invulnerability":
                    npcEditor.targetNPC.info.invulnerability = GetBoolValue(args[1]);
                    break;
                case "lootable":
                    npcEditor.targetNPC.info.lootable = GetBoolValue(args[1]);
                    break;
                case "hostile":
                    npcEditor.targetNPC.info.hostile = GetBoolValue(args[1]);
                    break;
                case "ahostile":
                    npcEditor.targetNPC.info.ahostile = GetBoolValue(args[1]);
                    break;
                case "defend":
                    npcEditor.targetNPC.info.defend = GetBoolValue(args[1]);
                    break;
                case "evade":
                    npcEditor.targetNPC.info.evade = GetBoolValue(args[1]);
                    break;
                case "evdist":
                    npcEditor.targetNPC.info.evdist = Convert.ToSingle(args[1]);
                    break;
                case "follow":
                    npcEditor.targetNPC.info.follow = GetBoolValue(args[1]);
                    break;
                case "followtime":
                    npcEditor.targetNPC.info.followtime = Convert.ToSingle(args[1]);
                    break;
                case "allowsit":
                    npcEditor.targetNPC.info.allowsit = GetBoolValue(args[1]);
                    break;
                case "allowride":
                    npcEditor.targetNPC.info.allowride = GetBoolValue(args[1]);
                    break;
                case "needsammo":
                    npcEditor.targetNPC.info.needsAmmo = GetBoolValue(args[1]);
                    break;
                case "dropweapon":
                    npcEditor.targetNPC.info.dropWeapon = GetBoolValue(args[1]);
                    break;
                case "health":
                    npcEditor.targetNPC.info.health = Convert.ToSingle(args[1]);
                    break;
                case "attackdistance":
                    npcEditor.targetNPC.info.attackDistance = Convert.ToSingle(args[1]);
                    break;
                case "damageamount":
                    npcEditor.targetNPC.info.damageAmount = Convert.ToSingle(args[1]);
                    break;
                case "damageinterval":
                    npcEditor.targetNPC.info.damageInterval = Convert.ToSingle(args[1]);
                    break;
                case "maxdistance":
                    npcEditor.targetNPC.info.maxDistance = Convert.ToSingle(args[1]);
                    break;
                case "damagedistance":
                    npcEditor.targetNPC.info.damageDistance = Convert.ToSingle(args[1]);
                    break;
                case "radius":
                    npcEditor.targetNPC.info.collisionRadius = Convert.ToSingle(args[1]);
                    break;
                case "respawn":
                    npcEditor.targetNPC.info.respawn = GetBoolValue(args[1]);
                    npcEditor.targetNPC.info.respawnSeconds = 60;
                    if(args.Length > 2)
                    {
                        npcEditor.targetNPC.info.respawnSeconds = Convert.ToSingle(args[2]);
                    }
                    break;
                case "spawn":
                    Quaternion currentRot;
                    TryGetPlayerView(player, out currentRot);
                    var newSpawn = new SpawnInfo(player.transform.position, currentRot);
                    npcEditor.targetNPC.info.spawnInfo = newSpawn;
                    SendReply(player, $"This NPC Spawn now is set to: {newSpawn.String()}");
                    break;
                case "speed":
                    npcEditor.targetNPC.info.speed = Convert.ToSingle(args[1]);
                    break;
                case "stopandtalk":
                    npcEditor.targetNPC.info.stopandtalk = GetBoolValue(args[1]);
                    npcEditor.targetNPC.info.stopandtalkSeconds = 3;
                    if(args.Length > 2)
                    {
                        npcEditor.targetNPC.info.stopandtalkSeconds = Convert.ToSingle(args[2]);
                    }
                    break;
                case "waypoints":
                case "waypoint":
                    var name = args[1].ToLower();
                    if(name == "reset")
                    {
                        npcEditor.targetNPC.info.waypoint = null;
                    }
                    else if(Interface.Oxide.CallHook("GetWaypointsList", name) == null)
                    {
                        SendReply(player, "This waypoint doesn't exist");
                        return;
                    }
                    else npcEditor.targetNPC.info.waypoint = name;
                    break;
                case "kit":
                case "kits":
                    npcEditor.targetNPC.info.spawnkit = args[1].ToLower();
                    break;
                case "hello":
                    npcEditor.targetNPC.info.message_hello = args[1] == "reset" ? new List<string>() : ListFromArgs(args, 1);
                    break;
                case "bye":
                    npcEditor.targetNPC.info.message_bye = args[1] == "reset" ? new List<string>() : ListFromArgs(args, 1);
                    break;
                case "use":
                    npcEditor.targetNPC.info.message_use = args[1] == "reset" ? new List<string>() : ListFromArgs(args, 1);
                    break;
                case "hurt":
                    npcEditor.targetNPC.info.message_hurt = args[1] == "reset" ? new List<string>() : ListFromArgs(args, 1);
                    break;
                case "kill":
                    npcEditor.targetNPC.info.message_kill = args[1] == "reset" ? new List<string>() : ListFromArgs(args, 1);
                    break;
                case "hitchance":
                    npcEditor.targetNPC.info.hitchance = Convert.ToSingle(args[1]);
                    break;
                case "reloadduration":
                    npcEditor.targetNPC.info.reloadDuration = Convert.ToSingle(args[1]);
                    break;
                default:
                    SendReply(player, "Wrong Argument, /npc for more information");
                    return;
            }
            SendReply(player, $"NPC Editor: Set {args[0]} to {args[1]}");
            save = true;
            RefreshNPC(npcEditor.targetNPC.player, true);
        }

        [ChatCommand("npc_end")]
        private void cmdChatNPCEnd(BasePlayer player, string command, string[] args)
        {
            if(!hasAccess(player)) return;
            var npcEditor = player.GetComponent<NPCEditor>();
            if(npcEditor == null)
            {
                SendReply(player, "NPC Editor: You are not editing any NPC");
                return;
            }
            if(!npcEditor.targetNPC.info.enable)
            {
                npcEditor.targetNPC.player.KillMessage();
                SendReply(player, "NPC Editor: The NPC you edited is disabled, killing him");
            }
            UnityEngine.Object.Destroy(npcEditor);
            SendReply(player, "NPC Editor: Ended");
            SaveData();
        }

        [ChatCommand("npc_pathtest")]
        private void cmdChatNPCPathTest(BasePlayer player, string command, string[] args)
        {
            if(!hasAccess(player)) return;
            var npcEditor = player.GetComponent<NPCEditor>();
            if(npcEditor == null)
            {
                SendReply(player, "NPC Editor: You are not editing any NPC");
                return;
            }
            Quaternion currentRot;
            if(!TryGetPlayerView(player, out currentRot)) return;
            object closestEnt;
            Vector3 closestHitpoint;
            if(!TryGetClosestRayPoint(player.transform.position, currentRot, out closestEnt, out closestHitpoint)) return;
            Interface.Oxide.CallHook("FindAndFollowPath", npcEditor.targetNPC.player, npcEditor.targetNPC.player.transform.position, closestHitpoint);
        }

//        [ChatCommand("npc_follow")]
//        private void cmdChatNPCFollow(BasePlayer player, string command, string[] args)
//        {
//            if(!hasAccess(player)) return;
//
//            HumanPlayer humanPlayer;
//            BaseEntity pe = player as BaseEntity;
//            if(args.Length == 0)
//            {
//                Quaternion currentRot;
//                if(!TryGetPlayerView(player, out currentRot)) return;
//                object closestEnt;
//                Vector3 closestHitpoint;
//                if(!TryGetClosestRayPoint(player.transform.position, currentRot, out closestEnt, out closestHitpoint)) return;
//                humanPlayer = ((Collider)closestEnt).GetComponentInParent<HumanPlayer>();
//                if(humanPlayer == null)
//                {
//                    SendReply(player, "This is not an NPC");
//                    return;
//                }
//            }
//            else
//            {
//                SendReply(player, "You are not looking at an NPC or this userid doesn't exist");
//                return;
//            }
//
//            var targetid = humanPlayer.player.userID;
//            humanPlayer.AllowMove();
//            //humanPlayer.StartFollowingEntity(pe, player.displayName);
//            humanPlayer.locomotion.targetPosition = player.transform.position;
//            humanPlayer.locomotion.followEntity = player;
//            humanPlayer.locomotion.TryToMove();
//            SendReply(player, $"NPC {targetid} following");
//        }

        [ChatCommand("npc_remove")]
        private void cmdChatNPCRemove(BasePlayer player, string command, string[] args)
        {
            if(!hasAccess(player)) return;

            HumanPlayer humanPlayer;
            if(args.Length == 0)
            {
                Quaternion currentRot;
                if(!TryGetPlayerView(player, out currentRot)) return;
                object closestEnt;
                Vector3 closestHitpoint;
                if(!TryGetClosestRayPoint(player.transform.position, currentRot, out closestEnt, out closestHitpoint)) return;
                humanPlayer = ((Collider)closestEnt).GetComponentInParent<HumanPlayer>();
                if(humanPlayer == null)
                {
                    SendReply(player, "This is not an NPC");
                    return;
                }
            }
            else if(args.Length > 0)
            {
                ulong userid;
                if(!ulong.TryParse(args[0], out userid))
                {
                    SendReply(player, "/npc_remove TARGETID");
                    return;
                }
                humanPlayer = FindHumanPlayerByID(userid);
                if(humanPlayer == null)
                {
                    SendReply(player, "This NPC doesn't exist");
                    return;
                }
            }
            else
            {
                SendReply(player, "You are not looking at an NPC or this userid doesn't exist");
                return;
            }

            var targetid = humanPlayer.player.userID;
            RemoveNPC(targetid);
            SendReply(player, $"NPC {targetid} Removed");
        }

        [ChatCommand("npc_reset")]
        private void cmdChatNPCReset(BasePlayer player, string command, string[] args)
        {
            if(!hasAccess(player)) return;
            if(player.GetComponent<NPCEditor>() != null) UnityEngine.Object.Destroy(player.GetComponent<NPCEditor>());
            cache.Clear();
            humannpcs.Clear();
            storedData.HumanNPCs.Clear();
            save = true;
            SendReply(player, "All NPCs were removed");
            OnServerInitialized();
        }

        private void SendMessage(HumanPlayer npc, BasePlayer target, string message)
        {
            if(Time.realtimeSinceStartup > npc.lastMessage + 0.1f)
            {
                SendReply(target, $"{chat}{message}", npc.player.displayName);
                npc.lastMessage = Time.realtimeSinceStartup;
            }
        }

        //////////////////////////////////////////////////////
        // NPC HOOKS:
        // will call ALL plugins
        //////////////////////////////////////////////////////
        private List<ulong> HumanNPCs()=>humannpcs.Keys.ToList<ulong>();

        private string HumanNPCname(ulong userid)
        {
            HumanPlayer humanPlayer;
            if(cache.TryGetValue(userid, out humanPlayer)) return humanPlayer.info.displayName;
            var allBasePlayer = Resources.FindObjectsOfTypeAll<HumanPlayer>();
            foreach(var humanplayer in allBasePlayer)
            {
                if(humanplayer.player.userID != userid) continue;
                cache[userid] = humanplayer;
                return humanplayer.info.displayName;
            }
            return null;
        }

        private ulong SpawnHumanNPC(Vector3 position, Quaternion currentRot, string name = "NPC", ulong clone = 0)
        {
            // Try to avoid duplicating players via this path.  If they are already in the data file, cool.
            foreach(KeyValuePair<ulong, HumanNPCInfo> pair in humannpcs)
            {
                if(pair.Value.displayName == name && clone == 0) return pair.Key;
            }
            HumanPlayer hp = CreateNPC(position, currentRot, name, clone);
            if(hp != null)
            {
                return hp.info.userid;
            }
            return 0;
        }

        private void RemoveHumanNPC(ulong npcid)
        {
            var npc = FindHumanPlayerByID(npcid);
            npc.locomotion.Stand();
            RemoveNPC(npcid);
        }

        private void GiveHumanNPC(ulong npcid, string itemname, string loc = "belt")
        {
#if DEBUG
            Puts($"Attempting to add '{itemname}' to NPC {npcid.ToString()}, location {loc}");
#endif
            var npc = FindHumanPlayerByID(npcid);
            Item item = ItemManager.CreateByName(itemname, 1, 0);

            switch(loc)
            {
                case "belt":
                    item.MoveToContainer(npc.player.inventory.containerBelt, -1, true);
                    if(item.info.category == ItemCategory.Weapon) npc.EquipFirstWeapon();
                    else if(item.info.category == ItemCategory.Fun) npc.EquipFirstInstrument();
                    break;
                case "wear":
                default:
                    item.MoveToContainer(npc.player.inventory.containerWear, -1, true);
                    break;
            }
            //UpdateInventory(npc);
            npc.player.inventory.ServerUpdate(0f);
        }

        public static Quaternion StringToQuaternion(string sQuaternion)
        {
            //Interface.Oxide.LogInfo($"Converting {sQuaternion} to Quaternion.");
            // Remove the parentheses
            if(sQuaternion.StartsWith("(") && sQuaternion.EndsWith(")"))
            {
                sQuaternion = sQuaternion.Substring(1, sQuaternion.Length - 2);
            }

            // split the items
            string[] sArray = sQuaternion.Split(',');

            // store as a Vector3
            Quaternion result = new Quaternion(
                float.Parse(sArray[0]),
                float.Parse(sArray[1]),
                float.Parse(sArray[2]),
                float.Parse(sArray[3])
            );

            return result;
        }
        private void SetHumanNPCInfo(ulong npcid, string info, string data, string rot = null)
        {
            var humanPlayer = FindHumanPlayerByID(npcid);
            if(humanPlayer == null) return;
            var npcEditor = humanPlayer.gameObject.AddComponent<NPCEditor>();
            npcEditor.targetNPC = humanPlayer;

            switch(info)
            {
                case "kit":
                case "spawnkit":
                    npcEditor.targetNPC.info.spawnkit = data;
                    break;
                case "hostiletowardsarmed":
                case "hostileTowardsArmed":
                    npcEditor.targetNPC.info.hostileTowardsArmed = GetBoolValue(data);
                    break;
                case "band":
                    npcEditor.targetNPC.info.band = Convert.ToSingle(data);
                    break;
                case "hostiletowardsarmedhard":
                case "hostileTowardsArmedHard":
                    npcEditor.targetNPC.info.hostileTowardsArmedHard = GetBoolValue(data);
                    break;
                case "raisealarm":
                case "raiseAlarm":
                    npcEditor.targetNPC.info.raiseAlarm = GetBoolValue(data);
                    break;
                case "name":
                case "displayName":
                    npcEditor.targetNPC.info.displayName = data;
                    break;
                case "enable":
                case "enabled":
                    npcEditor.targetNPC.info.enable = GetBoolValue(data);
                    break;
                case "persistent":
                    npcEditor.targetNPC.info.persistent = GetBoolValue(data);
                    break;
                case "invulnerable":
                case "invulnerability":
                    npcEditor.targetNPC.info.invulnerability = GetBoolValue(data);
                    break;
                case "lootable":
                    npcEditor.targetNPC.info.lootable = GetBoolValue(data);
                    break;
                case "hostile":
                    npcEditor.targetNPC.info.hostile = GetBoolValue(data);
                    break;
                case "ahostile":
                    npcEditor.targetNPC.info.ahostile = GetBoolValue(data);
                    break;
                case "defend":
                    npcEditor.targetNPC.info.defend = GetBoolValue(data);
                    break;
                case "evade":
                    npcEditor.targetNPC.info.evade = GetBoolValue(data);
                    break;
                case "evdist":
                    npcEditor.targetNPC.info.evdist = Convert.ToSingle(data);
                    break;
                case "follow":
                    npcEditor.targetNPC.info.follow = GetBoolValue(data);
                    break;
                case "followtime":
                    npcEditor.targetNPC.info.followtime = Convert.ToSingle(data);
                    break;
                case "allowsit":
                    npcEditor.targetNPC.info.allowsit = GetBoolValue(data);
                    break;
                case "allowride":
                    npcEditor.targetNPC.info.allowride = GetBoolValue(data);
                    break;
                case "needsammo":
                case "needsAmmo":
                    npcEditor.targetNPC.info.needsAmmo = GetBoolValue(data);
                    break;
                case "dropweapon":
                case "dropWeapon":
                    npcEditor.targetNPC.info.dropWeapon = GetBoolValue(data);
                    break;
                case "health":
                    npcEditor.targetNPC.info.health = Convert.ToSingle(data);
                    break;
                case "attackdistance":
                    npcEditor.targetNPC.info.attackDistance = Convert.ToSingle(data);
                    break;
                case "damageamount":
                    npcEditor.targetNPC.info.damageAmount = Convert.ToSingle(data);
                    break;
                case "damageinterval":
                    npcEditor.targetNPC.info.damageInterval = Convert.ToSingle(data);
                    break;
                case "maxdistance":
                    npcEditor.targetNPC.info.maxDistance = Convert.ToSingle(data);
                    break;
                case "damagedistance":
                    npcEditor.targetNPC.info.damageDistance = Convert.ToSingle(data);
                    break;
                case "radius":
                    npcEditor.targetNPC.info.collisionRadius = Convert.ToSingle(data);
                    break;
                case "speed":
                    npcEditor.targetNPC.info.speed = Convert.ToSingle(data);
                    break;
                case "spawn":
                    Quaternion currentRot = StringToQuaternion(rot);
                    data = data.Replace("(","").Replace(")","");
                    string[] xyz = data.Split(',');
                    //Puts($"Attempting to move NPC to {xyz[0]} {xyz[1]} {xyz[2]}");
                    var pv = new Vector3(float.Parse(xyz[0]), float.Parse(xyz[1]), float.Parse(xyz[2]));
                    var newSpawn = new SpawnInfo(pv, currentRot);
                    npcEditor.targetNPC.info.spawnInfo = newSpawn;
                    break;
                case "hello":
                    npcEditor.targetNPC.info.message_hello = data == "reset" ? new List<string>() : new List<string>() { data };
                    break;
                case "bye":
                    npcEditor.targetNPC.info.message_bye = data == "reset" ? new List<string>() : new List<string>() { data };
                    break;
                case "hurt":
                    npcEditor.targetNPC.info.message_hurt = data == "reset" ? new List<string>() : new List<string>() { data };
                    break;
                case "use":
                    npcEditor.targetNPC.info.message_use = data == "reset" ? new List<string>() : new List<string>() { data };
                    break;
                case "kill":
                    npcEditor.targetNPC.info.message_kill = data == "reset" ? new List<string>() : new List<string>() { data };
                    break;

            }
            save = true;
            RefreshNPC(npcEditor.targetNPC.player, true);
            UnityEngine.Object.Destroy(npcEditor);
            SaveData();
        }

        private string GetHumanNPCInfo(ulong npcid, string info)
        {
            if(!humannpcs.ContainsKey(npcid)) return null;
            var humanPlayer = FindHumanPlayerByID(npcid);

            switch(info)
            {
                case "kit":
                case "spawnkit":
                    return humanPlayer.info.spawnkit;
                case "hostiletowardsarmed":
                case "hostileTowardsArmed":
                    return humanPlayer.info.hostileTowardsArmed.ToString();
                case "hostiletowardsarmedhard":
                case "hostileTowardsArmedHard":
                    return humanPlayer.info.hostileTowardsArmedHard.ToString();
                case "raisealarm":
                case "raiseAlarm":
                    return humanPlayer.info.raiseAlarm.ToString();
                case "name":
                case "displayName":
                    return humanPlayer.info.displayName;
                case "enable":
                case "enabled":
                    return humanPlayer.info.enable.ToString();
                case "invulnerable":
                case "invulnerability":
                    return humanPlayer.info.invulnerability.ToString();
                case "lootable":
                    return humanPlayer.info.lootable.ToString();
                case "hostile":
                    return humanPlayer.info.hostile.ToString();
                case "ahostile":
                    return humanPlayer.info.ahostile.ToString();
                case "defend":
                    return humanPlayer.info.defend.ToString();
                case "evade":
                    return humanPlayer.info.evade.ToString();
                case "evdist":
                    return humanPlayer.info.evade.ToString();
                case "follow":
                    return humanPlayer.info.follow.ToString();
                case "followtime":
                    return humanPlayer.info.followtime.ToString();
                case "allowsit":
                    return humanPlayer.info.allowsit.ToString();
                case "allowride":
                    return humanPlayer.info.allowride.ToString();
                case "needsammo":
                case "needsAmmo":
                    return humanPlayer.info.needsAmmo.ToString();
                case "dropweapon":
                case "dropWeapon":
                    return humanPlayer.info.dropWeapon.ToString();
                case "health":
                    return humanPlayer.info.health.ToString();
                case "attackdistance":
                    return humanPlayer.info.attackDistance.ToString();
                case "damageamount":
                    return humanPlayer.info.damageAmount.ToString();
                case "damageinterval":
                    return humanPlayer.info.damageInterval.ToString();
                case "maxdistance":
                    return humanPlayer.info.maxDistance.ToString();
                case "damagedistance":
                    return humanPlayer.info.damageDistance.ToString();
                case "radius":
                    return humanPlayer.info.collisionRadius.ToString();
                case "respawn":
                    return humanPlayer.info.respawnSeconds.ToString();
                case "spawn":
                case "spawnInfo":
                    return humanPlayer.info.spawnInfo.position.ToString();
                case "speed":
                    return humanPlayer.info.speed.ToString();
                case "stopandtalk":
                    return humanPlayer.info.stopandtalk.ToString();
                case "stopandtalkSeconds":
                    return humanPlayer.info.stopandtalkSeconds.ToString();
                case "hitchance":
                    return humanPlayer.info.hitchance.ToString();
                case "reloadduration":
                    return humanPlayer.info.reloadDuration.ToString();
                case "band":
                    return humanPlayer.info.band.ToString();
                case "hello":
                    if(humanPlayer.info.message_hello == null || (humanPlayer.info.message_hello.Count == 0))
                        return "No hello message set yet";
                    else
                        return $"This NPC will say hi: {humanPlayer.info.message_hello.Count} different messages";
                case "bye":
                    if(humanPlayer.info.message_bye == null || humanPlayer.info.message_bye.Count == 0)
                        return "No bye message set yet";
                    else
                        return $"This NPC will say bye: {humanPlayer.info.message_bye.Count} different messages";
                case "use":
                    if(humanPlayer.info.message_use == null || humanPlayer.info.message_use.Count == 0)
                        return "No bye message set yet";
                    else
                        return $"This NPC will say bye: {humanPlayer.info.message_use.Count} different messages";
                case "hurt":
                    if(humanPlayer.info.message_hurt == null || humanPlayer.info.message_hurt.Count == 0)
                        return "No hurt message set yet";
                    else
                        return $"This NPC will say ouch: {humanPlayer.info.message_hurt.Count} different messages";
                case "kill":
                    if(humanPlayer.info.message_kill == null || humanPlayer.info.message_kill.Count == 0)
                        return "No kill message set yet";
                    else
                        return $"This NPC will say a death message: {humanPlayer.info.message_kill.Count} different messages";
                default:
                    return null;
            }
        }

        private bool IsHumanNPC(BasePlayer player)
        {
#if DEBUG
//            Puts($"IsHumanNPC called for {player.userID}");
#endif
            return player.GetComponent<HumanPlayer>() != null;
        }
        private bool IsHumanNPC(ulong npcid)
        {
#if DEBUG
            Puts($"IsHumanNPC called for {npcid.ToString()}");
#endif
            if(humannpcs.ContainsKey(npcid)) return true;
            return false;
        }

        // Hook to play note based on NPC ID and other values from external sequencing app
        // NPC must have its band value set to a matching band
        private bool npcPlayNote(ulong npcid, int band, int note, int sharp, int octave, float noteval, float duration = 0.2f)
        {
            if(humannpcs.ContainsKey(npcid))
            {
                var npc = FindHumanPlayerByID(npcid);
                if(npc.info.band != band) return false;
                switch(npc.info.instrument)
                {
                    case "drumkit.deployed.static":
                    case "drumkit.deployed":
                    case "xylophone.deployed":
                        if (npc.ktool != null)
                        {
                            npc.ktool.ClientRPC<int, int, int, float>(null, "Client_PlayNote", note, sharp, octave, noteval);
                        }
                        break;
                    case "cowbell.deployed":
                        if (npc.ktool != null)
                        {
                            npc.ktool.ClientRPC<int, int, int, float>(null, "Client_PlayNote", 2, 0, 0, 1);
                        }
                        break;
                    case "piano.deployed.static":
                    case "piano.deployed":
                        if (npc.ktool != null)
                        {
                            npc.ktool.ClientRPC<int, int, int, float>(null, "Client_PlayNote", note, sharp, octave, noteval);
                            timer.Once(duration, () =>
                            {
                                npc.ktool.ClientRPC<int, int, int, float>(null, "Client_StopNote", note, sharp, octave, noteval);
                            });
                        }
                        break;
                    default:
                        if (npc.itool != null)
                        {
                            npc.itool.ClientRPC<int, int, int, float>(null, "Client_PlayNote", note, sharp, octave, noteval);
                            timer.Once(duration, () =>
                            {
                                npc.itool.ClientRPC<int, int, int, float>(null, "Client_StopNote", note, sharp, octave, noteval);
                            });
                        }
                        break;
                }
                return true;
            }
            return false;
        }

        //////////////////////////////////////////////////////
        /// OnHitNPC(BasePlayer npc, HitInfo hinfo)
        /// called when an NPC gets hit
        //////////////////////////////////////////////////////
        /*void OnHitNPC(BasePlayer npc, HitInfo hinfo)
        {
        }*/

        //////////////////////////////////////////////////////
        ///  OnUseNPC(BasePlayer npc, BasePlayer player)
        ///  called when a player press USE while looking at the NPC (5m max)
        //////////////////////////////////////////////////////
        /*void OnUseNPC(BasePlayer npc, BasePlayer player)
        {
        }*/

        /////////////////////////////////////////////////////////////////////////
        ///  OnEnterNPC(BasePlayer npc, BasePlayer player)
        ///  called when a player gets close to an NPC (default is in 10m radius)
        /////////////////////////////////////////////////////////////////////////
        private void OnEnterNPC(BasePlayer npc, BasePlayer player)
        {
            //if(player.userID < 76560000000000000L) return;
            if(player.net?.connection == null) return;
#if DEBUG
            Puts("OnEnterNPC called for player");
#endif
            var humanPlayer = npc.GetComponent<HumanPlayer>();
            if(humanPlayer.info.message_hello != null && humanPlayer.info.message_hello.Count > 0)
            {
                SendMessage(humanPlayer, player, GetRandomMessage(humanPlayer.info.message_hello));
            }
            if(IsHumanNPC(player))
            {
                return;
            }
            if(humanPlayer.info.hostile && player.GetComponent<NPCEditor>() == null && !(bool)(Vanish?.CallHook("IsInvisible", player) ?? false))
            {
                if(humanPlayer.locomotion.sitting)
                {
                    humanPlayer.locomotion.Stand();
                    humanPlayer.locomotion.Evade();
                }
                // Nikedemos
                if (StartAttackingEntityIfSupposedTo(npc, player, true)) //returns true if decided to attack
                {
                    if (humanPlayer.info.raiseAlarm == true)
                        RaiseAlarm(npc, player);
                }
                // Nikedemos
                humanPlayer.StartAttackingEntity(player);
            }
            else if(humanPlayer.info.band > 0 && NPCPlay)
            {
                if((bool) NPCPlay?.Call("CanTriggerOn", Convert.ToInt32(humanPlayer.info.band)))
                {
#if DEBUG
                    Puts("OnEnterNPC: Trying to start band!");
#endif
                    NPCPlay?.Call("BandPlay", Convert.ToInt32(humanPlayer.info.band), true);
                }
            }
        }

        //////////////////////////////////////////////////////////////////////////
        ///  OnEnterNPC(BasePlayer npc, BaseAnimalNPC animal)
        ///  called when an animal gets close to an NPC (default is in 10m radius)
        //////////////////////////////////////////////////////////////////////////
        private void OnEnterNPC(BasePlayer npc, BaseAnimalNPC animal)
        {
#if DEBUG
            Puts("OnEnterNPC called for animal");
#endif
            var humanPlayer = npc.GetComponent<HumanPlayer>();
            if(humanPlayer.info.ahostile)
            {
                if(humanPlayer.locomotion.sitting)
                {
                    humanPlayer.locomotion.Stand();
                    humanPlayer.locomotion.Evade();
                }
                humanPlayer.StartAttackingEntity(animal);
            }
        }

        //////////////////////////////////////////////////
        ///  OnLeaveNPC(BasePlayer npc, BasePlayer player)
        ///  called when a player gets away from an NPC
        //////////////////////////////////////////////////
        private void OnLeaveNPC(BasePlayer npc, BasePlayer player)
        {
            //if(player.userID < 76560000000000000L) return;
            if(player.net?.connection == null) return;
#if DEBUG
            Puts("OnLeaveNPC called for player");
#endif
            var humanPlayer = npc.GetComponent<HumanPlayer>();
            if(humanPlayer.info.message_bye != null && humanPlayer.info.message_bye.Count > 0)
            {
                SendMessage(humanPlayer, player, GetRandomMessage(humanPlayer.info.message_bye));
            }
            if(humanPlayer.info.band > 0 && NPCPlay)
            {
                if((bool) NPCPlay?.Call("CanTriggerOff", Convert.ToInt32(humanPlayer.info.band)))
                {
#if DEBUG
                    Puts("OnEnterNPC: Trying to stop band!");
#endif
                    NPCPlay?.Call("BandStop", Convert.ToInt32(humanPlayer.info.band), true);
                }
            }
        }

        // Nikedemos
        private int RaiseAlarm(BasePlayer caller, BasePlayer target, int limit = 0) //will return the number of HumanPlayers notified who responded
        {
            //you're going to "broadcast" a spherical distress signal around yourself based on your maxDistance
            //(perhaps a separata alarmRadius property for NPCs should be implemented)?

            //every NPC in the radius will "hear" the call, but not every single one will answer.
            //if the respoinder is hostile, only when the caller is a part of the same team (doesn't matter if you're just friendly)
            //If not hostile, only if friendly.
            bool raised = false;
            int counter = 0;

            HumanPlayer humanPlayerComponent = caller.GetComponent<HumanPlayer>();

            if (humanPlayerComponent == null)
            {
                return -1; //actual players don't raise alarms... or should they? we'll see.
            }
            else
            {
                List<BasePlayer> nearPlayers = new List<BasePlayer>();
                Vis.Entities<BasePlayer>(caller.transform.position, humanPlayerComponent.info.maxDistance, nearPlayers, playerMask);
                foreach (var responder in nearPlayers)
                {
                    raised = false;

                    //check if we've reached the limit of called players
                    if (limit > 0 && counter >= limit)
                        break;
                    else
                    {
                        //check if responder is an NPC
                        if (!(responder.userID >= 76560000000000000L || responder.userID <= 0L))
                        {
                            HumanPlayer foundHumanPlayerComponent = responder.GetComponent<HumanPlayer>();

                            //check if responder is already targeting someone, in that case, do nothing
                            if (foundHumanPlayerComponent != null)
                            {
                                if (foundHumanPlayerComponent.locomotion.attackEntity == null)
                                {
                                    //check if responder is hostile. if so, the caller must be in the same team for the response to be answered
                                    if (foundHumanPlayerComponent.info.hostile == true)
                                    {
                                        if (CheckIfUsersShareTeam(responder.userID, caller.userID)) //doesn't matter if you're friends through teams etc.
                                        {
                                            raised = true; //at least 1 NPC has been notified and answered the call
                                        }

                                    }
                                    else
                                    {
                                        //non-hostile NPCs will answer calls of their friends, not just the team
                                        if (GetRelationshipFromCache(responder.userID, caller.userID) == HumanNPCAlignment.Friend)
                                        {
                                            raised = true;
                                        }
                                    }

                                    if (raised == true)
                                    {
                                        foundHumanPlayerComponent.StartAttackingEntity(target);
                                        counter++;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            return counter;
        }

        //This is a wrapper that checks for hostility, team alignments etc.
        private bool StartAttackingEntityIfSupposedTo(BasePlayer npc, BasePlayer player, bool sendChatMessage=false)
        {
            bool doAttack = false;

            var humanPlayer = npc.GetComponent<HumanPlayer>();
            List<string> messageWhich = null;

            if (player.GetComponent<NPCEditor>() == null && !(bool)(Vanish?.CallHook("IsInvisible", player) ?? false))
            {
                if (humanPlayer.info.hostile) //hostiles will not attack their friends, but will attack foes and neutrals on sight
                {
                    //
                    if (GetRelationshipFromCache(humanPlayer.info.userid, player.userID).Equals(HumanNPCAlignment.Friend))
                    {
                        messageWhich = humanPlayer.info.message_hello;
                    }
                    else
                    {
                        //no message for now
                        doAttack = true;
                    }
                }
                else
                {
                    //non-hostiles will only attack their foes on sight, or whatever their hostiletowardsarmed settings are for neutrals

                    messageWhich = humanPlayer.info.message_hello;

                    switch (GetRelationshipFromCache(humanPlayer.info.userid, player.userID))
                    {
                        case HumanNPCAlignment.Foe:
                            {
                                if (humanPlayer.info.raiseAlarm)
                                    messageWhich = humanPlayer.info.message_alarm;
                                else
                                    messageWhich = null;

                                doAttack = true;
                            }
                            break;
                        case HumanNPCAlignment.Neutral:
                            {
                                if (humanPlayer.info.hostileTowardsArmed)
                                {
                                    if (CheckForWeapons(player, humanPlayer.info.hostileTowardsArmedHard))
                                    {
                                        //oops. you have a weapon. armed hello.
                                        //or raising alarm.

                                        //check if you're supposed to raise alarm, then.
                                        if (humanPlayer.info.raiseAlarm)
                                            messageWhich = humanPlayer.info.message_alarm;
                                        else
                                            messageWhich = null;

                                        doAttack = true;
                                    }
                                }
                            }
                            break;
                    }
                }
            }

            if (messageWhich != null && messageWhich.Count > 0)
            {
                SendMessage(humanPlayer, player, GetRandomMessage(messageWhich));
            }

            if (doAttack)
            {
                if (humanPlayer.locomotion.sitting)
                {
                    humanPlayer.locomotion.Stand();
                    humanPlayer.locomotion.Evade();
                }
                humanPlayer.StartAttackingEntity(player);
            }

            return doAttack;
        }

        private bool CheckForWeapons(BasePlayer player, bool searchWholeInventory)
        {
            var weaponFound = false;

            for (int slot = 0; slot < player.inventory.containerBelt.capacity; ++slot)
            {
                Item slotItem = player.inventory.containerBelt.GetSlot(slot);

                if (CheckIfWeapon(slotItem))
                {
                    weaponFound = true;

                    break;
                }
            }

            if (weaponFound == false && searchWholeInventory == true)
            {
                for (int slot = 0; slot < player.inventory.containerMain.capacity; ++slot)
                {
                    Item slotItem2 = player.inventory.containerMain.GetSlot(slot);

                    if (CheckIfWeapon(slotItem2))
                    {
                        weaponFound = true;

                        break;
                    }

                }
            }

            return weaponFound;
        }

        private bool CheckIfWeapon(Item potentialWeapon)
        {
            var isWeapon = false;

            if (potentialWeapon == null)
                isWeapon = false;
            else
            {
                String slotItemCat = potentialWeapon.info.category.ToString();

                if (slotItemCat.Equals("Weapon") || slotItemCat.Equals("Tool") || slotItemCat.Equals("Ammunition") || slotItemCat.Equals("Traps"))
                {
                    isWeapon = true;
                }
            }
            return isWeapon;
        }
        // Nikedemos

        /////////////////////////////////////////////
        ///  OnKillNPC(BasePlayer npc, HitInfo hinfo)
        ///  called when an NPC gets killed
        /////////////////////////////////////////////
        /*void OnKillNPC(BasePlayer npc, HitInfo hinfo)
        {
        }*/

        ///////////////////////////////////////////////
        ///  OnNPCPosition(BasePlayer npc, Vector3 pos)
        ///  Called when an npc reachs a position
        ///////////////////////////////////////////////
        /*void OnNPCPosition(BasePlayer npc, Vector3 pos)
        {
        }*/

        /////////////////////////////////////////////
        ///  OnNPCRespawn(BasePlayer npc)
        ///  Called when an NPC respawns
        ///  here it will give an NPC a kit and set the first tool in the belt as the active weapon
        /////////////////////////////////////////////
        void OnNPCRespawn(BasePlayer npc)
        {
        }

        //////////////////////////////////////////////////////
        ///  OnNPCStartAttacking(BasePlayer npc, BaseEntity target)
        ///  Called when an NPC start to target someone to attack
        ///  return anything will block the attack
        //////////////////////////////////////////////////////
        /*object OnNPCStartTarget(BasePlayer npc, BaseEntity target)
        {
            return null;
        }*/
        //////////////////////////////////////////////////////
        ///  OnNPCStopTarget(BasePlayer npc, BaseEntity target)
        ///  Called when an NPC stops targeting
        ///  no return;
        //////////////////////////////////////////////////////
        void OnNPCStopTarget(BasePlayer npc, BaseEntity target)
        {
#if DEBUG
            Puts("OnNPCStopTarget() called...");
#endif
        }

        //////////////////////////////////////////////////////
        ///  OnNPCStopFollow(BasePlayer npc, BaseEntity target)
        ///  Called when an NPC stops following
        ///  no return;
        //////////////////////////////////////////////////////
        void OnNPCStopFollow(BasePlayer npc, BaseEntity target)
        {
#if DEBUG
            Puts("OnNPCStopFollow() called...");
#endif
        }

        //////////////////////////////////////////////////////
        ///  OnLootNPC(PlayerLoot loot, BaseEntity target, string npcuserID)
        ///  Called when an NPC gets looted
        ///  no return;
        //////////////////////////////////////////////////////
        /*void OnLootNPC(PlayerLoot loot, BaseEntity target, ulong npcuserID)
        {
        }*/

        private class UnityQuaternionConverter : JsonConverter
        {
            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                var quaternion = (Quaternion)value;
                writer.WriteValue($"{quaternion.x} {quaternion.y} {quaternion.z} {quaternion.w}");
            }

            public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
            {
                if(reader.TokenType == JsonToken.String)
                {
                    var values = reader.Value.ToString().Trim().Split(' ');
                    return new Quaternion(Convert.ToSingle(values[0]), Convert.ToSingle(values[1]), Convert.ToSingle(values[2]), Convert.ToSingle(values[3]));
                }
                var o = JObject.Load(reader);
                return new Quaternion(Convert.ToSingle(o["rx"]), Convert.ToSingle(o["ry"]), Convert.ToSingle(o["rz"]), Convert.ToSingle(o["rw"]));
            }

            public override bool CanConvert(Type objectType)
            {
                return objectType == typeof(Quaternion);
            }
        }

        private class UnityVector3Converter : JsonConverter
        {
            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                var vector =(Vector3)value;
                writer.WriteValue($"{vector.x} {vector.y} {vector.z}");
            }

            public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
            {
                if(reader.TokenType == JsonToken.String)
                {
                    var values = reader.Value.ToString().Trim().Split(' ');
                    return new Vector3(Convert.ToSingle(values[0]), Convert.ToSingle(values[1]), Convert.ToSingle(values[2]));
                }
                var o = JObject.Load(reader);
                return new Vector3(Convert.ToSingle(o["x"]), Convert.ToSingle(o["y"]), Convert.ToSingle(o["z"]));
            }

            public override bool CanConvert(Type objectType)
            {
                return objectType == typeof(Vector3);
            }
        }

        private class SpawnInfoConverter : JsonConverter
        {
            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
            }

            public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
            {
                var o = JObject.Load(reader);
                Vector3 position;
                Quaternion rotation;
                if(o["position"] != null)
                {
                    var values = Convert.ToString(o["position"]).Trim().Split(' ');
                    position = new Vector3(Convert.ToSingle(values[0]), Convert.ToSingle(values[1]), Convert.ToSingle(values[2]));
                    values = Convert.ToString(o["rotation"]).Trim().Split(' ');
                    rotation = new Quaternion(Convert.ToSingle(values[0]), Convert.ToSingle(values[1]), Convert.ToSingle(values[2]), Convert.ToSingle(values[3]));
                }
                else
                {
                    position = new Vector3(Convert.ToSingle(o["x"]), Convert.ToSingle(o["y"]), Convert.ToSingle(o["z"]));
                    rotation = new Quaternion(Convert.ToSingle(o["rx"]), Convert.ToSingle(o["ry"]), Convert.ToSingle(o["rz"]), Convert.ToSingle(o["rw"]));
                }
                return new SpawnInfo(position, rotation);
            }

            public override bool CanWrite => false;

            public override bool CanConvert(Type objectType)
            {
                return objectType == typeof(SpawnInfo);
            }
        }
    }
}
