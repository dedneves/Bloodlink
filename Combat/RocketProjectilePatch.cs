using HarmonyLib;
using ULTRAKILL.Bloodlink.Core;
using UnityEngine;

namespace ULTRAKILL.Bloodlink.Combat
{
    [HarmonyPatch(typeof(Grenade), "Start")]
    internal static class RocketProjectilePatch
    {
        private static void Postfix(Grenade __instance)
        {
            BloodlinkPlugin.Instance?.NotifyLocalRocketSpawn(__instance);
        }
    }

    [HarmonyPatch(typeof(Cannonball), "Start")]
    internal static class CannonballProjectilePatch
    {
        private static void Postfix(Cannonball __instance)
        {
            BloodlinkPlugin.Instance?.NotifyLocalCannonballSpawn(__instance);
        }
    }

    [HarmonyPatch(typeof(GasolineProjectile), "Start")]
    internal static class NapalmProjectilePatch
    {
        private static void Postfix(GasolineProjectile __instance)
        {
            BloodlinkPlugin.Instance?.NotifyLocalNapalmSpawn(__instance);
        }
    }
}
