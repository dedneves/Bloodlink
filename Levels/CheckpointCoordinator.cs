using System.Collections.Generic;
using HarmonyLib;
using ULTRAKILL.Bloodlink.Core;
using ULTRAKILL.Bloodlink.Networking;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ULTRAKILL.Bloodlink.Levels
{
    [HarmonyPatch(typeof(CheckPoint), nameof(CheckPoint.ActivateCheckPoint))]
    internal static class CheckpointActivationPatch
    {
        private static bool applyingHostCommit;

        private static bool Prefix(CheckPoint __instance)
        {
            var plugin = BloodlinkPlugin.Instance;
            if (plugin == null || !plugin.IsConnected || plugin.IsHost || applyingHostCommit) return true;
            plugin.RequestCheckpoint(__instance);
            return false;
        }

        private static void Postfix(CheckPoint __instance)
        {
            var plugin = BloodlinkPlugin.Instance;
            if (plugin == null || !plugin.IsHost || !plugin.IsConnected || applyingHostCommit) return;
            plugin.BroadcastCheckpointCommit(__instance);
        }

        internal static void ApplyHostCommit(CheckPoint checkpoint)
        {
            if (checkpoint == null || checkpoint.activated) return;
            applyingHostCommit = true;
            try { checkpoint.ActivateCheckPoint(); }
            finally { applyingHostCommit = false; }
        }
    }

    internal static class CheckpointCoordinator
    {
        private static readonly CheckpointSessionState sessionState = new CheckpointSessionState();
        private static CheckPoint pendingRequest;
        private static float nextRequestAt;
        private static CheckpointEventData pendingCommit;
        private static float nextCommitAttemptAt;
        private static CheckPoint predictedCheckpoint;
        private static CheckPoint previousCheckpoint;
        private static bool predictionWasActivated;
        private static bool predictionGraphicVisible;
        private static float predictionUntil;

        internal static void OnSceneLoaded(string sceneName)
        {
            if (string.IsNullOrWhiteSpace(sceneName) || sceneName == "DontDestroyOnLoad") return;
            sessionState.BeginScene(sceneName);
            ClearPrediction();
            pendingRequest = null;
            pendingCommit = null;
            nextRequestAt = 0f;
            nextCommitAttemptAt = 0f;
        }

        internal static void RecordCommit(string path, string sceneName)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(sceneName)) return;
            sessionState.RecordCommit(sceneName, path);
        }

        internal static bool TryGetCurrentCheckpoint(string sceneName, out string path)
        {
            return sessionState.TryGetForScene(sceneName, out path);
        }

        internal static string GetStablePath(CheckPoint checkpoint)
        {
            if (checkpoint == null) return null;
            string path;
            return SceneObjectIdentity.TryBuildPath(checkpoint.transform, out path) ? path : null;
        }

        internal static CheckPoint FindByStablePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (path.IndexOf(':') >= 0)
                return SceneObjectIdentity.Resolve<CheckPoint>(SceneManager.GetActiveScene().name, path, 0);
            var segments = path.Split('/');
            var indices = new int[segments.Length];
            for (var i = 0; i < segments.Length; i++)
                if (!int.TryParse(segments[i], out indices[i]) || indices[i] < 0) return null;

            for (var sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
            {
                var scene = SceneManager.GetSceneAt(sceneIndex);
                if (!scene.isLoaded || scene != SceneManager.GetActiveScene()) continue;
                var roots = scene.GetRootGameObjects();
                if (indices[0] >= roots.Length) continue;
                var current = roots[indices[0]].transform;
                for (var segment = 1; segment < indices.Length && current != null; segment++)
                {
                    if (indices[segment] >= current.childCount) { current = null; break; }
                    current = current.GetChild(indices[segment]);
                }
                if (current == null) continue;
                var checkpoint = current.GetComponent<CheckPoint>();
                if (checkpoint != null) return checkpoint;
            }
            return null;
        }

        internal static CheckPoint Resolve(string path, Vector3? position)
        {
            var exact = FindByStablePath(path);
            if (exact != null && exact.gameObject.activeInHierarchy &&
                (!position.HasValue || (exact.transform.position - position.Value).sqrMagnitude < 16f)) return exact;
            if (!position.HasValue) return null;
            CheckPoint nearest = null;
            var distance = 4f;
            foreach (var candidate in Resources.FindObjectsOfTypeAll<CheckPoint>())
            {
                if (candidate == null || !candidate.gameObject.activeInHierarchy || candidate.gameObject.scene != SceneManager.GetActiveScene()) continue;
                var squared = (candidate.transform.position - position.Value).sqrMagnitude;
                if (squared < distance) { distance = squared; nearest = candidate; }
            }
            return nearest;
        }

        internal static void Request(CheckPoint checkpoint)
        {
            if (pendingRequest == checkpoint) return;
            pendingRequest = checkpoint;
            nextRequestAt = 0f;
            var plugin = BloodlinkPlugin.Instance;
            if (checkpoint == null || plugin == null || plugin.IsHost || !plugin.IsConnected) return;
            ClearPrediction();
            predictedCheckpoint = checkpoint;
            predictionWasActivated = checkpoint.activated;
            predictionGraphicVisible = checkpoint.graphic != null && checkpoint.graphic.activeSelf;
            var stats = MonoSingleton<StatsManager>.Instance;
            previousCheckpoint = stats != null ? stats.currentCheckPoint : null;
            if (stats != null) stats.currentCheckPoint = checkpoint;
            checkpoint.activated = true;
            if (checkpoint.graphic != null) checkpoint.graphic.SetActive(false);
            predictionUntil = Time.unscaledTime + Mathf.Max(3f, plugin.Network.PingMilliseconds * 0.004f);
            SendPendingRequest(plugin);
            nextRequestAt = Time.unscaledTime + 0.2f;
        }

        internal static void Update()
        {
            var plugin = BloodlinkPlugin.Instance;
            if (plugin == null || !plugin.IsConnected || plugin.IsHost) { ClearPrediction(); pendingRequest = null; pendingCommit = null; return; }
            if (predictedCheckpoint != null && Time.unscaledTime >= predictionUntil) ClearPrediction();
            if (!plugin.Network.SceneSnapshotSyncReady || !string.IsNullOrEmpty(SceneHelper.PendingScene)) return;
            if (pendingCommit != null && Time.unscaledTime >= nextCommitAttemptAt) ApplyCommit(pendingCommit);
            if (pendingRequest == null || Time.unscaledTime < nextRequestAt) return;
            nextRequestAt = Time.unscaledTime + 0.2f;
            SendPendingRequest(plugin);
        }

        private static void SendPendingRequest(BloodlinkPlugin plugin)
        {
            if (pendingRequest == null) return;
            var path = GetStablePath(pendingRequest);
            if (path != null) plugin.Network.SendToHost(MessageType.CheckpointEvent,
                CheckpointEventCodec.Encode(CheckpointEventKind.Request, path, pendingRequest.transform.position));
        }

        internal static void ApplyCommit(CheckpointEventData data)
        {
            nextCommitAttemptAt = Time.unscaledTime + 0.25f;
            var plugin = BloodlinkPlugin.Instance;
            if (plugin == null || !plugin.Network.SceneSnapshotSyncReady || !string.IsNullOrEmpty(SceneHelper.PendingScene))
            { pendingCommit = data; return; }
            var checkpoint = Resolve(data.Path, data.HasPosition ? (Vector3?)data.Position : null);
            if (checkpoint == null) { pendingCommit = data; return; }
            pendingCommit = null;
            ClearPrediction();
            pendingRequest = null;
            RecordCommit(GetStablePath(checkpoint), SceneHelper.CurrentScene);
            CheckpointActivationPatch.ApplyHostCommit(checkpoint);
            var stats = MonoSingleton<StatsManager>.Instance;
            if (stats != null) stats.currentCheckPoint = checkpoint;
        }

        private static void ClearPrediction()
        {
            if (predictedCheckpoint == null) { predictedCheckpoint = null; previousCheckpoint = null; return; }
            predictedCheckpoint.activated = predictionWasActivated;
            if (predictedCheckpoint.graphic != null) predictedCheckpoint.graphic.SetActive(predictionGraphicVisible);
            var stats = MonoSingleton<StatsManager>.Instance;
            if (stats != null && stats.currentCheckPoint == predictedCheckpoint) stats.currentCheckPoint = previousCheckpoint;
            predictedCheckpoint = null;
            previousCheckpoint = null;
        }

        internal static CheckPoint GetCurrentCheckpoint(string sceneName)
        {
            // Native checkpoint references survive sibling insertions/removals
            // when room templates are cloned; the old numeric path may not.
            var stats = MonoSingleton<StatsManager>.Instance;
            if (stats != null && stats.currentCheckPoint != null &&
                stats.currentCheckPoint.gameObject.activeInHierarchy &&
                stats.currentCheckPoint.gameObject.scene == SceneManager.GetActiveScene() &&
                SceneObjectIdentity.IsCurrentScene(sceneName)) return stats.currentCheckPoint;
            string path;
            var saved = TryGetCurrentCheckpoint(sceneName, out path) ? FindByStablePath(path) : null;
            return saved != null && saved.gameObject.activeInHierarchy ? saved : null;
        }
    }
}
