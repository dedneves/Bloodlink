using HarmonyLib;
using ULTRAKILL.Bloodlink.Core;

namespace ULTRAKILL.Bloodlink.Combat
{
    [HarmonyPatch(typeof(Coin), "Start")]
    internal static class CoinProjectilePatch
    {
        private static void Postfix(Coin __instance)
        {
            BloodlinkPlugin.Instance?.NotifyLocalCoinSpawn(__instance);
        }
    }
}
