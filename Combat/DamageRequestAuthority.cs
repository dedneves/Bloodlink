using System;
using System.IO;
using System.Text;
using ULTRAKILL.Bloodlink.Enemies;
using ULTRAKILL.Bloodlink.Networking;
using UnityEngine;

namespace ULTRAKILL.Bloodlink.Combat
{
    internal sealed class EnemyDamageCapture
    {
        public EnemyIdentifier Enemy;
        public uint NetworkId, StableKey;
        public int EnemyType;
        public Vector3 Position;
        public Quaternion Rotation;
        public float HealthBefore;
        public byte[] Impact;
    }

    internal static class DamageRequestAuthority
    {
        private static int captureDepth;
        internal static bool IsCapturingNativeDamage => captureDepth > 0;
        internal static void EndCapture() { captureDepth = Math.Max(0, captureDepth - 1); }
        public static void Apply(uint senderId, byte[] payload, NetworkService network, EnemySyncRegistry registry,
            WindowedEventSequenceTracker sequences, DamageRequestRateLimiter limiter)
        {
            if (network == null || !network.IsHost || !network.Players.ContainsKey(senderId)) return;
            var request = DamageRequestCodec.Decode(payload);
            if (!limiter.TryAccept(senderId, network.NetworkTimeSeconds) || !sequences.TryAccept(senderId, request.RequestSequence)) return;
            var shooter = network.Players[senderId];
            if (shooter == null || (shooter.MovementFlags & 4) != 0) return;
            var enemy = registry.ResolveDamageTarget(request.EnemyId, request.StableKey, request.EnemyType, request.EnemyPosition);
            if (enemy == null || enemy.dead) return;
            // Native hit points may be projectile origins or boss weak-point positions.
            // Authenticate the player and resolve the exact entity; the game's own
            // damage implementation retains boss phases, resistances and immunities.
            if ((shooter.Position - request.ShooterPosition).sqrMagnitude > 300f * 300f ||
                (request.HitPoint - enemy.transform.position).sqrMagnitude > 1500f * 1500f) return;
            var target = string.IsNullOrEmpty(request.TargetPath) ? null : enemy.transform.Find(request.TargetPath);
            GameObject source = null;
            var guns = MonoSingleton<GunControl>.Instance;
            if (guns != null && guns.currentWeapon != null && DamageRequestPolicy.IsSelectedWeapon(request.WeaponName, guns.currentWeapon.name)) source = guns.currentWeapon;
            enemy.hitter = request.Hitter;
            enemy.hitterAttributes.Clear();
            foreach (var attribute in request.HitterAttributes)
                if (Enum.IsDefined(typeof(HitterAttribute), attribute)) enemy.hitterAttributes.Add((HitterAttribute)attribute);
            enemy.DeliverDamage(target != null ? target.gameObject : enemy.gameObject, request.Force, request.HitPoint,
                request.Multiplier, request.TryForExplode, request.CritMultiplier, source, request.IgnoreDamageMultiplier, request.FromExplosion);
        }

        public static EnemyDamageCapture Capture(EnemyIdentifier enemy, EnemySyncRegistry registry, PlayerSnapshot shooter,
            GameObject target, Vector3 force, Vector3 hitPoint, float multiplier, bool explode, float critical,
            GameObject source, bool ignoreScale, bool fromExplosion)
        {
            if (enemy == null) return null;
            enemy.ForceGetHealth();
            var id = registry.EnsureNetworkId(enemy);
            var snapshot = shooter ?? new PlayerSnapshot { PlayerId = 1, Position = enemy.transform.position, ViewRotation = Quaternion.identity, WeaponName = source != null ? source.name : "Unarmed" };
            var capture = new EnemyDamageCapture { Enemy = enemy, NetworkId = id, StableKey = EnemyIdentityKey.For(enemy.transform),
                EnemyType = (int)enemy.enemyType, Position = enemy.transform.position, Rotation = enemy.transform.rotation,
                HealthBefore = Mathf.Max(0f, enemy.health), Impact = DamageRequestCodec.Encode(1, id, enemy, target, snapshot,
                    snapshot.Position, force, hitPoint, multiplier, explode, critical, source, ignoreScale, fromExplosion) };
            captureDepth++;
            return capture;
        }

        public static void Publish(EnemyDamageCapture capture, NetworkService network, EnemySyncRegistry registry)
        {
            if (capture == null) return;
            var enemy = capture.Enemy;
            if (enemy != null) enemy.ForceGetHealth();
            registry.BroadcastNativeDamage(network, capture.NetworkId, capture.StableKey, capture.EnemyType,
                enemy != null ? enemy.transform.position : capture.Position, enemy != null ? enemy.transform.rotation : capture.Rotation,
                enemy != null ? Mathf.Max(0f, enemy.health) : 0f, enemy == null || enemy.dead || enemy.health <= 0f,
                capture.HealthBefore, capture.Impact);
        }
    }
}
