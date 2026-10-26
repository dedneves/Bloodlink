using HarmonyLib;
using ULTRAKILL.Bloodlink.Core;
using ULTRAKILL.Bloodlink.Levels;

namespace ULTRAKILL.Bloodlink.Combat
{
    [HarmonyPatch(typeof(Door), nameof(Door.Optimize))]
    internal static class CoopKeepRoomsLoadedPatch
    {
        private static bool Prefix()
        {
            var plugin = BloodlinkPlugin.Instance;
            return plugin == null || !plugin.IsConnected;
        }
    }

    [HarmonyPatch(typeof(Door), nameof(Door.Open))]
    internal static class DoorOpenSyncPatch
    {
        private static bool Prefix(Door __instance, bool enemy, bool skull)
        {
            if ((DoorStateSync.IsApplyingRemote || ItemSync.IsApplyingScenario)) return true;
            var plugin = BloodlinkPlugin.Instance;
            if (plugin == null || !plugin.IsConnected || plugin.IsHost) return true;
            plugin.RequestDoorOpen(__instance, enemy, skull);
            return !__instance.locked;
        }

        private static void Postfix(Door __instance, bool enemy, bool skull)
        {
            if ((DoorStateSync.IsApplyingRemote || ItemSync.IsApplyingScenario)) return;
            BloodlinkPlugin.Instance?.BroadcastDoorMutation(__instance, DoorMutation.Open, enemy, skull);
        }
    }

    [HarmonyPatch(typeof(Door), nameof(Door.Close))]
    internal static class DoorCloseSyncPatch
    {
        private static bool Prefix()
        {
            if ((DoorStateSync.IsApplyingRemote || ItemSync.IsApplyingScenario)) return true;
            var plugin = BloodlinkPlugin.Instance;
            return plugin == null || !plugin.IsConnected || plugin.IsHost;
        }

        private static void Postfix(Door __instance, bool force)
        {
            if ((DoorStateSync.IsApplyingRemote || ItemSync.IsApplyingScenario)) return;
            BloodlinkPlugin.Instance?.BroadcastDoorMutation(__instance, DoorMutation.Close, force: force);
        }
    }

    [HarmonyPatch(typeof(Door), nameof(Door.Lock))]
    internal static class DoorLockSyncPatch
    {
        private static bool Prefix()
        {
            if ((DoorStateSync.IsApplyingRemote || ItemSync.IsApplyingScenario)) return true;
            var plugin = BloodlinkPlugin.Instance;
            return plugin == null || !plugin.IsConnected || plugin.IsHost;
        }

        private static void Postfix(Door __instance)
        {
            if (!(DoorStateSync.IsApplyingRemote || ItemSync.IsApplyingScenario))
                BloodlinkPlugin.Instance?.BroadcastDoorMutation(__instance, DoorMutation.Lock);
        }
    }

    [HarmonyPatch(typeof(Door), nameof(Door.Unlock))]
    internal static class DoorUnlockSyncPatch
    {
        private static bool Prefix()
        {
            if ((DoorStateSync.IsApplyingRemote || ItemSync.IsApplyingScenario)) return true;
            var plugin = BloodlinkPlugin.Instance;
            return plugin == null || !plugin.IsConnected || plugin.IsHost;
        }

        private static void Postfix(Door __instance)
        {
            if (!(DoorStateSync.IsApplyingRemote || ItemSync.IsApplyingScenario))
                BloodlinkPlugin.Instance?.BroadcastDoorMutation(__instance, DoorMutation.Unlock);
        }
    }

    [HarmonyPatch(typeof(Door), nameof(Door.LockOpen))]
    internal static class DoorLockOpenSyncPatch
    {
        private static bool Prefix(Door __instance)
        {
            if ((DoorStateSync.IsApplyingRemote || ItemSync.IsApplyingScenario)) return true;
            var plugin = BloodlinkPlugin.Instance;
            if (plugin == null || !plugin.IsConnected) return true;
            if (!plugin.IsHost) return false;
            plugin.DoorStateSync.BeginCompoundMutation(__instance);
            return true;
        }

        private static void Postfix(Door __instance)
        {
            if ((DoorStateSync.IsApplyingRemote || ItemSync.IsApplyingScenario)) return;
            var plugin = BloodlinkPlugin.Instance;
            if (plugin == null || !plugin.IsConnected || !plugin.IsHost) return;
            plugin.DoorStateSync.EndCompoundMutation(__instance);
            plugin.BroadcastDoorMutation(__instance, DoorMutation.LockOpen);
        }
    }

    [HarmonyPatch(typeof(Door), nameof(Door.LockClose))]
    internal static class DoorLockCloseSyncPatch
    {
        private static bool Prefix(Door __instance)
        {
            if ((DoorStateSync.IsApplyingRemote || ItemSync.IsApplyingScenario)) return true;
            var plugin = BloodlinkPlugin.Instance;
            if (plugin == null || !plugin.IsConnected) return true;
            if (!plugin.IsHost) return false;
            plugin.DoorStateSync.BeginCompoundMutation(__instance);
            return true;
        }

        private static void Postfix(Door __instance)
        {
            if ((DoorStateSync.IsApplyingRemote || ItemSync.IsApplyingScenario)) return;
            var plugin = BloodlinkPlugin.Instance;
            if (plugin == null || !plugin.IsConnected || !plugin.IsHost) return;
            plugin.DoorStateSync.EndCompoundMutation(__instance);
            plugin.BroadcastDoorMutation(__instance, DoorMutation.LockClose);
        }
    }
}
