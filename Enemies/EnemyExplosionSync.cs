using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ULTRAKILL.Bloodlink.Networking;
using UnityEngine;

namespace ULTRAKILL.Bloodlink.Enemies
{
    internal sealed class EnemyExplosionEvent
    {
        public uint Sequence;
        public Vector3 Position;
        public float Radius;
        public int Damage;
        public int PlayerDamageOverride;
        public float PushForceMultiplier;
        public bool UltraBooster;
        public bool Boosted;
        public bool IsFup;
        public string PrefabName;
    }

    internal static class EnemyExplosionCodec
    {
        internal const int MaximumPrefabNameBytes = 48;

        public static byte[] Encode(EnemyExplosionEvent value)
        {
            Validate(value);
            var prefabName = Encoding.UTF8.GetBytes(value.PrefabName ?? string.Empty);
            if (prefabName.Length > MaximumPrefabNameBytes) throw new InvalidDataException("Enemy explosion prefab name is too long.");
            using (var stream = new MemoryStream(34 + prefabName.Length))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(value.Sequence);
                writer.Write(value.Position.x); writer.Write(value.Position.y); writer.Write(value.Position.z);
                writer.Write(value.Radius);
                writer.Write(value.Damage);
                writer.Write(value.PlayerDamageOverride);
                writer.Write(value.PushForceMultiplier);
                writer.Write(value.UltraBooster);
                writer.Write(value.Boosted);
                writer.Write(value.IsFup);
                writer.Write((byte)prefabName.Length);
                writer.Write(prefabName);
                return stream.ToArray();
            }
        }

        public static EnemyExplosionEvent Decode(byte[] payload)
        {
            if (payload == null || payload.Length < 34) throw new InvalidDataException("Invalid enemy explosion payload length.");
            using (var stream = new MemoryStream(payload, false))
            using (var reader = new BinaryReader(stream))
            {
                var result = new EnemyExplosionEvent
                {
                    Sequence = reader.ReadUInt32(),
                    Position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                    Radius = reader.ReadSingle(),
                    Damage = reader.ReadInt32(),
                    PlayerDamageOverride = reader.ReadInt32(),
                    PushForceMultiplier = reader.ReadSingle(),
                    UltraBooster = reader.ReadBoolean(),
                    Boosted = reader.ReadBoolean(),
                    IsFup = reader.ReadBoolean()
                };
                var nameLength = reader.ReadByte();
                if (nameLength > MaximumPrefabNameBytes || stream.Length - stream.Position != nameLength)
                    throw new InvalidDataException("Invalid enemy explosion prefab name length.");
                var nameBytes = reader.ReadBytes(nameLength);
                try { result.PrefabName = new UTF8Encoding(false, true).GetString(nameBytes); }
                catch (DecoderFallbackException ex) { throw new InvalidDataException("Invalid enemy explosion prefab name encoding.", ex); }
                Validate(result);
                return result;
            }
        }

        private static void Validate(EnemyExplosionEvent value)
        {
            if (value == null || value.Sequence == 0 ||
                !Finite(value.Position.x) || !Finite(value.Position.y) || !Finite(value.Position.z) ||
                value.Position.sqrMagnitude > 100000f * 100000f || !Finite(value.Radius) ||
                value.Radius <= 0f || value.Radius > 200f || value.Damage < 0 || value.Damage > 10000 ||
                value.PlayerDamageOverride < -1 || value.PlayerDamageOverride > 10000 ||
                !Finite(value.PushForceMultiplier) || value.PushForceMultiplier < 0f || value.PushForceMultiplier > 100f)
                throw new InvalidDataException("Invalid enemy explosion values.");
        }

        private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
    }

    public sealed class EnemyExplosionSync
    {
        private uint nextSequence;
        private uint lastReceivedSequence;

        public void Reset()
        {
            nextSequence = 0;
            lastReceivedSequence = 0;
        }

        public void Broadcast(NetworkService network, Explosion explosion)
        {
            if (network == null || !network.IsConnected || !network.IsHost || explosion == null ||
                !explosion.enemy || explosion.harmless || explosion.maxSize <= 0f) return;
            unchecked { nextSequence++; }
            if (nextSequence == 0) nextSequence = 1;
            var payload = EnemyExplosionCodec.Encode(new EnemyExplosionEvent
            {
                Sequence = nextSequence,
                Position = explosion.transform.position,
                Radius = explosion.maxSize,
                Damage = Mathf.Clamp(explosion.damage, 1, 10000),
                PlayerDamageOverride = explosion.playerDamageOverride,
                PushForceMultiplier = explosion.pushForceMultiplier,
                UltraBooster = explosion.ultrabooster,
                Boosted = explosion.boosted,
                IsFup = explosion.isFup,
                PrefabName = FitIdentifier(NormalizePrefabName(explosion.gameObject.name), EnemyExplosionCodec.MaximumPrefabNameBytes)
            });
            ApplyAuthoritativeRemotePlayerDamage(network, explosion.transform.position, explosion.maxSize,
                explosion.damage, explosion.playerDamageOverride, explosion.ultrabooster);
            network.BroadcastReliable(MessageType.EnemyExplosion, payload);
        }

        private static void ApplyAuthoritativeRemotePlayerDamage(NetworkService network, Vector3 origin, float radius,
            int baseDamage, int playerDamageOverride, bool ultraBooster)
        {
            foreach (var pair in network.Players)
            {
                var player = pair.Value;
                if (player == null || player.PlayerId == network.LocalPlayerId || player.Health == 0 ||
                    (player.MovementFlags & 4) != 0 || !network.HasFreshPlayerSnapshot(player.PlayerId, 0.25f)) continue;

                var center = player.Position + Vector3.up * 0.9f;
                var offset = center - origin;
                var distance = offset.magnitude;
                if (distance > radius) continue;
                if (distance > 0.05f && Physics.Raycast(origin, offset / distance, distance,
                    LayerMaskDefaults.Get(LMD.Environment), QueryTriggerInteraction.Ignore)) continue;

                var damage = ultraBooster ? (distance < 3f ? 35 : 50) : baseDamage;
                if (playerDamageOverride >= 0) damage = playerDamageOverride;
                if (damage <= 0) continue;
                var health = (ushort)Mathf.Max(0, player.Health - Mathf.Clamp(damage, 1, 200));
                network.SetAuthoritativePlayerVitals(player.PlayerId, health, health == 0);
            }
        }

        public void Receive(NetworkService network, byte[] payload)
        {
            if (network == null || !network.IsConnected || network.IsHost) return;
            var explosion = EnemyExplosionCodec.Decode(payload);
            if (lastReceivedSequence != 0 && !IsNewer(explosion.Sequence, lastReceivedSequence)) return;
            lastReceivedSequence = explosion.Sequence;
            SpawnVisual(explosion);

            var player = UnityEngine.Object.FindObjectOfType<NewMovement>();
            if (player == null || player.dead) return;
            var collider = player.playerCollider;
            var playerPoint = collider != null ? collider.ClosestPoint(explosion.Position) : player.transform.position + Vector3.up;
            var offset = playerPoint - explosion.Position;
            var distance = offset.magnitude;
            if (distance > explosion.Radius) return;
            if (distance > 0.05f && Physics.Raycast(explosion.Position, offset / distance, distance,
                LayerMaskDefaults.Get(LMD.Environment), QueryTriggerInteraction.Ignore)) return;

            if (!player.exploded && player.explosionLaunchResistance <= 0f &&
                (player.safeExplosionLaunchCooldown <= 0f || explosion.Damage > 0))
            {
                var launchOrigin = new Vector3(offset.x, 0f, offset.z).sqrMagnitude < 0.0625f ? playerPoint : explosion.Position;
                if (explosion.Boosted)
                    player.LaunchFromPointAtSpeed(launchOrigin, Mathf.Max(60f, player.rb != null ? player.rb.velocity.magnitude : 0f) * explosion.PushForceMultiplier);
                else if (explosion.IsFup)
                    player.LaunchFromPointAtSpeed(launchOrigin, 60f * explosion.PushForceMultiplier);
                else
                {
                    player.LaunchFromPoint(launchOrigin, 200f * explosion.PushForceMultiplier, explosion.Radius);
                    if (explosion.UltraBooster && distance < 12f)
                        player.LaunchFromPoint(launchOrigin, 200f * explosion.PushForceMultiplier, explosion.Radius);
                }
            }

            // Health is changed by the host before this reliable visual event is sent.
            // Keep only the local explosion launch here so movement still feels immediate.
        }

        private static bool IsNewer(uint candidate, uint previous)
        {
            return unchecked((int)(candidate - previous)) > 0;
        }

        private void SpawnVisual(EnemyExplosionEvent state)
        {
            if (string.IsNullOrEmpty(state.PrefabName)) return;
            var candidates = Resources.FindObjectsOfTypeAll<Explosion>();
            GameObject prefab = null;
            for (var i = 0; i < candidates.Length; i++)
            {
                var candidate = candidates[i];
                if (candidate == null || candidate.gameObject.scene.IsValid() ||
                    !string.Equals(FitIdentifier(NormalizePrefabName(candidate.gameObject.name), EnemyExplosionCodec.MaximumPrefabNameBytes), state.PrefabName, StringComparison.Ordinal)) continue;
                prefab = candidate.gameObject;
                break;
            }
            if (prefab == null) return;
            var instance = UnityEngine.Object.Instantiate(prefab, state.Position, prefab.transform.rotation);
            instance.name = "Bloodlink Enemy Explosion " + state.Sequence;
            var effects = instance.GetComponentsInChildren<Explosion>(true);
            for (var i = 0; i < effects.Length; i++)
            {
                effects[i].harmless = true;
                effects[i].enemy = false;
                effects[i].damage = 0;
                effects[i].playerDamageOverride = 0;
                effects[i].maxSize = state.Radius;
            }
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
    }
}
