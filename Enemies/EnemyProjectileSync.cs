using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ULTRAKILL.Bloodlink.Networking;
using ULTRAKILL.Portal;
using UnityEngine;

namespace ULTRAKILL.Bloodlink.Enemies
{
    internal sealed class EnemyProjectileState
    {
        public uint Id;
        public uint Frame;
        public int SafeEnemyType;
        public string PrefabName;
        public string BulletType;
        public string WeaponType;
        public int Damage;
        public float CollisionRadius;
        public bool Explosive;
        public bool Parried;
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Velocity;
    }

    internal static class EnemyProjectileCodec
    {
        private const int MaximumBatchCount = 8;
        private const int MaximumPrefabNameBytes = 32;
        private const int MaximumTypeNameBytes = 16;

        public static byte[] EncodeBatch(uint frame, IList<EnemyProjectileState> states, int start, int count)
        {
            if (frame == 0 || states == null || start < 0 || count < 1 || count > MaximumBatchCount || start + count > states.Count)
                throw new ArgumentOutOfRangeException();
            using (var stream = new MemoryStream(5 + count * 120))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(frame);
                writer.Write((byte)count);
                for (var i = 0; i < count; i++) WriteState(writer, states[start + i], frame);
                return stream.ToArray();
            }
        }

        public static List<EnemyProjectileState> DecodeBatch(byte[] payload)
        {
            if (payload == null || payload.Length < 5) throw new InvalidDataException("Enemy projectile batch is too short.");
            using (var stream = new MemoryStream(payload, false))
            using (var reader = new BinaryReader(stream))
            {
                var frame = reader.ReadUInt32();
                var count = reader.ReadByte();
                if (frame == 0 || count == 0 || count > MaximumBatchCount) throw new InvalidDataException("Invalid enemy projectile batch header.");
                var result = new List<EnemyProjectileState>(count);
                for (var i = 0; i < count; i++) result.Add(ReadState(reader, frame));
                if (stream.Position != stream.Length) throw new InvalidDataException("Trailing bytes in enemy projectile batch.");
                return result;
            }
        }

        private static void WriteState(BinaryWriter writer, EnemyProjectileState state, uint frame)
        {
            if (state == null || state.Id == 0 || !Finite(state.Position) || !Finite(state.Rotation) || !Finite(state.Velocity))
                throw new InvalidDataException("Invalid enemy projectile state.");
            writer.Write(state.Id);
            writer.Write(state.SafeEnemyType);
            WriteString(writer, state.PrefabName, MaximumPrefabNameBytes);
            WriteString(writer, state.BulletType, MaximumTypeNameBytes);
            WriteString(writer, state.WeaponType, MaximumTypeNameBytes);
            writer.Write(state.Damage);
            writer.Write(state.CollisionRadius);
            writer.Write(state.Explosive);
            writer.Write(state.Parried);
            WriteVector(writer, state.Position);
            writer.Write(state.Rotation.x); writer.Write(state.Rotation.y); writer.Write(state.Rotation.z); writer.Write(state.Rotation.w);
            WriteVector(writer, state.Velocity);
        }

        private static EnemyProjectileState ReadState(BinaryReader reader, uint frame)
        {
            var state = new EnemyProjectileState
            {
                Id = reader.ReadUInt32(),
                Frame = frame,
                SafeEnemyType = reader.ReadInt32(),
                PrefabName = ReadString(reader, MaximumPrefabNameBytes),
                BulletType = ReadString(reader, MaximumTypeNameBytes),
                WeaponType = ReadString(reader, MaximumTypeNameBytes),
                Damage = reader.ReadInt32(),
                CollisionRadius = reader.ReadSingle(),
                Explosive = reader.ReadBoolean(),
                Parried = reader.ReadBoolean(),
                Position = ReadVector(reader),
                Rotation = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                Velocity = ReadVector(reader)
            };
            var rotationMagnitude = state.Rotation.x * state.Rotation.x + state.Rotation.y * state.Rotation.y +
                state.Rotation.z * state.Rotation.z + state.Rotation.w * state.Rotation.w;
            if (state.Id == 0 || string.IsNullOrEmpty(state.PrefabName) || !Enum.IsDefined(typeof(EnemyType), state.SafeEnemyType) ||
                state.Damage < 0 || state.Damage > 10000 || !Finite(state.CollisionRadius) ||
                state.CollisionRadius < 0f || state.CollisionRadius > 4f ||
                !Finite(state.Position) || state.Position.sqrMagnitude > 100000f * 100000f ||
                !Finite(state.Rotation) || rotationMagnitude < 0.01f || rotationMagnitude > 4f ||
                !Finite(state.Velocity) || state.Velocity.sqrMagnitude > 100000000f)
                throw new InvalidDataException("Invalid enemy projectile state values.");
            return state;
        }

        private static void WriteString(BinaryWriter writer, string value, int maximumBytes)
        {
            var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            if (bytes.Length > maximumBytes) throw new InvalidDataException("Enemy projectile identifier is too long.");
            writer.Write((byte)bytes.Length);
            writer.Write(bytes);
        }

        private static string ReadString(BinaryReader reader, int maximumBytes)
        {
            var length = reader.ReadByte();
            if (length > maximumBytes) throw new InvalidDataException("Enemy projectile identifier is too long.");
            var bytes = reader.ReadBytes(length);
            if (bytes.Length != length) throw new EndOfStreamException();
            try { return new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException ex) { throw new InvalidDataException("Invalid enemy projectile identifier encoding.", ex); }
        }

        private static void WriteVector(BinaryWriter writer, Vector3 value)
        {
            writer.Write(value.x); writer.Write(value.y); writer.Write(value.z);
        }

        private static Vector3 ReadVector(BinaryReader reader)
        {
            return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        private static bool Finite(Vector3 value)
        {
            return Finite(value.x) && Finite(value.y) && Finite(value.z);
        }

        private static bool Finite(Quaternion value)
        {
            return Finite(value.x) && Finite(value.y) && Finite(value.z) && Finite(value.w);
        }

        private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
    }

    internal sealed class RemoteEnemyProjectile : MonoBehaviour
    {
        private Vector3 targetPosition;
        private Quaternion targetRotation = Quaternion.identity;
        private Vector3 targetVelocity;
        private float stateReceivedAt;
        private float originalSpeed;
        private HomingType originalHomingType;
        private bool originalStateCaptured;
        private bool parryMotion;
        private Vector3 parryVelocity;
        public uint NetworkId { get; private set; }

        public void SetNetworkId(uint id) { NetworkId = id; }

        public void SetState(EnemyProjectileState state)
        {
            var projectile = GetComponent<Projectile>();
            if (!originalStateCaptured && projectile != null)
            {
                originalSpeed = projectile.speed;
                originalHomingType = projectile.homingType;
                originalStateCaptured = true;
            }
            if (projectile != null)
            {
                projectile.hittingPlayer = false;
                if (state.Parried)
                {
                    projectile.friendly = true;
                    projectile.playerBullet = false;
                    projectile.parried = true;
                    projectile.homingType = HomingType.None;
                    projectile.explosive = false;
                }
                else
                {
                    projectile.friendly = false;
                    projectile.playerBullet = false;
                    projectile.parried = false;
                    projectile.speed = originalSpeed;
                    projectile.homingType = originalHomingType;
                    projectile.explosive = state.Explosive;
                }
            }
            targetPosition = state.Position;
            targetRotation = state.Rotation;
            targetVelocity = state.Velocity;
            parryMotion = state.Parried;
            if (state.Parried) parryVelocity = state.Velocity;
            if (state.Parried && state.Velocity.sqrMagnitude > 0.001f)
                transform.rotation = Quaternion.LookRotation(state.Velocity);
            stateReceivedAt = Time.unscaledTime;
        }

        public void ApplyParry(EnemyProjectileParryResult result)
        {
            if (result == null || result.ProjectileId != NetworkId) return;
            targetPosition = result.Position;
            parryVelocity = result.Velocity;
            parryMotion = true;
            transform.position = targetPosition;
            if (parryVelocity.sqrMagnitude > 0.001f) transform.rotation = Quaternion.LookRotation(parryVelocity);
            var projectile = GetComponent<Projectile>();
            if (projectile != null)
            {
                projectile.friendly = true;
                projectile.parried = true;
                projectile.hittingPlayer = false;
                projectile.homingType = HomingType.None;
                projectile.explosive = false;
                if (projectile.speed > 0f) projectile.speed = Mathf.Max(100f, projectile.speed * 2f);
            }
        }

        private void Update()
        {
            if (parryMotion)
            {
                transform.position += parryVelocity * Time.unscaledDeltaTime;
                if (parryVelocity.sqrMagnitude > 0.001f) transform.rotation = Quaternion.LookRotation(parryVelocity);
                return;
            }
            var extrapolation = Mathf.Clamp(Time.unscaledTime - stateReceivedAt, 0f, 0.1f);
            var desiredPosition = targetPosition + targetVelocity * extrapolation;
            var blend = 1f - Mathf.Exp(-22f * Time.unscaledDeltaTime);
            var previousPosition = transform.position;
            transform.position = Vector3.Lerp(transform.position, desiredPosition, blend);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, blend);
        }
    }

    public sealed class EnemyProjectileSync
    {
        private sealed class RemoteEntry
        {
            public RemoteEnemyProjectile Visual;
            public uint LastFrame;
            public float LastReceivedAt;
        }

        private const int StatesPerBatch = 8;
        private const float SnapshotInterval = 1f / 60f;
        private const float RemoteExpiration = 0.45f;
        private readonly Dictionary<Projectile, uint> idsByProjectile = new Dictionary<Projectile, uint>();
        private readonly Dictionary<uint, Projectile> projectilesById = new Dictionary<uint, Projectile>();
        private readonly Dictionary<uint, Vector3> previousPositionsById = new Dictionary<uint, Vector3>();
        private readonly Dictionary<uint, HashSet<uint>> playersHitByProjectile = new Dictionary<uint, HashSet<uint>>();
        private readonly HashSet<Projectile> presentProjectiles = new HashSet<Projectile>();
        private readonly List<EnemyProjectileState> stateScratch = new List<EnemyProjectileState>(64);
        private readonly Dictionary<uint, RemoteEntry> remoteById = new Dictionary<uint, RemoteEntry>();
        private readonly Dictionary<string, GameObject> prefabCache = new Dictionary<string, GameObject>(StringComparer.Ordinal);
        private readonly ReliableEventSequenceTracker parryRequestSequences = new ReliableEventSequenceTracker();
        private readonly DamageRequestRateLimiter parryRequestRateLimiter = new DamageRequestRateLimiter(4f, 8f);
        private uint nextId = 1;
        private uint nextParrySequence;
        private uint frame;
        private float timer;

        public void Update(NetworkService network, float deltaTime)
        {
            if (network == null || !network.IsConnected) return;
            if (network.IsHost) UpdateHost(network, deltaTime);
            else UpdateClient(deltaTime);
        }

        public void SendCurrentToPlayer(NetworkService network, uint playerId)
        {
            if (network == null || !network.IsHost || playerId == 0) return;
            if (frame == 0) frame = 1;
            CaptureHostStates();
            for (var start = 0; start < stateScratch.Count; start += StatesPerBatch)
            {
                var count = Math.Min(StatesPerBatch, stateScratch.Count - start);
                network.SendToPlayer(playerId, MessageType.EnemyProjectileSnapshot,
                    EnemyProjectileCodec.EncodeBatch(frame, stateScratch, start, count));
            }
        }

        public void ReceiveBatch(byte[] payload)
        {
            var batch = EnemyProjectileCodec.DecodeBatch(payload);
            for (var i = 0; i < batch.Count; i++)
            {
                var state = batch[i];
                RemoteEntry entry;
                if (!remoteById.TryGetValue(state.Id, out entry))
                {
                    var prefab = ResolvePrefab(state);
                    if (prefab == null) continue;
                    var instance = UnityEngine.Object.Instantiate(prefab, state.Position, state.Rotation);
                    instance.name = "Bloodlink Enemy Projectile " + state.Id;
                    foreach (var behaviour in instance.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled = false;
                    foreach (var body in instance.GetComponentsInChildren<Rigidbody>(true))
                    {
                        body.isKinematic = true;
                        body.useGravity = false;
                        body.velocity = Vector3.zero;
                    }
                    var visual = instance.AddComponent<RemoteEnemyProjectile>();
                    visual.SetNetworkId(state.Id);
                    entry = new RemoteEntry { Visual = visual, LastFrame = 0, LastReceivedAt = Time.unscaledTime };
                    remoteById.Add(state.Id, entry);
                }
                if (entry.LastFrame != 0 && !IsNewer(state.Frame, entry.LastFrame)) continue;
                entry.LastFrame = state.Frame;
                entry.LastReceivedAt = Time.unscaledTime;
                if (entry.Visual != null) entry.Visual.SetState(state);
            }
        }

        public bool RequestParry(Projectile projectile, NetworkService network)
        {
            var visual = projectile != null ? projectile.GetComponent<RemoteEnemyProjectile>() : null;
            if (visual == null || visual.NetworkId == 0 || network == null || network.IsHost || !network.IsConnected) return false;
            unchecked { nextParrySequence++; }
            if (nextParrySequence == 0) nextParrySequence = 1;
            var payload = EnemyProjectileParryCodec.EncodeRequest(nextParrySequence, network.LocalSnapshotTick, visual.NetworkId);
            if (!network.SendToHost(MessageType.EnemyProjectileParryRequest, payload)) return false;
            return true;
        }

        public bool TryAuthorizeParry(uint senderId, byte[] payload, NetworkService network, out byte[] resultPayload)
        {
            resultPayload = null;
            if (network == null || !network.IsHost || senderId == 0 || !network.HasFreshPlayerSnapshot(senderId, 0.5f)) return false;
            var request = EnemyProjectileParryCodec.DecodeRequest(payload);
            PlayerSnapshot currentPlayer;
            PlayerSnapshot attackSnapshot;
            if (!network.Players.TryGetValue(senderId, out currentPlayer) || currentPlayer == null ||
                (currentPlayer.MovementFlags & 4) != 0 ||
                !network.TryGetPlayerSnapshotAtTick(senderId, request.SnapshotTick, 15, out attackSnapshot) ||
                attackSnapshot == null || (attackSnapshot.MovementFlags & 4) != 0) return false;

            Projectile projectile;
            if (!projectilesById.TryGetValue(request.ProjectileId, out projectile) || projectile == null ||
                !projectile.gameObject.activeInHierarchy || projectile.friendly || projectile.playerBullet ||
                projectile.unparryable || projectile.undeflectable) return false;

            var origin = attackSnapshot.Position + Vector3.up * 1.2f;
            var toProjectile = projectile.transform.position - origin;
            if (toProjectile.sqrMagnitude > 4.5f * 4.5f || toProjectile.sqrMagnitude < 0.0001f) return false;
            var direction = attackSnapshot.ViewRotation * Vector3.forward;
            if (Vector3.Angle(direction, toProjectile) > 55f) return false;
            if (!parryRequestRateLimiter.TryAccept(senderId, network.NetworkTimeSeconds) ||
                !parryRequestSequences.TryAccept(senderId, request.Sequence)) return false;

            projectile.hittingPlayer = false;
            projectile.friendly = true;
            projectile.parried = true;
            projectile.speed *= 2f;
            projectile.homingType = HomingType.None;
            projectile.precheckForCollisions = true;
            projectile.explosive = false;
            projectile.SetPortalTravellerType(PortalTravellerType.PLAYER_PROJECTILE);
            if (projectile.parryChallenge != null) projectile.parryChallenge.Done();
            var beams = projectile.GetComponentsInChildren<ContinuousBeam>(true);
            for (var i = 0; i < beams.Length; i++)
            {
                if (beams[i] == null) continue;
                if (beams[i].enemy) beams[i].parryMultiplier = 2.5f;
                beams[i].enemy = false;
                beams[i].canHitEnemy = true;
                beams[i].canHitPlayer = false;
            }
            projectile.transform.SetParent(null, true);
            projectile.transform.rotation = Quaternion.LookRotation(direction);
            var body = projectile.GetComponent<Rigidbody>();
            if (body != null)
            {
                body.constraints = RigidbodyConstraints.FreezeRotation;
                if (body.GetGravityMode()) body.SetGravityMode(false);
                var speed = projectile.speed <= 0f ? 250f : Mathf.Max(100f, projectile.speed);
                body.velocity = direction * speed;
            }
            else if (projectile.speed > 0f)
            {
                projectile.speed = Mathf.Max(100f, projectile.speed);
            }
            var velocity = body != null ? body.velocity : direction * Mathf.Max(projectile.speed, 100f);
            resultPayload = EnemyProjectileParryCodec.EncodeResult(new EnemyProjectileParryResult
            {
                ProjectileId = request.ProjectileId,
                PlayerId = senderId,
                Position = projectile.transform.position,
                Velocity = velocity
            });
            return true;
        }

        public void ReceiveParryResult(byte[] payload)
        {
            var result = EnemyProjectileParryCodec.DecodeResult(payload);
            RemoteEntry entry;
            if (!remoteById.TryGetValue(result.ProjectileId, out entry) || entry == null || entry.Visual == null) return;
            entry.Visual.ApplyParry(result);
        }

        public void RemovePlayer(uint playerId)
        {
            parryRequestSequences.Remove(playerId);
            parryRequestRateLimiter.Remove(playerId);
        }

        public void Reset()
        {
            foreach (var entry in remoteById.Values)
                if (entry.Visual != null) UnityEngine.Object.Destroy(entry.Visual.gameObject);
            remoteById.Clear();
            idsByProjectile.Clear();
            projectilesById.Clear();
            previousPositionsById.Clear();
            playersHitByProjectile.Clear();
            presentProjectiles.Clear();
            stateScratch.Clear();
            prefabCache.Clear();
            parryRequestSequences.Clear();
            parryRequestRateLimiter.Clear();
            nextId = 1;
            nextParrySequence = 0;
            frame = 0;
            timer = 0f;
        }

        private void UpdateHost(NetworkService network, float deltaTime)
        {
            timer += Mathf.Max(0f, deltaTime);
            if (timer < SnapshotInterval) return;
            timer %= SnapshotInterval;
            unchecked { frame++; }
            if (frame == 0) frame = 1;
            CaptureHostStates();
            AuthorizeRemotePlayerProjectileHits(network, Mathf.Max(SnapshotInterval, deltaTime));
            for (var start = 0; start < stateScratch.Count; start += StatesPerBatch)
            {
                var count = Math.Min(StatesPerBatch, stateScratch.Count - start);
                network.BroadcastUnreliable(MessageType.EnemyProjectileSnapshot,
                    EnemyProjectileCodec.EncodeBatch(frame, stateScratch, start, count));
            }
        }

        private void CaptureHostStates()
        {
            stateScratch.Clear();
            presentProjectiles.Clear();
            var projectiles = UnityEngine.Object.FindObjectsOfType<Projectile>();
            for (var i = 0; i < projectiles.Length; i++)
            {
                var projectile = projectiles[i];
                if (projectile == null || !projectile.gameObject.activeInHierarchy || projectile.decorative ||
                    ((projectile.friendly || projectile.playerBullet) && !projectile.parried)) continue;
                presentProjectiles.Add(projectile);
                uint id;
                if (!idsByProjectile.TryGetValue(projectile, out id))
                {
                    id = nextId++;
                    if (id == 0) id = nextId++;
                    idsByProjectile[projectile] = id;
                }
                projectilesById[id] = projectile;
                var prefabName = NormalizePrefabName(projectile.gameObject.name);
                var velocity = Vector3.zero;
                var body = projectile.GetComponent<Rigidbody>();
                if (body != null) velocity = body.velocity;
                else if (projectile.speed > 0f) velocity = projectile.transform.forward * projectile.speed;
                var collider = projectile.GetComponentInChildren<Collider>();
                var collisionRadius = collider != null
                    ? Mathf.Clamp(collider.bounds.extents.magnitude, 0.03f, 1.5f)
                    : 0.12f;
                stateScratch.Add(new EnemyProjectileState
                {
                    Id = id,
                    Frame = frame,
                    SafeEnemyType = (int)projectile.safeEnemyType,
                    PrefabName = FitIdentifier(prefabName, 32),
                    BulletType = FitIdentifier(projectile.bulletType, 16),
                    WeaponType = FitIdentifier(projectile.weaponType, 16),
                    Damage = Mathf.Clamp(Mathf.RoundToInt(projectile.damage), 0, 10000),
                    CollisionRadius = collisionRadius,
                    Explosive = projectile.explosive,
                    Parried = projectile.parried,
                    Position = projectile.transform.position,
                    Rotation = projectile.transform.rotation,
                    Velocity = velocity
                });
                var state = stateScratch[stateScratch.Count - 1];
                if (state.PrefabName.Length == 0 || !Finite(state.Position) || !Finite(state.Rotation) || !Finite(state.Velocity))
                    stateScratch.RemoveAt(stateScratch.Count - 1);
            }

            var stale = new List<Projectile>();
            foreach (var projectile in idsByProjectile.Keys)
                if (projectile == null || !presentProjectiles.Contains(projectile)) stale.Add(projectile);
            for (var i = 0; i < stale.Count; i++)
            {
                uint id;
                if (idsByProjectile.TryGetValue(stale[i], out id))
                {
                    projectilesById.Remove(id);
                    previousPositionsById.Remove(id);
                    playersHitByProjectile.Remove(id);
                }
                idsByProjectile.Remove(stale[i]);
            }
        }

        private void AuthorizeRemotePlayerProjectileHits(NetworkService network, float elapsed)
        {
            for (var i = 0; i < stateScratch.Count; i++)
            {
                var state = stateScratch[i];
                if (state.Parried || state.Explosive || state.Damage <= 0) continue;

                Vector3 previousPosition;
                if (!previousPositionsById.TryGetValue(state.Id, out previousPosition))
                    previousPosition = state.Position - state.Velocity * elapsed;

                HashSet<uint> hitPlayers;
                if (!playersHitByProjectile.TryGetValue(state.Id, out hitPlayers))
                {
                    hitPlayers = new HashSet<uint>();
                    playersHitByProjectile.Add(state.Id, hitPlayers);
                }

                foreach (var pair in network.Players)
                {
                    var player = pair.Value;
                    if (player == null || player.PlayerId == network.LocalPlayerId || player.Health == 0 ||
                        (player.MovementFlags & 4) != 0 || !network.HasFreshPlayerSnapshot(player.PlayerId, 0.25f) ||
                        hitPlayers.Contains(player.PlayerId)) continue;

                    var playerCenter = player.Position + Vector3.up * 0.9f + player.Velocity * (elapsed * 0.5f);
                    var radius = Mathf.Clamp(state.CollisionRadius + 0.65f, 0.7f, 2.1f);
                    if (DistanceToSegmentSquared(playerCenter, previousPosition, state.Position) > radius * radius) continue;

                    hitPlayers.Add(player.PlayerId);
                    var health = (ushort)Mathf.Max(0, player.Health - Mathf.Clamp(state.Damage, 1, 200));
                    network.SetAuthoritativePlayerVitals(player.PlayerId, health, health == 0);
                }

                previousPositionsById[state.Id] = state.Position;
            }
        }

        private static float DistanceToSegmentSquared(Vector3 point, Vector3 start, Vector3 end)
        {
            var segment = end - start;
            var lengthSquared = segment.sqrMagnitude;
            if (lengthSquared <= 0.000001f) return (point - start).sqrMagnitude;
            var t = Mathf.Clamp01(Vector3.Dot(point - start, segment) / lengthSquared);
            var closest = start + segment * t;
            return (point - closest).sqrMagnitude;
        }

        private void UpdateClient(float deltaTime)
        {
            var expired = new List<uint>();
            foreach (var pair in remoteById)
                if (pair.Value.Visual == null || Time.unscaledTime - pair.Value.LastReceivedAt > RemoteExpiration) expired.Add(pair.Key);
            for (var i = 0; i < expired.Count; i++)
            {
                RemoteEntry entry;
                if (!remoteById.TryGetValue(expired[i], out entry)) continue;
                if (entry.Visual != null) UnityEngine.Object.Destroy(entry.Visual.gameObject);
                remoteById.Remove(expired[i]);
            }
        }

        private GameObject ResolvePrefab(EnemyProjectileState state)
        {
            var key = state.PrefabName + "|" + state.BulletType + "|" + state.WeaponType + "|" + state.SafeEnemyType;
            GameObject cached;
            if (prefabCache.TryGetValue(key, out cached)) return cached;
            var candidates = Resources.FindObjectsOfTypeAll<Projectile>();
            for (var i = 0; i < candidates.Length; i++)
            {
                var candidate = candidates[i];
                if (candidate == null || candidate.gameObject.scene.IsValid() ||
                    !string.Equals(FitIdentifier(NormalizePrefabName(candidate.gameObject.name), 32), state.PrefabName, StringComparison.Ordinal) ||
                    !string.Equals(FitIdentifier(candidate.bulletType, 16), state.BulletType, StringComparison.Ordinal) ||
                    !string.Equals(FitIdentifier(candidate.weaponType, 16), state.WeaponType, StringComparison.Ordinal)) continue;
                prefabCache[key] = candidate.gameObject;
                return candidate.gameObject;
            }
            return null;
        }

        private static string NormalizePrefabName(string name)
        {
            const string suffix = "(Clone)";
            if (string.IsNullOrEmpty(name)) return string.Empty;
            return name.EndsWith(suffix, StringComparison.Ordinal) ? name.Substring(0, name.Length - suffix.Length).TrimEnd() : name;
        }

        private static string FitIdentifier(string value, int maximumUtf8Bytes)
        {
            value = value ?? string.Empty;
            if (Encoding.UTF8.GetByteCount(value) <= maximumUtf8Bytes) return value;
            for (var length = value.Length - 1; length > 0; length--)
            {
                var prefix = value.Substring(0, length);
                if (Encoding.UTF8.GetByteCount(prefix) <= maximumUtf8Bytes) return prefix;
            }
            return string.Empty;
        }

        private static bool Finite(Vector3 value)
        {
            return Finite(value.x) && Finite(value.y) && Finite(value.z);
        }

        private static bool Finite(Quaternion value)
        {
            return Finite(value.x) && Finite(value.y) && Finite(value.z) && Finite(value.w);
        }

        private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }

        private static bool IsNewer(uint candidate, uint previous)
        {
            return unchecked((int)(candidate - previous)) > 0;
        }
    }
}
