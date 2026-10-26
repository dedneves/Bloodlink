using HarmonyLib;
using ULTRAKILL.Bloodlink.Core;
using ULTRAKILL.Bloodlink.Levels;
using ULTRAKILL.Bloodlink.Networking;
using UnityEngine;

namespace ULTRAKILL.Bloodlink.Combat
{
    [HarmonyPatch(typeof(UnityEngine.Object), nameof(UnityEngine.Object.Destroy), new[] { typeof(UnityEngine.Object) })]
    internal static class GenericSceneObjectDestroySyncPatch
    {
        private static void Prefix(UnityEngine.Object obj, out SceneObjectState __state)
        {
            __state = null;
            var plugin = BloodlinkPlugin.Instance;
            if (plugin == null || !plugin.IsConnected || SceneObjectStateSync.IsApplyingRemote ||
                WorldObjectSync.IsApplyingRemote || WorldObjectSync.IsRunningNativeMutation) return;
            var gameObject = obj as GameObject;
            if (gameObject != null)
                __state = plugin.CaptureSceneObjectState(gameObject, SceneObjectOperation.Destroy, false);
            else
            {
                var component = obj as Component;
                if (component != null) __state = plugin.CaptureSceneObjectComponent(component);
            }
        }

        private static void Postfix(SceneObjectState __state)
        {
            if (__state == null) return;
            var plugin = BloodlinkPlugin.Instance;
            if (plugin != null && plugin.IsConnected) plugin.CommitSceneObjectState(__state);
        }
    }

    [HarmonyPatch(typeof(Breakable), nameof(Breakable.Break), new[] { typeof(float) })]
    internal static class BreakableSyncPatch
    {
        private static bool Prefix(Breakable __instance, float damage, out WorldMutation __state)
        {
            __state = null;
            if (WorldObjectSync.IsApplyingRemote || WorldObjectSync.IsRunningNativeMutation || SceneObjectStateSync.IsApplyingRemote) return true;
            var plugin = BloodlinkPlugin.Instance;
            if (plugin == null || !plugin.IsConnected) return true;
            __state = plugin.CaptureBreakableMutation(__instance, damage);
            if (__state != null) WorldObjectSync.BeginNativeMutation();
            return true;
        }

        private static void Postfix(WorldMutation __state)
        {
            var plugin = BloodlinkPlugin.Instance;
            if (plugin != null && plugin.IsConnected && __state != null)
                plugin.CommitCapturedWorldMutation(__state);
        }

        private static System.Exception Finalizer(System.Exception __exception, WorldMutation __state)
        { if (__state != null) WorldObjectSync.EndNativeMutation(); return __exception; }
    }

    [HarmonyPatch(typeof(Breakable), nameof(Breakable.ForceBreak))]
    internal static class ForceBreakSyncPatch
    {
        private static void Prefix(Breakable __instance, out WorldMutation __state)
        {
            __state = null;
            if (WorldObjectSync.IsApplyingRemote || WorldObjectSync.IsRunningNativeMutation || SceneObjectStateSync.IsApplyingRemote) return;
            var plugin = BloodlinkPlugin.Instance;
            if (plugin == null || !plugin.IsConnected) return;
            __state = plugin.CaptureBreakableMutation(__instance, 99999f);
            if (__state != null) WorldObjectSync.BeginNativeMutation();
        }

        private static void Postfix(WorldMutation __state)
        {
            if (__state != null) BloodlinkPlugin.Instance?.CommitCapturedWorldMutation(__state);
        }

        private static System.Exception Finalizer(System.Exception __exception, WorldMutation __state)
        { if (__state != null) WorldObjectSync.EndNativeMutation(); return __exception; }
    }

    [HarmonyPatch(typeof(Glass), nameof(Glass.Shatter))]
    internal static class GlassSyncPatch
    {
        private static bool Prefix(Glass __instance, out WorldMutation __state)
        {
            __state = null;
            if (WorldObjectSync.IsApplyingRemote || WorldObjectSync.IsRunningNativeMutation || SceneObjectStateSync.IsApplyingRemote) return true;
            var plugin = BloodlinkPlugin.Instance;
            if (plugin == null || !plugin.IsConnected) return true;
            __state = plugin.CaptureGlassMutation(__instance);
            if (__state != null) WorldObjectSync.BeginNativeMutation();
            return true;
        }

        private static void Postfix(WorldMutation __state)
        {
            var plugin = BloodlinkPlugin.Instance;
            if (plugin != null && plugin.IsConnected && __state != null)
                plugin.CommitCapturedWorldMutation(__state);
        }

        private static System.Exception Finalizer(System.Exception __exception, WorldMutation __state)
        { if (__state != null) WorldObjectSync.EndNativeMutation(); return __exception; }
    }
}
