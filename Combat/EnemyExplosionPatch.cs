using HarmonyLib;
using ULTRAKILL.Bloodlink.Core;

namespace ULTRAKILL.Bloodlink.Combat
{
    [HarmonyPatch(typeof(Explosion), "Start")]
    internal static class EnemyExplosionPatch
    {
        private static void Prefix(Explosion __instance)
        {
            var plugin = BloodlinkPlugin.Instance;
            if (plugin == null || !plugin.IsConnected || plugin.IsHost || __instance == null || !__instance.enemy) return;

            // Enemy explosions on clients are replicas. The host applies health changes and
            // sends the reliable visual event; allow the local launch response, but prevent
            // this duplicate scene copy from applying health before the host event arrives.
            __instance.harmless = true;
            __instance.damage = 0;
            __instance.playerDamageOverride = 0;
        }

        private static void Postfix(Explosion __instance)
        {
            BloodlinkPlugin.Instance?.NotifyEnemyExplosion(__instance);
        }
    }
}
