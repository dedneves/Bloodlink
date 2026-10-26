using System;
using System.Collections.Generic;
using System.IO;
using ULTRAKILL.Bloodlink.Combat;
using ULTRAKILL.Bloodlink.Networking;
using ULTRAKILL.Bloodlink.Levels;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace ULTRAKILL.Bloodlink.Enemies
{
    internal sealed class EnemyWireState
    {
        public uint NetworkId;
        public uint StableKey;
        public EnemyType Type;
        public Vector3 Position;
        public Quaternion Rotation;
        public float Health;
        public bool Dead;
        public bool Active = true;
        public float HealthBefore;
        public byte[] ImpactPayload;
        public Vector3 Velocity;
        public int AnimationStateHash;
        public float AnimationNormalizedTime;
        public float AnimationSpeed = 1f;
        public EnemyAnimatorParameter[] AnimationParameters;
        public float ReceivedAt;
        public bool HasKinematics;
        public bool HasAnimation;
        public uint Frame;
    }

    public sealed class EnemySyncRegistry
    {
        private static EnemyIdentifier applyingAuthoritativeHealthTarget;
        internal static bool IsReplayingAuthoritativeDamage => (object)applyingAuthoritativeHealthTarget != null;
        internal static bool IsApplyingAuthoritativeHealthFor(EnemyIdentifier enemy)
        {
            return enemy != null && applyingAuthoritativeHealthTarget == enemy;
        }

        private const int EnemiesPerDatagram = 2;
        // Pose/animation updates run at 20 Hz, while hierarchy discovery is
        // cached separately so more frequent motion does not multiply scene scans.
        private const float HostUpdateInterval = 1f / 20f;
        private readonly Dictionary<uint, uint> appliedAnimationFrameByEnemy = new Dictionary<uint, uint>();
        private readonly Dictionary<EnemyIdentifier, uint> idsByEnemy = new Dictionary<EnemyIdentifier, uint>();
        private readonly Dictionary<uint, EnemyIdentifier> enemiesById = new Dictionary<uint, EnemyIdentifier>();
        private readonly Dictionary<uint, EnemyWireState> latestRemoteStates = new Dictionary<uint, EnemyWireState>();
        private readonly Dictionary<uint, uint> latestFrameByEnemy = new Dictionary<uint, uint>();
        private readonly Dictionary<uint, uint> appliedHealthFrameByEnemy = new Dictionary<uint, uint>();
        private readonly Dictionary<uint, uint> latestDamageFrameByEnemy = new Dictionary<uint, uint>();
        private readonly Dictionary<uint, uint> appliedDamageFrameByEnemy = new Dictionary<uint, uint>();
        private readonly Dictionary<uint, EnemyWireState> latestDamageStates = new Dictionary<uint, EnemyWireState>();
        private readonly Dictionary<uint, float> unresolvedAgeByEnemy = new Dictionary<uint, float>();
        private readonly Dictionary<uint, float> nextReplicaAttempt = new Dictionary<uint, float>();
        private readonly Dictionary<uint, float> absentSinceByEnemy = new Dictionary<uint, float>();
        private readonly Dictionary<uint, uint> stableKeyByEnemyId = new Dictionary<uint, uint>();
        private readonly Dictionary<uint, EnemyType> enemyTypeById = new Dictionary<uint, EnemyType>();
        private readonly HashSet<uint> despawnedNetworkIds = new HashSet<uint>();
        private readonly HashSet<EnemyIdentifier> presentHostEnemies = new HashSet<EnemyIdentifier>();
        private readonly List<EnemyWireState> snapshotStates = new List<EnemyWireState>(64);
        private readonly List<uint> destroyedIdsScratch = new List<uint>(16);
        private readonly List<uint> missingIdsScratch = new List<uint>(16);
        private readonly List<EnemyIdentifier> destroyedReferencesScratch = new List<EnemyIdentifier>(16);
        private readonly List<EnemyIdentifier> trackedKeysScratch = new List<EnemyIdentifier>(16);
        private EnemyIdentifier[] cachedHostEnemies = new EnemyIdentifier[0];
        private EnemyIdentifier[] cachedLocalEnemyCandidates = new EnemyIdentifier[0];
        private float nextLocalEnemyScanTime;
        private uint nextId = 1;
        private uint frame;
        private float hostSnapshotTimer;
        private float nextHostEnemyScanTime;
        private float nextPresenceAt;
        private uint lastPresenceFrame;
        private bool receivedPresence;
        private readonly Dictionary<uint, int> missingPresence = new Dictionary<uint, int>();
        private int receivedStateCount;
        private int resolvedStateCount;
        private int unresolvedStateCount;
        private const float DespawnConfirmationSeconds = 0.75f;

        public bool TryGetNetworkId(EnemyIdentifier enemy, out uint id)
        {
            return idsByEnemy.TryGetValue(enemy, out id);
        }

        public uint EnsureNetworkId(EnemyIdentifier enemy)
        {
            uint id;
            if (!idsByEnemy.TryGetValue(enemy, out id))
            { unchecked { id = nextId++; if (id == 0) id = nextId++; } TrackEnemy(enemy, id); }
            return id;
        }

        public EnemyIdentifier ResolveDamageTarget(uint id, uint key, int type, Vector3 position)
        {
            var mapped = Find(id);
            if (IsLiveDamageTarget(mapped, type) && (key == 0 || EnemyIdentityKey.For(mapped.transform) == key ||
                (mapped.transform.position - position).sqrMagnitude <= 144f)) return mapped;
            var candidates = cachedHostEnemies.Length > 0 ? cachedHostEnemies : UnityEngine.Object.FindObjectsOfType<EnemyIdentifier>();
            var result = ResolveDamageCandidate(candidates, key, type, position);
            if (result != null) return result;
            // A hit can precede periodic discovery immediately after a wave spawns.
            // Refresh once per discovery window, rather than silently losing it.
            if (Time.unscaledTime >= nextHostEnemyScanTime)
            {
                cachedHostEnemies = CaptureHostEnemyCandidates();
                nextHostEnemyScanTime = Time.unscaledTime + 0.2f;
                return ResolveDamageCandidate(cachedHostEnemies, key, type, position);
            }
            return null;
        }

        private static bool IsLiveDamageTarget(EnemyIdentifier enemy, int type) => enemy != null && !enemy.dead &&
            (int)enemy.enemyType == type && enemy.gameObject.scene == SceneManager.GetActiveScene() && enemy.gameObject.activeInHierarchy;

        private static EnemyIdentifier ResolveDamageCandidate(EnemyIdentifier[] candidates, uint key, int type, Vector3 position)
        {
            EnemyIdentifier nearest = null; var best = 144f; var second = float.PositiveInfinity;
            foreach (var candidate in candidates)
            {
                if (!IsLiveDamageTarget(candidate, type)) continue;
                if (key != 0 && EnemyIdentityKey.For(candidate.transform) == key) return candidate;
                var distance = (candidate.transform.position - position).sqrMagnitude;
                if (distance < best) { second = best; best = distance; nearest = candidate; }
                else if (distance < second) second = distance;
            }
            return nearest != null && EnemyMatchPolicy.IsUnambiguousNearest(best, second) ? nearest : null;
        }

        public EnemyIdentifier Find(uint id)
        {
            EnemyIdentifier enemy;
            return enemiesById.TryGetValue(id, out enemy) ? enemy : null;
        }

        public void ResetForLevel()
        {
            idsByEnemy.Clear();
            enemiesById.Clear();
            latestRemoteStates.Clear();
            latestFrameByEnemy.Clear();
            appliedHealthFrameByEnemy.Clear();
            appliedAnimationFrameByEnemy.Clear();
            latestDamageFrameByEnemy.Clear();
            appliedDamageFrameByEnemy.Clear();
            latestDamageStates.Clear();
            unresolvedAgeByEnemy.Clear();
            nextReplicaAttempt.Clear();
            absentSinceByEnemy.Clear();
            stableKeyByEnemyId.Clear();
            enemyTypeById.Clear();
            despawnedNetworkIds.Clear();
            presentHostEnemies.Clear();
            snapshotStates.Clear();
            destroyedIdsScratch.Clear();
            missingIdsScratch.Clear();
            destroyedReferencesScratch.Clear();
            trackedKeysScratch.Clear();
            cachedHostEnemies = new EnemyIdentifier[0];
            cachedLocalEnemyCandidates = new EnemyIdentifier[0];
            nextLocalEnemyScanTime = 0f;
            nextId = 1;
            frame = 0;
            hostSnapshotTimer = 0f;
            nextHostEnemyScanTime = 0f;
            nextPresenceAt = 0f;
            receivedPresence = false;
            missingPresence.Clear();
            receivedStateCount = 0;
            resolvedStateCount = 0;
            unresolvedStateCount = 0;
        }

        public string GetDiagnostics()
        {
            return "enemy sync: received=" + receivedStateCount +
                ", tracked=" + latestRemoteStates.Count +
                ", matched=" + resolvedStateCount +
                ", unresolved=" + unresolvedStateCount +
                ", localCandidates=" + cachedLocalEnemyCandidates.Length + ".";
        }

        public void Update(NetworkService network, float deltaTime)
        {
            if (!network.IsHost) PruneDestroyed();
            if (network.IsHost)
            {
                if (!network.IsConnected || network.Players.Count <= 1) { hostSnapshotTimer = 0f; return; }
                hostSnapshotTimer += Mathf.Max(0f, deltaTime);
                if (hostSnapshotTimer >= HostUpdateInterval)
                {
                    hostSnapshotTimer %= HostUpdateInterval;
                    if (Time.unscaledTime >= nextHostEnemyScanTime)
                    {
                        cachedHostEnemies = CaptureHostEnemyCandidates();
                        nextHostEnemyScanTime = Time.unscaledTime + 0.2f;
                    }
                    BroadcastSnapshots(network, cachedHostEnemies);
                    if (network.SceneSnapshotSyncReady && Time.unscaledTime >= nextPresenceAt)
                    {
                        nextPresenceAt = Time.unscaledTime + 2f;
                        var present = new List<uint>();
                        foreach (var pair in enemiesById)
                            if (pair.Value != null && pair.Value.gameObject.scene == SceneManager.GetActiveScene()) present.Add(pair.Key);
                        if (present.Count <= 4096) network.BroadcastUnreliable(MessageType.EnemyPresence,
                            EnemyPresenceCodec.Encode(SceneManager.GetActiveScene().name, present.ToArray(), frame));
                    }
                }
            }
            else if (network.IsConnected) ApplyRemoteStates(deltaTime);
        }

        private EnemyIdentifier[] CaptureHostEnemyCandidates()
        {
            var scene = SceneManager.GetActiveScene();
            var result = new HashSet<EnemyIdentifier>();
            var active = UnityEngine.Object.FindObjectsOfType<EnemyIdentifier>();
            for (var i = 0; i < active.Length; i++)
            {
                var enemy = active[i];
                if (enemy != null && enemy.gameObject.scene == scene) result.Add(enemy);
            }
            // FindObjectsOfType omits inactive objects. Preserve tracked scene
            // enemies across boss phases, intro cutscenes, and scripted hide/show
            // windows so one inactive frame cannot be mistaken for a despawn.
            foreach (var enemy in idsByEnemy.Keys)
            {
                if (enemy == null || enemy.gameObject.scene != scene || !enemy.gameObject.scene.isLoaded ||
                    enemy.gameObject.activeInHierarchy) continue;
                result.Add(enemy);
            }
            var snapshot = new EnemyIdentifier[result.Count];
            result.CopyTo(snapshot);
            return snapshot;
        }

        public void BroadcastNativeDamage(NetworkService network, uint id, uint stableKey, int enemyType,
            Vector3 position, Quaternion rotation, float health, bool dead, float healthBefore, byte[] impact)
        {
            if (network == null || !network.IsHost || id == 0) return;
            unchecked { frame++; }
            if (frame == 0) frame = 1;
            network.BroadcastReliable(MessageType.EnemyDamageState, EnemyDamageStateCodec.Encode(new EnemyDamageState
            {
                NetworkId = id,
                Frame = frame,
                StableKey = stableKey,
                EnemyType = enemyType,
                Position = position,
                Rotation = rotation,
                Health = health,
                Dead = dead, HealthBefore = healthBefore, ImpactPayload = impact
            }));
        }

        public void SendFullSnapshotToPlayer(NetworkService network, uint playerId)
        {
            if (network == null || !network.IsHost || playerId == 0) return;
            // Reuse the host's recent scene scan when available instead of rescanning on the
            // peer-ready callback. If the join precedes the first network scan, capture once.
            var all = CaptureHostEnemyCandidates();
            var states = new List<EnemyWireState>(all.Length);
            foreach (var enemy in all)
            {
                if (enemy == null) continue;
                uint id;
                if (!idsByEnemy.TryGetValue(enemy, out id))
                {
                    id = nextId++;
                    TrackEnemy(enemy, id);
                }
                enemy.ForceGetHealth();
                if (!ValidVector(enemy.transform.position) || enemy.transform.position.sqrMagnitude > 100000f * 100000f ||
                    !ValidRotation(enemy.transform.rotation) || !Finite(enemy.health)) continue;
                states.Add(new EnemyWireState
                {
                    NetworkId = id,
                    StableKey = EnemyIdentityKey.For(enemy.transform),
                    Type = enemy.enemyType,
                    Position = enemy.transform.position,
                    Rotation = enemy.transform.rotation,
                    Health = enemy.health,
                    Dead = enemy.dead || enemy.health <= 0f,
                    Active = enemy.gameObject.activeInHierarchy,
                    Velocity = CaptureVelocity(enemy),
                    HasKinematics = true,
                    AnimationStateHash = CaptureAnimation(enemy, out var fullSnapshotAnimationTime),
                    AnimationNormalizedTime = fullSnapshotAnimationTime,
                    HasAnimation = true,
                    AnimationParameters = CaptureAnimationParameters(enemy),
                    AnimationSpeed = CaptureAnimationSpeed(enemy),
                    Frame = frame
                });
            }

            for (var start = 0; start < states.Count; start += EnemiesPerDatagram)
            {
                var count = Math.Min(EnemiesPerDatagram, states.Count - start);
                using (var stream = new MemoryStream(8 + count * 66))
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write(frame);
                    writer.Write((byte)count);
                    for (var i = 0; i < count; i++) WriteState(writer, states[start + i]);
                    network.SendToPlayer(playerId, MessageType.EnemySnapshot, stream.ToArray());
                }
            }
        }

        public void ReceiveDamageState(byte[] payload)
        {
            var received = EnemyDamageStateCodec.Decode(payload);
            if (despawnedNetworkIds.Contains(received.NetworkId)) return;
            var enemyType = (EnemyType)received.EnemyType;
            if (!Enum.IsDefined(typeof(EnemyType), enemyType)) throw new InvalidDataException("Invalid enemy type in authoritative damage state.");

            uint previousDamageFrame;
            if (latestDamageFrameByEnemy.TryGetValue(received.NetworkId, out previousDamageFrame) &&
                !IsNewer(received.Frame, previousDamageFrame)) return;

            var state = new EnemyWireState
            {
                NetworkId = received.NetworkId,
                Frame = received.Frame,
                StableKey = received.StableKey,
                Type = enemyType,
                Position = received.Position,
                Rotation = received.Rotation,
                Health = received.Health,
                Dead = received.Dead, HealthBefore = received.HealthBefore, ImpactPayload = received.ImpactPayload
            };
            latestDamageFrameByEnemy[state.NetworkId] = state.Frame;
            latestDamageStates[state.NetworkId] = state;
            EnemyWireState latestSnapshot;
            // Damage commits carry health/impact, not motion or animator state.
            // Replacing a full snapshot here freezes the replica until another
            // motion packet arrives, especially under reliable-channel backlog.
            if (!latestRemoteStates.TryGetValue(state.NetworkId, out latestSnapshot))
            {
                state.ReceivedAt = Time.unscaledTime;
                latestRemoteStates[state.NetworkId] = state;
            }

            var enemy = ResolveLocalEnemy(state, false, GetLocalEnemyCandidates());
            if (enemy == null) return;
            EnsureReplicaAuthority(enemy);
            ApplyAuthoritativeHealth(enemy, state);
            appliedDamageFrameByEnemy[state.NetworkId] = state.Frame;
        }

        private void BroadcastSnapshots(NetworkService network, EnemyIdentifier[] all)
        {
            presentHostEnemies.Clear();
            snapshotStates.Clear();
            foreach (var enemy in all)
            {
                if (enemy == null) continue;
                presentHostEnemies.Add(enemy);
                uint id;
                if (!idsByEnemy.TryGetValue(enemy, out id))
                {
                    id = nextId++;
                    TrackEnemy(enemy, id);
                }
                uint stableKey;
                if (!stableKeyByEnemyId.TryGetValue(id, out stableKey)) stableKey = 0;
                EnemyType trackedType;
                if (!enemyTypeById.TryGetValue(id, out trackedType)) trackedType = enemy.enemyType;
                enemy.ForceGetHealth();
                var position = enemy.transform.position;
                var rotation = enemy.transform.rotation;
                var velocity = CaptureVelocity(enemy);
                var health = Mathf.Clamp(enemy.health, 0f, 100000f);
                var animationHash = CaptureAnimation(enemy, out var animationTime);
                // A Unity enemy can briefly expose NaN values while dying,
                // spawning, or leaving a NavMesh. One invalid record poisons the
                // entire UDP batch, so omit that entity until its next valid tick.
                if (!ValidVector(position) || position.sqrMagnitude > 100000f * 100000f ||
                    !ValidRotation(rotation) || !Finite(health) || health < 0f || health > 100000f)
                    continue;
                if (!ValidVector(velocity)) velocity = Vector3.zero;
                if (!Finite(animationTime) || animationTime < 0f || animationTime >= 1f) animationTime = 0f;
                snapshotStates.Add(new EnemyWireState
                {
                    NetworkId = id,
                    StableKey = stableKey,
                    Type = trackedType,
                    Position = position,
                    Rotation = rotation,
                    Health = health,
                    Dead = enemy.dead || health <= 0f,
                    Active = enemy.gameObject.activeInHierarchy,
                    Velocity = velocity,
                    HasKinematics = true,
                    AnimationStateHash = animationHash,
                    AnimationNormalizedTime = animationTime,
                    HasAnimation = true,
                    AnimationParameters = CaptureAnimationParameters(enemy),
                    AnimationSpeed = CaptureAnimationSpeed(enemy),
                    Frame = frame
                });
            }

            for (var start = 0; start < snapshotStates.Count; start += EnemiesPerDatagram)
            {
                var count = Math.Min(EnemiesPerDatagram, snapshotStates.Count - start);
                using (var stream = new MemoryStream(8 + count * 66))
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write(frame);
                    writer.Write((byte)count);
                    for (var i = 0; i < count; i++) WriteState(writer, snapshotStates[start + i]);
                    network.BroadcastUnreliable(MessageType.EnemySnapshot, stream.ToArray());
                }
            }
            PruneMissingHostEnemies(network, presentHostEnemies);
            frame++;
        }

        public void ReceiveSnapshotBatch(byte[] payload)
        {
            using (var stream = new MemoryStream(payload, false))
            using (var reader = new BinaryReader(stream))
            {
                var batchFrame = reader.ReadUInt32();
                var count = reader.ReadByte();
                if (count > EnemiesPerDatagram) throw new InvalidDataException("Enemy snapshot batch is too large.");
                var batch = new List<EnemyWireState>(count);
                for (var i = 0; i < count; i++)
                {
                    batch.Add(ReadState(reader, batchFrame));
                }
                if (stream.Position != stream.Length) throw new InvalidDataException("Trailing bytes in enemy snapshot batch.");
                foreach (var state in batch)
                {
                    receivedStateCount++;
                    state.ReceivedAt = Time.unscaledTime;
                    if (despawnedNetworkIds.Contains(state.NetworkId)) continue;
                    if (state.StableKey != 0) stableKeyByEnemyId[state.NetworkId] = state.StableKey;
                    enemyTypeById[state.NetworkId] = state.Type;
                    uint previousFrame;
                    if (!latestFrameByEnemy.TryGetValue(state.NetworkId, out previousFrame) || IsNewer(state.Frame, previousFrame))
                    {
                        latestFrameByEnemy[state.NetworkId] = state.Frame;
                        latestRemoteStates[state.NetworkId] = state;
                    }
                }
            }
        }

        public void ReceiveDespawn(byte[] payload)
        {
            var despawn = EnemyDespawnCodec.Decode(payload);
            if (!despawnedNetworkIds.Add(despawn.NetworkId)) return;
            EnemyIdentifier enemy = Find(despawn.NetworkId);
            if (enemy != null && ((int)enemy.enemyType != despawn.EnemyType ||
                EnemyIdentityKey.For(enemy.transform) != despawn.StableKey)) enemy = null;
            if (enemy == null)
            {
                var matches = UnityEngine.Object.FindObjectsOfType<EnemyIdentifier>();
                for (var i = 0; i < matches.Length; i++)
                {
                    var candidate = matches[i];
                    if (candidate == null || (int)candidate.enemyType != despawn.EnemyType ||
                        EnemyIdentityKey.For(candidate.transform) != despawn.StableKey) continue;
                    enemy = candidate;
                    break;
                }
            }
            RemoveTrackedEnemy(despawn.NetworkId);
            if (enemy != null)
            {
                enemy.ForceGetHealth();
                var preserveCorpse = enemy.dead || enemy.health <= 0f;
                idsByEnemy.Remove(enemy);
                // Host despawn ends synchronization ownership; it must not erase a
                // dead replica's game-authored ragdoll, gore, and corpse animations.
                if (!preserveCorpse) UnityEngine.Object.Destroy(enemy.gameObject);
            }
        }

        public void ReceivePresence(byte[] payload)
        {
            var roster = EnemyPresenceCodec.Decode(payload);
            if (!SceneObjectIdentity.IsCurrentScene(roster.Scene)) return;
            if (receivedPresence && !IsNewer(roster.Frame, lastPresenceFrame)) return;
            receivedPresence = true;
            lastPresenceFrame = roster.Frame;
            var present = new HashSet<uint>(roster.Ids);
            foreach (var id in new List<uint>(latestRemoteStates.Keys))
            {
                if (present.Contains(id)) { missingPresence.Remove(id); continue; }
                if (IsNewer(latestRemoteStates[id].Frame, roster.Frame)) continue;
                int missing; missingPresence.TryGetValue(id, out missing);
                missingPresence[id] = ++missing;
                // Two independent rosters avoid treating a spawn arriving between
                // a snapshot and its roster as a nonexistent enemy.
                if (missing < 2) continue;
                var state = latestRemoteStates[id];
                ReceiveDespawn(EnemyDespawnCodec.Encode(id, state.StableKey, (int)state.Type));
                missingPresence.Remove(id);
            }
        }

        public void ApplyJoinSnapshot()
        {
            ApplyRemoteStates(0.1f, false, true);
        }

        private void ApplyRemoteStates(float deltaTime, bool allowNearestFallback = true, bool snapToAuthority = false)
        {
            if (latestRemoteStates.Count == 0) return;
            var candidates = GetLocalEnemyCandidates();
            foreach (var state in latestRemoteStates.Values)
            {
                float unresolvedAge;
                unresolvedAgeByEnemy.TryGetValue(state.NetworkId, out unresolvedAge);
                unresolvedAge += Mathf.Max(0f, deltaTime);
                unresolvedAgeByEnemy[state.NetworkId] = unresolvedAge;
                var enemy = ResolveLocalEnemy(state,
                    allowNearestFallback && EnemyMatchPolicy.ShouldAllowNearestFallback(state.StableKey, unresolvedAge), candidates);
                float nextAttempt;
                var authoredCandidateNearby = false;
                if (enemy == null && unresolvedAge >= 2f)
                    foreach (var candidate in candidates)
                        if (candidate != null && candidate.enemyType == state.Type && !idsByEnemy.ContainsKey(candidate) &&
                            (candidate.transform.position - state.Position).sqrMagnitude <= 30f * 30f) { authoredCandidateNearby = true; break; }
                if (enemy == null && !authoredCandidateNearby && allowNearestFallback && unresolvedAge >= 2f &&
                    (!nextReplicaAttempt.TryGetValue(state.NetworkId, out nextAttempt) || Time.unscaledTime >= nextAttempt))
                {
                    nextReplicaAttempt[state.NetworkId] = Time.unscaledTime + 1f;
                    enemy = EnemyReplicaFactory.Create(state.Type, state.Position, state.Rotation);
                    if (enemy != null) { TrackEnemy(enemy, state.NetworkId); nextReplicaAttempt.Remove(state.NetworkId); }
                }
                if (enemy == null)
                {
                    unresolvedStateCount++;
                    continue;
                }
                resolvedStateCount++;
                unresolvedAgeByEnemy.Remove(state.NetworkId);

                EnsureReplicaAuthority(enemy);
                EnemyWireState damageState;
                var hasDamageState = latestDamageStates.TryGetValue(state.NetworkId, out damageState);
                var damageOverridesSnapshot = hasDamageState && !IsNewer(state.Frame, damageState.Frame);
                var authoritativeDead = state.Dead || (hasDamageState && damageState.Dead);
                if (authoritativeDead)
                {
                    var corpseAuthority = enemy.GetComponent<EnemyReplicaAuthority>();
                    if (corpseAuthority != null) corpseAuthority.ReleaseBodyForDeath();
                    // Let the local game own corpse physics and ragdoll motion. Reapplying
                    // host root transforms every snapshot pins the body upright/frozen.
                    if (!enemy.dead) ApplyAuthoritativeHealth(enemy, hasDamageState && damageState.Dead ? damageState : state);
                    appliedHealthFrameByEnemy[state.NetworkId] = state.Frame;
                    if (hasDamageState) appliedDamageFrameByEnemy[state.NetworkId] = damageState.Frame;
                    if (!state.Active && enemy.gameObject.activeSelf) enemy.gameObject.SetActive(false);
                    continue;
                }
                if (!state.Active)
                {
                    // Scripted boss phases sometimes hide an enemy instead of
                    // destroying it. Mirror that state so the client does not keep
                    // a frozen, apparently living boss in the arena.
                    if (enemy.gameObject.activeSelf) enemy.gameObject.SetActive(false);
                    continue;
                }
                // Enable only after replica authority has disabled local AI.
                if (!enemy.gameObject.activeInHierarchy)
                {
                    enemy.gameObject.SetActive(true);
                    // An active host enemy inside a hidden local room must not
                    // remain invisible because only its own activeSelf was set.
                    for (var parent = enemy.transform.parent; parent != null; parent = parent.parent)
                    {
                        if (!parent.gameObject.activeSelf) parent.gameObject.SetActive(true);
                        if (parent.GetComponent<GoreZone>() != null) break;
                    }
                }
                if (enemy.dead) continue;
                var body = enemy.GetComponent<Rigidbody>();
                var currentPosition = body != null ? body.position : enemy.transform.position;
                var targetPosition = state.Position;
                if (state.HasKinematics)
                    targetPosition += state.Velocity * Mathf.Clamp(Time.unscaledTime - state.ReceivedAt, 0f, 0.1f);
                var correction = snapToAuthority || (targetPosition - currentPosition).sqrMagnitude > 144f
                    ? 1f : 1f - Mathf.Exp(-22f * Mathf.Max(0f, deltaTime));
                var corrected = Vector3.Lerp(currentPosition, targetPosition, correction);
                enemy.transform.SetPositionAndRotation(corrected, Quaternion.Slerp(enemy.transform.rotation, state.Rotation, correction));
                if (body != null) body.position = corrected;

                if (state.HasKinematics)
                {
                    if (body != null && !body.isKinematic) body.velocity = state.Velocity;
                    var agent = enemy.GetComponentInChildren<NavMeshAgent>(true);
                    if (agent != null && agent.enabled && agent.isOnNavMesh) agent.velocity = state.Velocity;
                }
                uint appliedAnimationFrame;
                if (state.HasAnimation && (!appliedAnimationFrameByEnemy.TryGetValue(state.NetworkId, out appliedAnimationFrame) ||
                    IsNewer(state.Frame, appliedAnimationFrame)))
                {
                    ApplyAnimation(enemy, state.AnimationStateHash, state.AnimationNormalizedTime, state.AnimationSpeed, state.AnimationParameters);
                    appliedAnimationFrameByEnemy[state.NetworkId] = state.Frame;
                }

                uint appliedFrame;
                if (damageOverridesSnapshot)
                {
                    uint appliedDamageFrame;
                    if (!appliedDamageFrameByEnemy.TryGetValue(state.NetworkId, out appliedDamageFrame) ||
                        IsNewer(damageState.Frame, appliedDamageFrame))
                    {
                        ApplyAuthoritativeHealth(enemy, damageState);
                        appliedDamageFrameByEnemy[state.NetworkId] = damageState.Frame;
                    }
                }
                else if (!appliedHealthFrameByEnemy.TryGetValue(state.NetworkId, out appliedFrame) || IsNewer(state.Frame, appliedFrame))
                {
                    appliedHealthFrameByEnemy[state.NetworkId] = state.Frame;
                    ApplyAuthoritativeHealth(enemy, state);
                }
            }
        }

        private static void ApplyAuthoritativeHealth(EnemyIdentifier enemy, EnemyWireState state)
        {
            if (state.Dead)
            {
                var authority = enemy.GetComponent<EnemyReplicaAuthority>();
                if (authority != null) authority.ReleaseBodyForDeath();
            }
            if (state.ImpactPayload != null && state.ImpactPayload.Length > 0 && !enemy.dead)
            {
                var hit = DamageRequestCodec.Decode(state.ImpactPayload);
                EnemyHealthSynchronizer.Apply(enemy, state.HealthBefore);
                var previous = applyingAuthoritativeHealthTarget;
                applyingAuthoritativeHealthTarget = enemy;
                try
                {
                    enemy.hitter = hit.Hitter;
                    enemy.hitterAttributes.Clear();
                    foreach (var attribute in hit.HitterAttributes)
                        if (Enum.IsDefined(typeof(HitterAttribute), attribute)) enemy.hitterAttributes.Add((HitterAttribute)attribute);
                    var target = string.IsNullOrEmpty(hit.TargetPath) ? null : enemy.transform.Find(hit.TargetPath);
                    enemy.DeliverDamage(target != null ? target.gameObject : enemy.gameObject, hit.Force, hit.HitPoint,
                        hit.Multiplier, hit.TryForExplode, hit.CritMultiplier, null, hit.IgnoreDamageMultiplier, hit.FromExplosion);
                }
                finally { applyingAuthoritativeHealthTarget = previous; }
                if (enemy == null) return;
            }
            if (state.Dead)
            {
                if (!enemy.dead) enemy.InstaKill();
                return;
            }
            if (enemy.dead) return;
            if (EnemyHealthSynchronizer.Apply(enemy, state.Health)) return;
            enemy.ForceGetHealth();
            var difference = enemy.health - state.Health;
            if (difference > 0.01f)
            {
                var previousTarget = applyingAuthoritativeHealthTarget;
                applyingAuthoritativeHealthTarget = enemy;
                try { enemy.SimpleDamage(difference); }
                finally { applyingAuthoritativeHealthTarget = previousTarget; }
            }
        }

        private static Vector3 CaptureVelocity(EnemyIdentifier enemy)
        {
            var body = enemy.GetComponent<Rigidbody>();
            if (body != null) return ValidVector(body.velocity) ? body.velocity : Vector3.zero;
            var agent = enemy.GetComponentInChildren<NavMeshAgent>(true);
            if (agent == null || !agent.enabled || !agent.isOnNavMesh || !ValidVector(agent.velocity)) return Vector3.zero;
            return agent.velocity;
        }

        private static int CaptureAnimation(EnemyIdentifier enemy, out float normalizedTime)
        {
            normalizedTime = 0f;
            var animator = enemy.GetComponentInChildren<Animator>(true);
            if (animator == null || !animator.isActiveAndEnabled || animator.runtimeAnimatorController == null || animator.layerCount == 0) return 0;
            var state = animator.IsInTransition(0) ? animator.GetNextAnimatorStateInfo(0) : animator.GetCurrentAnimatorStateInfo(0);
            normalizedTime = Finite(state.normalizedTime) ? Mathf.Repeat(state.normalizedTime, 1f) : 0f;
            if (!Finite(normalizedTime) || normalizedTime >= 1f || normalizedTime < 0f) normalizedTime = 0f;
            return state.fullPathHash;
        }

        private static EnemyAnimatorParameter[] CaptureAnimationParameters(EnemyIdentifier enemy)
        {
            var animator = enemy.GetComponentInChildren<Animator>(true);
            if (animator == null || !animator.isActiveAndEnabled || animator.runtimeAnimatorController == null) return null;
            var values = new List<EnemyAnimatorParameter>();
            foreach (var parameter in animator.parameters)
            {
                if (values.Count >= EnemyAnimatorParameterCodec.MaximumParameters) break;
                float value;
                if (parameter.type == AnimatorControllerParameterType.Float) value = animator.GetFloat(parameter.nameHash);
                else if (parameter.type == AnimatorControllerParameterType.Int) value = animator.GetInteger(parameter.nameHash);
                else if (parameter.type == AnimatorControllerParameterType.Bool) value = animator.GetBool(parameter.nameHash) ? 1f : 0f;
                else continue;
                if (!Finite(value) || (parameter.type == AnimatorControllerParameterType.Int && value >= 2147483648f)) continue;
                values.Add(new EnemyAnimatorParameter { Hash = parameter.nameHash, Type = (byte)parameter.type, Value = value });
            }
            return values.ToArray();
        }

        private static float CaptureAnimationSpeed(EnemyIdentifier enemy)
        {
            var animator = enemy.GetComponentInChildren<Animator>(true);
            return animator != null && Finite(animator.speed) ? Mathf.Clamp(animator.speed, 0f, 16f) : 1f;
        }

        private static void ApplyAnimation(EnemyIdentifier enemy, int stateHash, float normalizedTime, float speed, EnemyAnimatorParameter[] parameters)
        {
            if (stateHash == 0) return;
            var animator = enemy.GetComponentInChildren<Animator>(true);
            if (animator == null || !animator.isActiveAndEnabled || animator.runtimeAnimatorController == null || animator.layerCount == 0) return;
            animator.speed = speed;
            if (parameters != null)
                foreach (var parameter in parameters)
                {
                    if (parameter.Type == 1) animator.SetFloat(parameter.Hash, parameter.Value);
                    else if (parameter.Type == 3) animator.SetInteger(parameter.Hash, (int)parameter.Value);
                    else if (parameter.Type == 4) animator.SetBool(parameter.Hash, parameter.Value != 0f);
                }
            var current = animator.GetCurrentAnimatorStateInfo(0);
            var phaseDifference = Mathf.Abs(Mathf.Repeat(current.normalizedTime, 1f) - normalizedTime);
            phaseDifference = Mathf.Min(phaseDifference, 1f - phaseDifference);
            if (current.fullPathHash == stateHash && phaseDifference < 0.2f) return;
            if (!animator.HasState(0, stateHash)) return;
            animator.Play(stateHash, 0, normalizedTime);
        }

        private static void EnsureReplicaAuthority(EnemyIdentifier enemy)
        {
            var authority = enemy.GetComponent<EnemyReplicaAuthority>();
            if (authority == null) authority = enemy.gameObject.AddComponent<EnemyReplicaAuthority>();
            authority.Activate();
        }

        private EnemyIdentifier[] GetLocalEnemyCandidates()
        {
            if (cachedLocalEnemyCandidates.Length == 0 || Time.unscaledTime >= nextLocalEnemyScanTime)
            {
                // Include inactive scene-authored encounter objects. FindObjectsOfType
                // silently omitted those, which made clients unable to bind a host
                // snapshot when the encounter had not locally awakened yet.
                var scene = SceneManager.GetActiveScene();
                var all = Resources.FindObjectsOfTypeAll<EnemyIdentifier>();
                var candidates = new List<EnemyIdentifier>(all.Length);
                for (var i = 0; i < all.Length; i++)
                {
                    var candidate = all[i];
                    if (candidate == null || !candidate.gameObject.scene.IsValid() ||
                        !candidate.gameObject.scene.isLoaded || candidate.gameObject.scene != scene) continue;
                    candidates.Add(candidate);
                }
                cachedLocalEnemyCandidates = candidates.ToArray();
                nextLocalEnemyScanTime = Time.unscaledTime + 0.1f;
            }
            return cachedLocalEnemyCandidates;
        }

        private EnemyIdentifier ResolveLocalEnemy(EnemyWireState state, bool allowNearestFallback, EnemyIdentifier[] candidates)
        {
            EnemyIdentifier enemy;
            if (enemiesById.TryGetValue(state.NetworkId, out enemy) && enemy != null) return enemy;
            if (enemy != null) enemiesById.Remove(state.NetworkId);

            if (state.StableKey != 0)
            {
                foreach (var candidate in candidates)
                {
                    if (candidate == null || candidate.enemyType != state.Type || idsByEnemy.ContainsKey(candidate)) continue;
                    if (!candidate.gameObject.activeInHierarchy && (candidate.transform.position - state.Position).sqrMagnitude > 250f * 250f) continue;
                    if (EnemyIdentityKey.For(candidate.transform) == state.StableKey)
                    {
                        TrackEnemy(candidate, state.NetworkId);
                        return candidate;
                    }
                }
            }

            // Give a client still loading a level time to create the matching hierarchy before
            // considering a spatial fallback that could claim a different same-type enemy.
            if (!allowNearestFallback) return null;

            var bestDistance = float.PositiveInfinity;
            var secondBestDistance = float.PositiveInfinity;
            EnemyIdentifier best = null;
            foreach (var candidate in candidates)
            {
                if (candidate == null || (state.Active && !candidate.gameObject.activeInHierarchy) ||
                    candidate.enemyType != state.Type || idsByEnemy.ContainsKey(candidate)) continue;
                var distance = (candidate.transform.position - state.Position).sqrMagnitude;
                if (distance < bestDistance)
                {
                    secondBestDistance = bestDistance;
                    bestDistance = distance;
                    best = candidate;
                }
                else if (distance < secondBestDistance) secondBestDistance = distance;
            }
            if (best == null || !EnemyMatchPolicy.IsUnambiguousNearest(bestDistance, secondBestDistance)) return null;
            TrackEnemy(best, state.NetworkId);
            return best;
        }

        private void PruneDestroyed()
        {
            destroyedIdsScratch.Clear();
            foreach (var pair in enemiesById) if (pair.Value == null) destroyedIdsScratch.Add(pair.Key);
            for (var i = 0; i < destroyedIdsScratch.Count; i++) RemoveTrackedEnemy(destroyedIdsScratch[i]);
            destroyedIdsScratch.Clear();

            destroyedReferencesScratch.Clear();
            foreach (var pair in idsByEnemy) if (pair.Key == null) destroyedReferencesScratch.Add(pair.Key);
            for (var i = 0; i < destroyedReferencesScratch.Count; i++) idsByEnemy.Remove(destroyedReferencesScratch[i]);
            destroyedReferencesScratch.Clear();
        }

        private void PruneMissingHostEnemies(NetworkService network, HashSet<EnemyIdentifier> present)
        {
            var now = network.NetworkTimeSeconds;
            missingIdsScratch.Clear();
            foreach (var pair in idsByEnemy)
            {
                var id = pair.Value;
                if (pair.Key != null && present.Contains(pair.Key))
                {
                    absentSinceByEnemy.Remove(id);
                    continue;
                }
                float firstMissingAt;
                if (!absentSinceByEnemy.TryGetValue(id, out firstMissingAt))
                {
                    absentSinceByEnemy[id] = now;
                    continue;
                }
                if (now - firstMissingAt >= DespawnConfirmationSeconds) missingIdsScratch.Add(id);
            }

            for (var i = 0; i < missingIdsScratch.Count; i++)
            {
                var id = missingIdsScratch[i];
                uint stableKey;
                EnemyType enemyType;
                if (!stableKeyByEnemyId.TryGetValue(id, out stableKey) || stableKey == 0 || !enemyTypeById.TryGetValue(id, out enemyType))
                {
                    RemoveTrackedEnemy(id);
                    continue;
                }
                network.BroadcastReliable(MessageType.EnemyDespawn, EnemyDespawnCodec.Encode(id, stableKey, (int)enemyType));
                RemoveTrackedEnemy(id);
            }
            missingIdsScratch.Clear();
        }

        private void RemoveTrackedEnemy(uint id)
        {
            enemiesById.Remove(id);
            stableKeyByEnemyId.Remove(id);
            enemyTypeById.Remove(id);
            latestRemoteStates.Remove(id);
            latestFrameByEnemy.Remove(id);
            appliedHealthFrameByEnemy.Remove(id);
            appliedAnimationFrameByEnemy.Remove(id);
            latestDamageFrameByEnemy.Remove(id);
            appliedDamageFrameByEnemy.Remove(id);
            latestDamageStates.Remove(id);
            unresolvedAgeByEnemy.Remove(id);
            nextReplicaAttempt.Remove(id);
            absentSinceByEnemy.Remove(id);
            trackedKeysScratch.Clear();
            foreach (var pair in idsByEnemy)
                if (pair.Value == id) trackedKeysScratch.Add(pair.Key);
            for (var i = 0; i < trackedKeysScratch.Count; i++) idsByEnemy.Remove(trackedKeysScratch[i]);
            trackedKeysScratch.Clear();
        }

        private void TrackEnemy(EnemyIdentifier enemy, uint id)
        {
            if (enemy == null || id == 0) return;
            idsByEnemy[enemy] = id;
            enemiesById[id] = enemy;
            var stableKey = EnemyIdentityKey.For(enemy.transform);
            if (stableKey != 0) stableKeyByEnemyId[id] = stableKey;
            enemyTypeById[id] = enemy.enemyType;
        }

        private static void WriteState(BinaryWriter writer, EnemyWireState state)
        {
            writer.Write(state.NetworkId);
            writer.Write(state.StableKey);
            writer.Write((int)state.Type);
            writer.Write(state.Position.x); writer.Write(state.Position.y); writer.Write(state.Position.z);
            writer.Write(state.Rotation.x); writer.Write(state.Rotation.y); writer.Write(state.Rotation.z); writer.Write(state.Rotation.w);
            writer.Write(Finite(state.Health) ? Mathf.Clamp(state.Health, 0f, 100000f) : 0f);
            writer.Write(state.Dead);
            writer.Write((byte)((state.HasKinematics ? 1 : 0) | (state.HasAnimation ? 2 : 0) | (state.Active ? 4 : 0)));
            var velocity = ValidVector(state.Velocity) && state.Velocity.sqrMagnitude <= 100000000f ? state.Velocity : Vector3.zero;
            writer.Write(velocity.x); writer.Write(velocity.y); writer.Write(velocity.z);
            writer.Write(state.AnimationStateHash);
            writer.Write(Finite(state.AnimationNormalizedTime) && state.AnimationNormalizedTime >= 0f && state.AnimationNormalizedTime < 1f ? state.AnimationNormalizedTime : 0f);
            EnemyAnimatorParameterCodec.Write(writer, state.AnimationParameters);
            writer.Write(state.AnimationSpeed);
        }

        private static EnemyWireState ReadState(BinaryReader reader, uint frame)
        {
            var id = reader.ReadUInt32();
            var stableKey = reader.ReadUInt32();
            var type = (EnemyType)reader.ReadInt32();
            var position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            var rotation = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            var health = reader.ReadSingle();
            var dead = reader.ReadBoolean();
            var flags = reader.ReadByte();
            var velocity = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            var animationHash = reader.ReadInt32();
            var animationTime = reader.ReadSingle();
            var animationParameters = EnemyAnimatorParameterCodec.Read(reader);
            var animationSpeed = reader.ReadSingle();
            var rotationMagnitude = rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w;
            if (id == 0 || !Enum.IsDefined(typeof(EnemyType), type) ||
                !Finite(position.x) || !Finite(position.y) || !Finite(position.z) || position.sqrMagnitude > 100000f * 100000f ||
                !Finite(rotation.x) || !Finite(rotation.y) || !Finite(rotation.z) || !Finite(rotation.w) || rotationMagnitude < 0.01f || rotationMagnitude > 4f ||
                !Finite(health) || health < 0 || health > 100000f || (flags & ~7) != 0 ||
                !Finite(velocity.x) || !Finite(velocity.y) || !Finite(velocity.z) || velocity.sqrMagnitude > 100000000f ||
                !Finite(animationTime) || animationTime < 0f || animationTime >= 1f ||
                !Finite(animationSpeed) || animationSpeed < 0f || animationSpeed > 16f)
                throw new InvalidDataException("Invalid enemy snapshot values.");
            return new EnemyWireState { NetworkId = id, StableKey = stableKey, Type = type, Position = position, Rotation = rotation, Health = health, Dead = dead, Active = (flags & 4) != 0, Velocity = velocity, AnimationStateHash = animationHash, AnimationNormalizedTime = animationTime, AnimationSpeed = animationSpeed, AnimationParameters = animationParameters, HasKinematics = (flags & 1) != 0, HasAnimation = (flags & 2) != 0, Frame = frame };
        }

        private static bool IsNewer(uint candidate, uint previous)
        {
            return unchecked((int)(candidate - previous)) > 0;
        }

        private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
        private static bool ValidVector(Vector3 value)
        {
            return Finite(value.x) && Finite(value.y) && Finite(value.z);
        }
        private static bool ValidRotation(Quaternion value)
        {
            if (!Finite(value.x) || !Finite(value.y) || !Finite(value.z) || !Finite(value.w)) return false;
            var magnitude = value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w;
            return Finite(magnitude) && magnitude >= 0.01f && magnitude <= 4f;
        }
    }
}

