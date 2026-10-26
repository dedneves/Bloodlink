using HarmonyLib;
using ULTRAKILL.Bloodlink.Core;

namespace ULTRAKILL.Bloodlink.Combat
{
    [HarmonyPatch(typeof(Nail), "Start")]
    internal static class NailProjectilePatch
    {
        private static void Postfix(Nail __instance)
        {
            BloodlinkPlugin.Instance?.NotifyLocalNailSpawn(__instance);
        }
    }
}
