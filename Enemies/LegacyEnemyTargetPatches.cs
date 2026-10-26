using HarmonyLib;
using ULTRAKILL.Bloodlink.Core;
using UnityEngine;

namespace ULTRAKILL.Bloodlink.Enemies
{
    internal static class LegacyEnemyTargetSelection
    {
        private static uint targetChoice;

        public static void ReplaceWithRemoteTarget(ref EnemyTarget current)
        {
            var plugin = BloodlinkPlugin.Instance;
            if (current == null || plugin == null || !plugin.IsConnected || !plugin.IsHost) return;

            unchecked { targetChoice++; }
            if ((targetChoice & 1) != 0) return;
            RemotePlayerTargetProxy proxy;
            if (!RemotePlayerTargetProxy.TryGetNextLegacyTarget(out proxy) || proxy == null) return;

            var remote = new EnemyTarget(proxy.transform);
            remote.isPlayer = true;
            current = remote;
        }
    }

    [HarmonyPatch(typeof(EnemyTarget), "TrackPlayerIfAllowed")]
    internal static class TrackPlayerIfAllowedPatch
    {
        private static void Postfix(ref EnemyTarget __result)
        {
            LegacyEnemyTargetSelection.ReplaceWithRemoteTarget(ref __result);
        }
    }

    [HarmonyPatch(typeof(EnemyTarget), "TrackPlayer")]
    internal static class TrackPlayerPatch
    {
        private static void Postfix(ref EnemyTarget __result)
        {
            LegacyEnemyTargetSelection.ReplaceWithRemoteTarget(ref __result);
        }
    }

    [HarmonyPatch(typeof(EnemyTarget), "GetVelocity")]
    internal static class EnemyTargetVelocityPatch
    {
        private static bool Prefix(EnemyTarget __instance, ref Vector3 __result)
        {
            if (__instance == null || !__instance.isPlayer || __instance.targetTransform == null) return true;
            var proxy = __instance.targetTransform.GetComponent<RemotePlayerTargetProxy>();
            if (proxy == null) return true;
            __result = proxy.Velocity;
            return false;
        }
    }

    [HarmonyPatch(typeof(EnemyTarget), "get_headTransform")]
    internal static class EnemyTargetHeadTransformPatch
    {
        private static bool Prefix(EnemyTarget __instance, ref Transform __result)
        {
            if (__instance == null || !__instance.isPlayer || __instance.targetTransform == null) return true;
            var proxy = __instance.targetTransform.GetComponent<RemotePlayerTargetProxy>();
            if (proxy == null) return true;
            __result = proxy.LegacyHeadTransform;
            return false;
        }
    }

    [HarmonyPatch(typeof(EnemyTarget), "get_isOnGround")]
    internal static class EnemyTargetGroundedPatch
    {
        private static bool Prefix(EnemyTarget __instance, ref bool __result)
        {
            if (__instance == null || !__instance.isPlayer || __instance.targetTransform == null) return true;
            var proxy = __instance.targetTransform.GetComponent<RemotePlayerTargetProxy>();
            if (proxy == null) return true;
            __result = proxy.IsGrounded;
            return false;
        }
    }

    [HarmonyPatch(typeof(EnemyTarget), "IsTargetTransform")]
    internal static class EnemyTargetTransformPatch
    {
        private static bool Prefix(EnemyTarget __instance, Transform other, ref bool __result)
        {
            if (__instance == null || !__instance.isPlayer || __instance.targetTransform == null) return true;
            var proxy = __instance.targetTransform.GetComponent<RemotePlayerTargetProxy>();
            if (proxy == null) return true;
            __result = other == proxy.transform;
            return false;
        }
    }
}
