using System.Reflection;
using HarmonyLib;
using ULTRAKILL.Bloodlink.Core;
using ULTRAKILL.Bloodlink.Levels;
using ULTRAKILL.Bloodlink.Networking;
using UnityEngine;

namespace ULTRAKILL.Bloodlink.Combat
{
    [HarmonyPatch(typeof(Bonus), "OnTriggerEnter")]
    internal static class SecretBonusReplicationPatch
    {
        private static readonly FieldInfo ActivatedField = AccessTools.Field(typeof(Bonus), "activated");
        private static readonly MethodInfo OnTriggerEnterMethod = AccessTools.Method(typeof(Bonus), "OnTriggerEnter");
        private static SecretBonusLocation pendingHostPickup;
        private static bool applyingRemotePickup;

        internal static bool IsActivated(Bonus bonus)
        {
            return bonus != null && ActivatedField != null && (bool)ActivatedField.GetValue(bonus);
        }

        internal static bool ApplyRemotePickup(Bonus bonus, Collider playerCollider)
        {
            if (bonus == null || playerCollider == null || OnTriggerEnterMethod == null) return false;
            applyingRemotePickup = true;
            try { OnTriggerEnterMethod.Invoke(bonus, new object[] { playerCollider }); }
            finally { applyingRemotePickup = false; }
            return IsActivated(bonus) || (object)bonus == null;
        }

        private static bool Prefix(Bonus __instance, Collider other, out bool __state)
        {
            __state = false;
            pendingHostPickup = null;
            var plugin = BloodlinkPlugin.Instance;
            if (plugin == null || !plugin.IsConnected || __instance == null || __instance.ghost || __instance.superCharge ||
                __instance.secretNumber < 0 || other == null || !other.CompareTag("Player")) return true;
            __state = IsActivated(__instance);
            if (__state || applyingRemotePickup) return true;
            if (!plugin.IsHost)
            {
                plugin.RequestSecretBonusPickup(__instance);
                return false;
            }
            string sceneName;
            string objectPath;
            byte componentIndex;
            if (!SceneObjectIdentity.TryBuild(__instance, out sceneName, out objectPath, out componentIndex)) return true;
            pendingHostPickup = new SecretBonusLocation(sceneName, objectPath, componentIndex, __instance.secretNumber);
            return true;
        }

        private static void Postfix(Bonus __instance, bool __state)
        {
            var identity = pendingHostPickup;
            pendingHostPickup = null;
            var plugin = BloodlinkPlugin.Instance;
            if (plugin == null || !plugin.IsHost || !plugin.IsConnected || __state || applyingRemotePickup || identity == null) return;
            if (!IsActivated(__instance) && (object)__instance != null) return;
            plugin.NotifyHostSecretBonusPickedUp(identity);
        }
    }
}
