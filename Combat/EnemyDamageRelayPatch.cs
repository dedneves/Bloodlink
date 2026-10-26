using HarmonyLib;
using ULTRAKILL.Bloodlink.Core;
using ULTRAKILL.Bloodlink.Enemies;
using ULTRAKILL.Bloodlink.Networking;
using UnityEngine;

namespace ULTRAKILL.Bloodlink.Combat
{
    [HarmonyPatch(typeof(EnemyIdentifier), "DeliverDamage")]
    internal static class EnemyDamageRelayPatch
    {
        private static bool Prefix(EnemyIdentifier __instance, GameObject target, Vector3 force, Vector3 hitPoint,
            ref float multiplier, bool tryForExplode, float critMultiplier, GameObject sourceWeapon,
            bool ignoreTotalDamageTakenMultiplier, bool fromExplosion, out EnemyDamageCapture __state)
        {
            __state = null;
            var plugin = BloodlinkPlugin.Instance;
            if (plugin == null || !plugin.IsConnected) return true;
            if (EnemySyncRegistry.IsApplyingAuthoritativeHealthFor(__instance)) return true;
            if (EnemySyncRegistry.IsReplayingAuthoritativeDamage) return false;
            if (plugin.IsHost)
            {
                plugin.ScaleEnemyDamageForCoop(ref multiplier);
                __state = plugin.CaptureHostEnemyDamage(__instance, target, force, hitPoint, multiplier, tryForExplode,
                    critMultiplier, sourceWeapon, ignoreTotalDamageTakenMultiplier, fromExplosion);
                return true;
            }
            // Relay every supported hit, including explosions whose native sourceWeapon is null.
            // Only suppress local mutation after the hit is queued or sent to host authority.
            plugin.TryRelayEnemyDamage(__instance, target, force, hitPoint, multiplier, tryForExplode,
                critMultiplier, sourceWeapon, ignoreTotalDamageTakenMultiplier, fromExplosion);
            return false;
        }

        private static void Postfix(EnemyDamageCapture __state)
        {
            if (__state != null) BloodlinkPlugin.Instance?.PublishHostEnemyDamage(__state);
        }

        private static System.Exception Finalizer(System.Exception __exception, EnemyDamageCapture __state)
        { if (__state != null) DamageRequestAuthority.EndCapture(); return __exception; }
    }
}
