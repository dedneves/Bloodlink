using HarmonyLib;
using ULTRAKILL.Bloodlink.Core;

namespace ULTRAKILL.Bloodlink.Combat
{
    [HarmonyPatch(typeof(NewMovement), nameof(NewMovement.Respawn))]
    internal static class CoopPlayerRespawnPatch
    {
        private static bool Prefix(NewMovement __instance)
        {
            var plugin = BloodlinkPlugin.Instance;
            return plugin == null || plugin.PrepareCoopRespawn(__instance);
        }

        private static void Postfix(NewMovement __instance)
        {
            BloodlinkPlugin.Instance?.FinishCoopRespawn(__instance);
        }
    }

    [HarmonyPatch(typeof(CheckPoint), nameof(CheckPoint.OnRespawn))]
    internal static class CoopCheckpointRespawnPatch
    {
        private static bool Prefix()
        {
            var plugin = BloodlinkPlugin.Instance;
            return plugin == null || !plugin.InterceptNativeCheckpointRespawn();
        }
    }

    [HarmonyPatch(typeof(NewMovement), "GetHurt")]
    internal static class PlayerDamageAuthorityPatch
    {
        private static bool Prefix(NewMovement __instance, int damage, bool invincible, bool ignoreInvincibility)
        {
            var plugin = BloodlinkPlugin.Instance;
            return plugin == null || !plugin.InterceptLocalPlayerDamage(__instance, damage, invincible, ignoreInvincibility);
        }

        private static void Postfix(NewMovement __instance)
        {
            BloodlinkPlugin.Instance?.OnLocalPlayerVitalsChanged(__instance);
        }
    }

    [HarmonyPatch(typeof(NewMovement), "GetHealth")]
    internal static class PlayerHealAuthorityPatch
    {
        private static bool Prefix(NewMovement __instance, int health)
        {
            var plugin = BloodlinkPlugin.Instance;
            return plugin == null || !plugin.InterceptLocalPlayerHeal(__instance, health);
        }

        private static void Postfix(NewMovement __instance)
        {
            BloodlinkPlugin.Instance?.OnLocalPlayerVitalsChanged(__instance);
        }
    }

    [HarmonyPatch(typeof(NewMovement), "SuperCharge")]
    internal static class PlayerSuperChargeAuthorityPatch
    {
        private static bool Prefix(NewMovement __instance)
        {
            var plugin = BloodlinkPlugin.Instance;
            return plugin == null || !plugin.InterceptLocalPlayerSuperCharge(__instance);
        }

        private static void Postfix(NewMovement __instance)
        {
            BloodlinkPlugin.Instance?.OnLocalPlayerVitalsChanged(__instance);
        }
    }
}
