using HarmonyLib;
using ULTRAKILL.Bloodlink.Core;
using ULTRAKILL.Bloodlink.Networking;
using UnityEngine;

namespace ULTRAKILL.Bloodlink.Combat
{
    [HarmonyPatch(typeof(PlayerAnimations), "Punch")]
    internal static class PlayerPunchAnimationPatch
    {
        private static void Postfix(float __0)
        {
            BloodlinkPlugin.Instance?.NotifyLocalPlayerAnimation(PlayerAnimationAction.Punch, __0);
        }
    }

    [HarmonyPatch(typeof(PlayerAnimations), "CoinToss")]
    internal static class PlayerCoinTossAnimationPatch
    {
        private static void Postfix()
        {
            BloodlinkPlugin.Instance?.NotifyLocalPlayerAnimation(PlayerAnimationAction.CoinToss, 1f);
        }
    }

    [HarmonyPatch(typeof(PlayerAnimations), "Jump")]
    internal static class PlayerJumpAnimationPatch
    {
        private static void Postfix()
        {
            BloodlinkPlugin.Instance?.NotifyLocalPlayerAnimation(PlayerAnimationAction.Jump, 1f);
        }
    }

    [HarmonyPatch(typeof(PlayerAnimations), "Land")]
    internal static class PlayerLandAnimationPatch
    {
        private static void Postfix(float __0)
        {
            BloodlinkPlugin.Instance?.NotifyLocalPlayerAnimation(PlayerAnimationAction.Land, Mathf.Clamp(__0, 0.25f, 2f));
        }
    }

    [HarmonyPatch(typeof(NewMovement), "Parry")]
    internal static class PlayerParryAnimationPatch
    {
        private static void Postfix(NewMovement __instance)
        {
            BloodlinkPlugin.Instance?.NotifyLocalParry(__instance);
        }
    }
}
