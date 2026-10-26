using HarmonyLib;
using ULTRAKILL.Bloodlink.Core;
using ULTRAKILL.Bloodlink.Networking;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ULTRAKILL.Bloodlink.Levels
{
    [HarmonyPatch(typeof(SceneHelper), nameof(SceneHelper.LoadSceneAsync))]
    internal static class SceneLoadPatch
    {
        private static readonly LevelReadinessBarrier readiness = new LevelReadinessBarrier();
        private static Coroutine transitionLoadCoroutine;
        private static bool allowSceneLoad;
        private static bool forceNextReload;
        private static uint nextTransitionId;
        private static uint activeTransitionId;
        private static string activeSceneName;
        private static string hostPreviousSceneName;
        private static string clientPreviousSceneName;
        private static bool hostTransitionActive;
        private static bool joinOnlyBarrier;
        private static bool activeForceReload;
        private static bool hostSceneLoadObserved;
        private static bool clientTransitionActive;
        private static bool clientSceneReady;
        private static bool clientReadyOverlayDismissed;
        private static bool clientStartReceived;
        private static bool clientSceneLoadObserved;
        private static bool recoveryTransitionActive;
        private static string recoverySceneName;
        private static string snapshotRecoveryScene;
        private static byte clientReadyPlayerCount;
        private static byte clientTotalPlayerCount;
        private static uint[] clientReadyPlayerIds = new uint[0];
        private static float nextClientReadyRetryAt;
        private static bool timeScaleSaved;
        private static float savedTimeScale = 1f;

        internal static void ResetSession()
        {
            var plugin = BloodlinkPlugin.Instance;
            var rollbackScene = plugin != null && plugin.IsHost ? hostPreviousSceneName : clientPreviousSceneName;
            var hadUnstartedTransition = (hostTransitionActive && !joinOnlyBarrier && !readiness.IsStarted) || clientTransitionActive;
            var hadPendingLoad = CancelPendingSceneLoad(plugin);
            var shouldRestorePreviousScene = hadPendingLoad || hadUnstartedTransition;
            snapshotRecoveryScene = shouldRestorePreviousScene ? rollbackScene : null;
            if (plugin != null) plugin.Network.SetLocalSnapshotsDuringBarrier(false);
            if (plugin != null) plugin.SetSceneSnapshotSyncReady(!shouldRestorePreviousScene);
            SessionDifficultyOverride.Clear();
            RestoreTimeScale();
            CheckpointCoordinator.OnSceneLoaded(SceneHelper.CurrentScene);
            LevelProgressionCoordinator.Reset();
            nextTransitionId = 0;
            activeTransitionId = 0;
            activeSceneName = null;
            hostPreviousSceneName = null;
            clientPreviousSceneName = null;
            activeForceReload = false;
            forceNextReload = false;
            hostTransitionActive = false;
            joinOnlyBarrier = false;
            hostSceneLoadObserved = false;
            clientTransitionActive = false;
            clientSceneReady = false;
            clientReadyOverlayDismissed = false;
            clientStartReceived = false;
            clientSceneLoadObserved = false;
            recoveryTransitionActive = false;
            recoverySceneName = null;
            clientReadyPlayerCount = 0;
            clientTotalPlayerCount = 0;
            clientReadyPlayerIds = new uint[0];
            nextClientReadyRetryAt = 0f;
            readiness.Reset();
            allowSceneLoad = false;
            if (shouldRestorePreviousScene)
                RestorePreviousScene(plugin, rollbackScene, plugin != null && plugin.IsHost ? "host" : "client");
        }

        private static void Postfix(Coroutine __result)
        {
            if (__result != null && (hostTransitionActive || clientTransitionActive))
                transitionLoadCoroutine = __result;
        }

        private static bool CancelPendingSceneLoad(BloodlinkPlugin plugin)
        {
            if (string.IsNullOrEmpty(SceneHelper.PendingScene))
            {
                transitionLoadCoroutine = null;
                return false;
            }

            try
            {
                var sceneHelper = MonoSingleton<SceneHelper>.Instance;
                if (sceneHelper != null && transitionLoadCoroutine != null)
                    sceneHelper.StopCoroutine(transitionLoadCoroutine);

                transitionLoadCoroutine = null;
                var pendingSceneProperty = typeof(SceneHelper).GetProperty(nameof(SceneHelper.PendingScene));
                var pendingSceneSetter = pendingSceneProperty != null ? pendingSceneProperty.GetSetMethod(true) : null;
                if (pendingSceneSetter != null) pendingSceneSetter.Invoke(null, new object[] { null });
                SceneHelper.DismissBlockers();
                return true;
            }
            catch (System.Exception ex)
            {
                if (plugin != null) plugin.LogLevelTransition("Could not cancel the pending scene load cleanly: " + ex.Message);
                return false;
            }
        }

        internal static bool IsClientWaitingForStart { get { return clientTransitionActive && !clientStartReceived; } }
        internal static bool IsTransitionInProgress
        {
            get { return (hostTransitionActive && !readiness.IsStarted) || clientTransitionActive || !string.IsNullOrEmpty(SceneHelper.PendingScene); }
        }
        internal static bool ShowLoadingOverlay
        {
            get { return (hostTransitionActive && !readiness.IsStarted) ||
                (clientTransitionActive && !clientReadyOverlayDismissed) || recoveryTransitionActive; }
        }
        internal static string LoadingStatus
        {
            get
            {
                if (recoveryTransitionActive)
                    return "RECOVERING AFTER A LEVEL LOAD FAILURE\n" + (recoverySceneName ?? "Previous level") +
                        "\nRETURNING TO THE LAST WORKING LEVEL";
                if (hostTransitionActive && !readiness.IsStarted)
                    return (activeSceneName ?? "Unknown level") + "\n" +
                        (joinOnlyBarrier ? "SYNCHRONIZING JOINER" : readiness.IsHostReady
                            ? "WAITING FOR PLAYERS" : GetSceneLoadPhase(activeSceneName, hostSceneLoadObserved)) +
                        AppendSceneLoadDiagnostic(activeSceneName) +
                        "\nPLAYERS READY  " + ReadyPlayerCount() + " / " + TotalPlayerCount() +
                        "\nREADY: " + FormatReadyPlayerNames(readiness.GetReadyPlayerIds()) +
                        (joinOnlyBarrier || readiness.IsHostReady ? string.Empty : "\nCLICK READY WHEN THE LEVEL IS VISIBLE");
                if (clientTransitionActive)
                    return (activeSceneName ?? "Unknown level") + "\n" + (clientStartReceived
                        ? GetSceneLoadPhase(activeSceneName, clientSceneLoadObserved)
                        : clientSceneReady ? "READY - WAITING FOR HOST" :
                            GetSceneLoadPhase(activeSceneName, clientSceneLoadObserved)) +
                        AppendSceneLoadDiagnostic(activeSceneName) +
                        "\nPLAYERS READY  " + clientReadyPlayerCount + " / " + clientTotalPlayerCount +
                        "\nREADY: " + FormatReadyPlayerNames(clientReadyPlayerIds) +
                        (clientSceneReady ? string.Empty : "\nCLICK READY WHEN THE LEVEL IS VISIBLE");
                return string.Empty;
            }
        }

        private static bool Prefix(string sceneName, bool noBlocker, ref Coroutine __result)
        {
            var forceReload = forceNextReload;
            forceNextReload = false;
            var plugin = BloodlinkPlugin.Instance;
            if (plugin == null || !plugin.IsConnected) return true;
            if (allowSceneLoad)
            {
                allowSceneLoad = false;
                return true;
            }
            if (!plugin.IsHost)
            {
                __result = null;
                plugin.LogRejectedClientTransition(sceneName);
                return false;
            }
            if ((hostTransitionActive && !readiness.IsStarted) || !string.IsNullOrEmpty(SceneHelper.PendingScene))
            {
                __result = null;
                plugin.SetLevelTransitionStatus("Another level transition is still in progress.");
                plugin.LogLevelTransition("Ignored overlapping host scene request for " + sceneName + ".");
                return false;
            }

            unchecked { nextTransitionId++; }
            if (nextTransitionId == 0) nextTransitionId = 1;
            activeTransitionId = nextTransitionId;
            hostPreviousSceneName = SceneHelper.CurrentScene ?? SceneManager.GetActiveScene().name;
            activeSceneName = sceneName;
            activeForceReload = forceReload;
            hostTransitionActive = true;
            joinOnlyBarrier = false;
            hostSceneLoadObserved = false;
            snapshotRecoveryScene = null;
            plugin.SetSceneSnapshotSyncReady(false);
            readiness.Begin(activeTransitionId, sceneName, plugin.Network.Players.Keys, plugin.Network.LocalPlayerId);
            plugin.Network.BroadcastReliable(MessageType.LevelTransition,
                LevelTransitionCodec.Encode(activeTransitionId, LevelTransitionStage.Prepare, sceneName, activeForceReload,
                    ReadyPlayerCount(), TotalPlayerCount(), readiness.GetReadyPlayerIds(), (byte)plugin.SessionDifficulty));
            plugin.LogLevelTransition("Preparing level " + activeTransitionId + ": " + sceneName);
            return true;
        }

        internal static void ReceiveHostTransition(uint senderId, byte[] payload)
        {
            var plugin = BloodlinkPlugin.Instance;
            if (plugin == null || plugin.IsHost || !plugin.IsConnected || senderId != 1) return;
            var transition = LevelTransitionCodec.Decode(payload);
            if (transition.Stage == LevelTransitionStage.Prepare)
            {
                BeginClientTransition(plugin, transition);
                return;
            }
            if (transition.Stage == LevelTransitionStage.Abort)
            {
                if (!clientTransitionActive || transition.TransitionId != activeTransitionId ||
                    !string.Equals(transition.SceneName, activeSceneName, System.StringComparison.Ordinal)) return;
                var rollbackScene = clientPreviousSceneName;
                var hadPendingLoad = CancelPendingSceneLoad(plugin);
                plugin.Network.SetLocalSnapshotsDuringBarrier(false);
                clientTransitionActive = false;
                clientSceneReady = false;
                clientReadyOverlayDismissed = false;
                clientStartReceived = false;
                clientSceneLoadObserved = false;
                clientPreviousSceneName = null;
                RestoreTimeScale();
                RestorePreviousScene(plugin, rollbackScene, "client", hadPendingLoad);
                DeferSnapshotResume(plugin, rollbackScene);
                plugin.SetLevelTransitionStatus("Host cancelled the level change; restored the previous level.");
                plugin.LogLevelTransition("Host cancelled transition " + transition.TransitionId + "; restored the previous level.");
                return;
            }
            if (transition.Stage == LevelTransitionStage.Progress)
            {
                if (!clientTransitionActive || transition.TransitionId != activeTransitionId ||
                    !string.Equals(transition.SceneName, activeSceneName, System.StringComparison.Ordinal)) return;
                clientReadyPlayerCount = transition.ReadyPlayerCount;
                clientTotalPlayerCount = transition.TotalPlayerCount;
                clientReadyPlayerIds = transition.ReadyPlayerIds;
                SessionDifficultyOverride.Apply(transition.Difficulty);
                return;
            }
            if (transition.Stage != LevelTransitionStage.Start) return;
            if (!clientTransitionActive ||
                !string.Equals(transition.SceneName, activeSceneName, System.StringComparison.Ordinal))
            {
                plugin.LogLevelTransition("Ignored START for transition " + transition.TransitionId + " (" + transition.SceneName +
                    "); client is waiting for " + activeTransitionId + " (" + (activeSceneName ?? "none") + ").");
                return;
            }

            if (transition.TransitionId != activeTransitionId)
                plugin.LogLevelTransition("Reconciling client transition " + activeTransitionId + " with host START " + transition.TransitionId + ".");
            activeTransitionId = transition.TransitionId;
            activeForceReload = transition.ForceReload;
            clientStartReceived = true;
            SessionDifficultyOverride.Apply(transition.Difficulty);
            clientReadyPlayerCount = transition.ReadyPlayerCount;
            clientTotalPlayerCount = transition.TotalPlayerCount;
            clientReadyPlayerIds = transition.ReadyPlayerIds;
            // START is sent only after the host has accepted this player's READY.
            // Treat it as the authoritative acknowledgement even if a duplicate
            // PREPARE retry briefly failed to send from this client.
            clientSceneReady = true;
            if (IsTargetSceneFullyLoaded(activeSceneName)) CompleteClientTransition(plugin);
            else plugin.LogLevelTransition("Host authorized transition " + transition.TransitionId + "; waiting for local scene load.");
        }

        private static void BeginClientTransition(BloodlinkPlugin plugin, LevelTransition transition)
        {
            SessionDifficultyOverride.Apply(transition.Difficulty);
            if (clientTransitionActive && transition.TransitionId == activeTransitionId &&
                string.Equals(activeSceneName, transition.SceneName, System.StringComparison.Ordinal))
            {
                // PREPARE is authoritative for whether this request needs a reload.
                // START/READY are matched by transition id and scene, not this option.
                activeForceReload = transition.ForceReload;
                if (clientSceneReady) SendClientReady(plugin, false);
                return;
            }

            activeTransitionId = transition.TransitionId;
            activeSceneName = transition.SceneName;
            activeForceReload = transition.ForceReload;
            clientPreviousSceneName = SceneHelper.CurrentScene ?? SceneManager.GetActiveScene().name;
            clientTransitionActive = true;
            clientSceneReady = false;
            clientReadyOverlayDismissed = false;
            plugin.Network.SetLocalSnapshotsDuringBarrier(false);
            clientStartReceived = false;
            nextClientReadyRetryAt = 0f;
            clientSceneLoadObserved = false;
            snapshotRecoveryScene = null;
            plugin.SetSceneSnapshotSyncReady(false);
            clientReadyPlayerCount = transition.ReadyPlayerCount;
            clientTotalPlayerCount = transition.TotalPlayerCount;
            clientReadyPlayerIds = transition.ReadyPlayerIds;
            if (!transition.ForceReload && IsTargetSceneFullyLoaded(transition.SceneName))
            {
                clientSceneLoadObserved = true;
                plugin.LogLevelTransition("The requested scene is already active. Waiting for the client to press READY.");
                return;
            }

            if (!string.IsNullOrEmpty(SceneHelper.PendingScene))
            {
                plugin.LogLevelTransition("Waiting for the existing scene load to finish before preparing transition " + transition.TransitionId + ".");
                return;
            }

            allowSceneLoad = true;
            try { SceneHelper.LoadSceneAsync(transition.SceneName); }
            finally { allowSceneLoad = false; }
            plugin.LogLevelTransition("Loading prepared level " + transition.TransitionId + ": " + transition.SceneName);
        }

        internal static void OnSceneLoaded(string sceneName)
        {
            var plugin = BloodlinkPlugin.Instance;
            if (recoveryTransitionActive && string.Equals(sceneName, recoverySceneName, System.StringComparison.Ordinal))
            {
                recoveryTransitionActive = false;
                recoverySceneName = null;
                if (plugin != null) plugin.SetLevelTransitionStatus("Returned to the previous level.");
            }
            if (plugin == null || !plugin.IsConnected || !string.Equals(sceneName, activeSceneName, System.StringComparison.Ordinal)) return;

            if (plugin.IsHost && hostTransitionActive && !hostSceneLoadObserved)
            {
                hostSceneLoadObserved = true;
                plugin.LogLevelTransition("Host scene activated. Host readiness requires a manual READY confirmation.");
            }
            else if (!plugin.IsHost && clientTransitionActive && !clientSceneLoadObserved)
            {
                clientSceneLoadObserved = true;
                plugin.LogLevelTransition("Client scene activated. Client readiness requires a manual READY confirmation.");
            }
        }

        private static bool IsTargetSceneFullyLoaded(string sceneName)
        {
            var active = SceneManager.GetActiveScene();
            return LevelSceneReadinessPolicy.IsFullyLoaded(sceneName, SceneHelper.CurrentScene,
                SceneHelper.PendingScene, active.IsValid(), active.isLoaded);
        }

        private static bool IsCampaignLevel(string sceneName)
        {
            return !string.IsNullOrEmpty(sceneName) && sceneName.StartsWith("Level ", System.StringComparison.Ordinal);
        }

        private static string GetSceneLoadPhase(string targetScene, bool sceneObserved)
        {
            var pending = SceneHelper.PendingScene;
            if (!string.IsNullOrEmpty(pending))
                return string.Equals(pending, targetScene, System.StringComparison.Ordinal)
                    ? sceneObserved ? "FINISHING LEVEL SETUP" : "LOADING LEVEL"
                    : "WAITING FOR SCENE LOAD: " + pending;
            if (!IsTargetSceneFullyLoaded(targetScene)) return "WAITING FOR SCENE ACTIVATION";
            var plugin = BloodlinkPlugin.Instance;
            if (IsCampaignLevel(targetScene) && (plugin == null || !plugin.HasActivePlayerMovement))
                return "WAITING FOR PLAYER SPAWN";
            return "FINALIZING LEVEL SETUP";
        }

        private static string AppendSceneLoadDiagnostic(string targetScene)
        {
            if (!string.IsNullOrEmpty(SceneHelper.PendingScene))
                return "\nSCENE LOAD: " + SceneHelper.PendingScene;

            var current = SceneHelper.CurrentScene ?? "unset";
            var activeScene = SceneManager.GetActiveScene();
            var active = activeScene.name ?? "unset";
            if (!string.Equals(current, targetScene, System.StringComparison.Ordinal) ||
                !activeScene.IsValid() || !activeScene.isLoaded)
                return "\nSCENE STATE: game=" + current + " / active=" + active;
            return "\nPOSITION SYNC: " + ((BloodlinkPlugin.Instance != null && BloodlinkPlugin.Instance.Network.SceneSnapshotSyncReady)
                ? "ACTIVE" : "PAUSED DURING LOAD");
        }

        internal static bool CanManuallyReady
        {
            get
            {
                var plugin = BloodlinkPlugin.Instance;
                if (plugin == null || !plugin.IsConnected) return false;
                // A READY packet is the host's authorization to begin live play.
                // Never let a client confirm while Unity still has the previous
                // scene active or a target scene load pending.
                if (!IsTargetSceneFullyLoaded(activeSceneName)) return false;
                if (plugin.IsHost) return hostTransitionActive && !joinOnlyBarrier && !readiness.IsStarted && !readiness.IsHostReady;
                return clientTransitionActive && !clientSceneReady;
            }
        }

        internal static bool IsLocallyReady
        {
            get
            {
                var plugin = BloodlinkPlugin.Instance;
                if (plugin == null) return false;
                return plugin.IsHost ? hostTransitionActive && !joinOnlyBarrier && !readiness.IsStarted && readiness.IsHostReady : clientTransitionActive && clientSceneReady;
            }
        }

        internal static string LocalReadyStatus
        {
            get
            {
                var plugin = BloodlinkPlugin.Instance;
                if (plugin == null || !IsLocallyReady) return string.Empty;
                if (plugin.IsHost) return "READY CONFIRMED - WAITING FOR THE PARTY";
                return clientStartReceived ? "HOST STARTED - FINISHING LEVEL LOAD" : "READY CONFIRMED - WAITING FOR HOST";
            }
        }

        internal static bool MarkLocalReady()
        {
            var plugin = BloodlinkPlugin.Instance;
            if (plugin == null || !plugin.IsConnected || !CanManuallyReady) return false;

            if (plugin.IsHost)
            {
                if (!readiness.MarkHostReady(activeTransitionId, activeSceneName)) return false;
                plugin.Network.SetLocalSnapshotsDuringBarrier(true);
                plugin.LogLevelTransition("Host manually confirmed READY for " + activeSceneName + ".");
                BroadcastReadinessProgress(plugin);
                TryStartHostTransition(plugin);
                return true;
            }

            clientSceneReady = true;
            if (!SendClientReady(plugin))
            {
                clientSceneReady = false;
                return false;
            }
            // READY dismisses this client's blocking UI immediately. START still
            // controls the host barrier and the authoritative scene transition.
            clientReadyOverlayDismissed = true;
            RestoreTimeScale();
            plugin.Network.SetLocalSnapshotsDuringBarrier(true);
            plugin.LogLevelTransition("Client manually confirmed READY for " + activeSceneName + ".");
            if (clientStartReceived && IsTargetSceneFullyLoaded(activeSceneName)) CompleteClientTransition(plugin);
            return true;
        }

        internal static bool CanCancelHostTransition
        {
            get { return hostTransitionActive && !readiness.IsStarted; }
        }

        internal static bool IsJoinOnlyBarrier { get { return joinOnlyBarrier; } }

        internal static void CancelHostTransitionByUser()
        {
            var plugin = BloodlinkPlugin.Instance;
            if (!CanCancelHostTransition || plugin == null || !plugin.IsHost) return;
            CancelHostTransition(plugin);
        }

        internal static void ReleaseOverlayAfterDisconnect(BloodlinkPlugin plugin)
        {
            hostTransitionActive = false;
            joinOnlyBarrier = false;
            readiness.Reset();
            clientTransitionActive = false;
            clientSceneReady = false;
            clientReadyOverlayDismissed = false;
            if (plugin != null) plugin.Network.SetLocalSnapshotsDuringBarrier(false);
            clientStartReceived = false;
            clientSceneLoadObserved = false;
            recoveryTransitionActive = false;
            recoverySceneName = null;
            snapshotRecoveryScene = null;
            activeSceneName = null;
            activeTransitionId = 0;
            nextClientReadyRetryAt = 0f;
            RestoreTimeScale();
            if (plugin != null) plugin.SetSceneSnapshotSyncReady(true);
        }

        private static void DeferSnapshotResume(BloodlinkPlugin plugin, string sceneName)
        {
            if (plugin == null) return;
            if (string.IsNullOrWhiteSpace(sceneName))
            {
                snapshotRecoveryScene = null;
                plugin.SetSceneSnapshotSyncReady(true);
                return;
            }
            snapshotRecoveryScene = sceneName;
        }

        internal static void ReceiveClientReady(uint senderId, byte[] payload)
        {
            var plugin = BloodlinkPlugin.Instance;
            if (plugin == null || !plugin.IsHost || !plugin.IsConnected || senderId == plugin.Network.LocalPlayerId ||
                !plugin.Network.Players.ContainsKey(senderId)) return;
            var transition = LevelTransitionCodec.Decode(payload);
            if (transition.Stage != LevelTransitionStage.Ready ||
                !string.Equals(transition.SceneName, activeSceneName, System.StringComparison.Ordinal)) return;

            if (readiness.IsStarted)
            {
                readiness.MarkPlayerReady(senderId, activeTransitionId, activeSceneName);
                plugin.SendCurrentCheckpointToPlayer(senderId, activeSceneName);
                plugin.SendInitialEnemySnapshot(senderId);
                plugin.Network.SendToPlayer(senderId, MessageType.LevelTransition,
                    LevelTransitionCodec.Encode(activeTransitionId, LevelTransitionStage.Start, activeSceneName, activeForceReload,
                        ReadyPlayerCount(), TotalPlayerCount(), readiness.GetReadyPlayerIds(), (byte)plugin.SessionDifficulty));
                return;
            }
            if (!readiness.MarkPlayerReady(senderId, activeTransitionId, activeSceneName)) return;
            BroadcastReadinessProgress(plugin);
            if (joinOnlyBarrier)
            {
                CompleteJoinOnlyBarrier(plugin);
                return;
            }
            TryStartHostTransition(plugin);
        }

        internal static bool SendPrepareToJoiningPlayer(uint playerId)
        {
            var plugin = BloodlinkPlugin.Instance;
            if (plugin == null || !plugin.IsHost || !plugin.IsConnected) return false;
            if (!hostTransitionActive)
            {
                var sceneName = SceneHelper.CurrentScene;
                if (string.IsNullOrWhiteSpace(sceneName)) return false;
                unchecked { nextTransitionId++; }
                if (nextTransitionId == 0) nextTransitionId = 1;
                activeTransitionId = nextTransitionId;
                activeSceneName = sceneName;
                hostPreviousSceneName = sceneName;
                activeForceReload = false;
                hostTransitionActive = true;
                joinOnlyBarrier = true;
                readiness.Begin(activeTransitionId, sceneName, new[] { playerId }, plugin.Network.LocalPlayerId);
                readiness.MarkHostReady(activeTransitionId, sceneName);
            }
            // Additional clients may join while this join-only barrier is active. The host
            // remains unpaused, and START waits until every participant has loaded.
            if (activeTransitionId == 0 || string.IsNullOrEmpty(activeSceneName)) return false;
            readiness.AddPlayer(playerId);
            plugin.Network.SendToPlayer(playerId, MessageType.LevelTransition,
                LevelTransitionCodec.Encode(activeTransitionId, LevelTransitionStage.Prepare, activeSceneName, activeForceReload,
                    ReadyPlayerCount(), TotalPlayerCount(), readiness.GetReadyPlayerIds(), (byte)plugin.SessionDifficulty));
            BroadcastReadinessProgress(plugin);
            return true;
        }

        internal static void OnPlayerLeft(uint playerId)
        {
            var plugin = BloodlinkPlugin.Instance;
            readiness.RemovePlayer(playerId);
            if (plugin != null && plugin.IsHost && hostTransitionActive && !readiness.IsStarted)
                BroadcastReadinessProgress(plugin);
            if (joinOnlyBarrier && readiness.ParticipantCount == 0)
            {
                hostTransitionActive = false;
                joinOnlyBarrier = false;
                readiness.Reset();
                return;
            }
            if (joinOnlyBarrier && plugin != null && readiness.ReadyCount == readiness.ParticipantCount)
            {
                CompleteJoinOnlyBarrier(plugin);
                return;
            }
            if (plugin != null && plugin.IsHost && hostTransitionActive && !readiness.IsStarted && readiness.IsHostReady)
                TryStartHostTransition(plugin);
        }

        internal static void Update()
        {
            var plugin = BloodlinkPlugin.Instance;
            if (plugin == null) return;
            if (!plugin.IsHost && clientTransitionActive && clientStartReceived && clientSceneReady && IsTargetSceneFullyLoaded(activeSceneName))
            {
                CompleteClientTransition(plugin);
                return;
            }
            if (!plugin.IsHost && clientTransitionActive && clientSceneReady && !clientStartReceived &&
                Time.unscaledTime >= nextClientReadyRetryAt)
            {
                SendClientReady(plugin, false);
                nextClientReadyRetryAt = Time.unscaledTime + 1.5f;
            }
            if (!hostTransitionActive && !clientTransitionActive && !string.IsNullOrEmpty(snapshotRecoveryScene) &&
                IsTargetSceneFullyLoaded(snapshotRecoveryScene))
            {
                var recoveredScene = snapshotRecoveryScene;
                snapshotRecoveryScene = null;
                plugin.SetSceneSnapshotSyncReady(true);
                plugin.BeginAutomaticWorldResync();
                plugin.LogLevelTransition("Resumed player snapshots after restoring " + recoveredScene + ".");
            }
            if (!plugin.IsHost && clientTransitionActive && !plugin.IsConnected)
            {
                var interruptedTransitionId = activeTransitionId;
                var rollbackScene = clientPreviousSceneName;
                var hadPendingLoad = CancelPendingSceneLoad(plugin);
                plugin.Network.SetLocalSnapshotsDuringBarrier(false);
                clientTransitionActive = false;
                clientSceneReady = false;
                clientReadyOverlayDismissed = false;
                clientStartReceived = false;
                clientSceneLoadObserved = false;
                clientPreviousSceneName = null;
                clientReadyPlayerCount = 0;
                clientTotalPlayerCount = 0;
                clientReadyPlayerIds = new uint[0];
                RestoreTimeScale();
                plugin.LogLevelTransition("Released the loading freeze after losing the host during transition " + interruptedTransitionId + ".");
                RestorePreviousScene(plugin, rollbackScene, "client", hadPendingLoad);
                DeferSnapshotResume(plugin, rollbackScene);
                return;
            }
            if ((plugin.IsHost && hostTransitionActive && !joinOnlyBarrier && readiness.IsHostReady && !readiness.IsStarted) ||
                (!plugin.IsHost && clientTransitionActive && clientSceneReady && !clientReadyOverlayDismissed && !clientStartReceived))
            {
                if (IsTargetSceneFullyLoaded(activeSceneName)) FreezeTimeScale();
            }
        }

        private static void CancelHostTransition(BloodlinkPlugin plugin)
        {
            if (!hostTransitionActive || plugin == null || !plugin.IsHost) return;
            plugin.Network.BroadcastReliable(MessageType.LevelTransition,
                LevelTransitionCodec.Encode(activeTransitionId, LevelTransitionStage.Abort, activeSceneName, activeForceReload,
                    ReadyPlayerCount(), TotalPlayerCount(), readiness.GetReadyPlayerIds(), (byte)plugin.SessionDifficulty));
            var wasJoinOnly = joinOnlyBarrier;
            plugin.LogLevelTransition((wasJoinOnly ? "Host cancelled late-join synchronization " : "Host cancelled level ") + activeTransitionId + ". " +
                AppendSceneLoadDiagnostic(activeSceneName).Trim());
            plugin.SetLevelTransitionStatus(wasJoinOnly ? "Join synchronization cancelled." : "Host cancelled the level change; returning to the previous level.");
            var rollbackScene = hostPreviousSceneName;
            var hadPendingLoad = CancelPendingSceneLoad(plugin);
            hostTransitionActive = false;
            joinOnlyBarrier = false;
            hostSceneLoadObserved = false;
            readiness.Reset();
            RestoreTimeScale();
            hostPreviousSceneName = null;
            RestorePreviousScene(plugin, rollbackScene, "host", hadPendingLoad);
            DeferSnapshotResume(plugin, rollbackScene);
        }

        private static void TryStartHostTransition(BloodlinkPlugin plugin)
        {
            if (!hostTransitionActive || !readiness.CanStart()) return;
            foreach (var playerId in plugin.Network.Players.Keys)
                if (playerId != plugin.Network.LocalPlayerId && readiness.IsPlayerReady(playerId))
                {
                    plugin.SendCurrentCheckpointToPlayer(playerId, activeSceneName);
                    plugin.SendInitialEnemySnapshot(playerId);
                }
            readiness.MarkStarted();
            plugin.Network.BroadcastReliable(MessageType.LevelTransition,
                LevelTransitionCodec.Encode(activeTransitionId, LevelTransitionStage.Start, activeSceneName, activeForceReload,
                    ReadyPlayerCount(), TotalPlayerCount(), readiness.GetReadyPlayerIds(), (byte)plugin.SessionDifficulty));
            plugin.SetSceneSnapshotSyncReady(true);
            RestoreTimeScale();
            plugin.SetLevelTransitionStatus("All players are ready in " + activeSceneName + ".");
            plugin.LogLevelTransition("All connected players are ready for level " + activeTransitionId + ".");
        }

        private static void CompleteJoinOnlyBarrier(BloodlinkPlugin plugin)
        {
            if (!joinOnlyBarrier || readiness.IsStarted || readiness.ReadyCount != readiness.ParticipantCount) return;
            foreach (var player in plugin.Network.Players.Values)
            {
                if (!readiness.IsPlayerReady(player.PlayerId)) continue;
                plugin.SendCurrentCheckpointToPlayer(player.PlayerId, activeSceneName);
                plugin.SendInitialEnemySnapshot(player.PlayerId);
            }
            readiness.MarkStarted();
            foreach (var player in plugin.Network.Players.Values)
            {
                if (player.PlayerId == plugin.Network.LocalPlayerId) continue;
                plugin.Network.SendToPlayer(player.PlayerId, MessageType.LevelTransition,
                    LevelTransitionCodec.Encode(activeTransitionId, LevelTransitionStage.Start, activeSceneName, activeForceReload,
                        ReadyPlayerCount(), TotalPlayerCount(), readiness.GetReadyPlayerIds(), (byte)plugin.SessionDifficulty));
            }
            hostTransitionActive = false;
            joinOnlyBarrier = false;
            plugin.LogLevelTransition("Late joiners synchronized with the active level.");
        }

        private static bool SendClientReady(BloodlinkPlugin plugin, bool logFailure = true)
        {
            var sent = plugin.Network.SendToHost(MessageType.LevelTransition,
                LevelTransitionCodec.Encode(activeTransitionId, LevelTransitionStage.Ready, activeSceneName, activeForceReload));
            nextClientReadyRetryAt = Time.unscaledTime + 1.5f;
            if (sent) plugin.LogLevelTransition("Client is ready for level " + activeTransitionId + ".");
            else if (logFailure) plugin.LogLevelTransition("Could not send READY for transition " + activeTransitionId + "; will retry while connected.");
            return sent;
        }

        private static byte ReadyPlayerCount()
        {
            return (byte)readiness.GetReadyPlayerIds().Length;
        }

        private static byte TotalPlayerCount()
        {
            return (byte)System.Math.Min(4, readiness.ParticipantCount + 1);
        }

        private static void BroadcastReadinessProgress(BloodlinkPlugin plugin)
        {
            if (plugin == null || !plugin.IsHost || !hostTransitionActive || readiness.IsStarted ||
                activeTransitionId == 0 || string.IsNullOrEmpty(activeSceneName)) return;
            var total = TotalPlayerCount();
            if (total == 0) return;
            plugin.Network.BroadcastReliable(MessageType.LevelTransition,
                LevelTransitionCodec.Encode(activeTransitionId, LevelTransitionStage.Progress, activeSceneName,
                    activeForceReload, ReadyPlayerCount(), total, readiness.GetReadyPlayerIds(), (byte)plugin.SessionDifficulty));
        }

        private static string FormatReadyPlayerNames(uint[] readyPlayerIds)
        {
            if (readyPlayerIds == null || readyPlayerIds.Length == 0) return "none yet";
            var plugin = BloodlinkPlugin.Instance;
            var names = string.Empty;
            for (var i = 0; i < readyPlayerIds.Length; i++)
            {
                PlayerSnapshot player;
                var name = plugin != null && plugin.Network.Players.TryGetValue(readyPlayerIds[i], out player) &&
                    !string.IsNullOrWhiteSpace(player.PlayerName) ? player.PlayerName : "Player " + readyPlayerIds[i];
                if (names.Length > 0) names += ", ";
                names += name;
            }
            return names;
        }

        private static void CompleteClientTransition(BloodlinkPlugin plugin)
        {
            clientTransitionActive = false;
            clientReadyOverlayDismissed = false;
            clientSceneLoadObserved = false;
            nextClientReadyRetryAt = 0f;
            RestoreTimeScale();
            clientPreviousSceneName = null;
            plugin.SetSceneSnapshotSyncReady(true);
            plugin.BeginAutomaticWorldResync();
            plugin.SetLevelTransitionStatus("Connected to " + activeSceneName + ".");
            plugin.LogLevelTransition("Started level " + activeTransitionId + " after host readiness barrier.");
            // Optional replica initialization must not keep movement/time locked if
            // an enemy replica cannot be resolved during the first frame of the map.
            try { plugin.ApplyInitialEnemySnapshot(); }
            catch (System.Exception ex) { plugin.LogLevelTransition("Initial enemy snapshot failed after releasing READY: " + ex); }
        }

        private static void RestorePreviousScene(BloodlinkPlugin plugin, string sceneName, string role, bool forceReload = false)
        {
            if (string.IsNullOrWhiteSpace(sceneName) || (!forceReload &&
                IsTargetSceneFullyLoaded(sceneName))) return;

            allowSceneLoad = true;
            try
            {
                if (role == "host") hostPreviousSceneName = sceneName;
                else clientPreviousSceneName = sceneName;
                recoveryTransitionActive = true;
                recoverySceneName = sceneName;
                if (plugin != null) plugin.SetLevelTransitionStatus("Returning to the previous level: " + sceneName + ".");
                transitionLoadCoroutine = SceneHelper.LoadSceneAsync(sceneName);
                if (plugin != null) plugin.LogLevelTransition("Restoring " + role + " to the previous level: " + sceneName + ".");
            }
            catch (System.Exception ex)
            {
                if (plugin != null) plugin.LogLevelTransition("Could not restore " + role + " to the previous level: " + ex.Message);
            }
            finally
            {
                allowSceneLoad = false;
            }
        }

        private static void FreezeTimeScale()
        {
            if (!timeScaleSaved)
            {
                savedTimeScale = Time.timeScale > 0f ? Time.timeScale : 1f;
                timeScaleSaved = true;
            }
            Time.timeScale = 0f;
        }

        private static void RestoreTimeScale()
        {
            if (!timeScaleSaved) return;
            Time.timeScale = savedTimeScale;
            timeScaleSaved = false;
        }

        [HarmonyPatch(typeof(TimeController), nameof(TimeController.InitializeValues))]
        private static class TimeControllerBarrierPatch
        {
            private static void Postfix()
            {
                if (timeScaleSaved &&
                    ((hostTransitionActive && !joinOnlyBarrier && readiness.IsHostReady && !readiness.IsStarted) ||
                     (clientTransitionActive && clientSceneReady && !clientReadyOverlayDismissed && !clientStartReceived)))
                    Time.timeScale = 0f;
            }
        }

        [HarmonyPatch(typeof(SceneHelper), nameof(SceneHelper.RestartSceneAsync))]
        private static class RestartScenePatch
        {
            private static bool Prefix(ref Coroutine __result)
            {
                var plugin = BloodlinkPlugin.Instance;
                if (plugin != null && plugin.InterceptDeathRestart())
                {
                    __result = null;
                    return false;
                }
                if (plugin != null && plugin.IsHost && plugin.IsConnected) forceNextReload = true;
                return true;
            }
        }

        internal static void ReceiveJoinSessionState(string sceneName, byte difficulty)
        {
            var plugin = BloodlinkPlugin.Instance;
            if (plugin == null || plugin.IsHost || !plugin.IsConnected || string.IsNullOrWhiteSpace(sceneName)) return;
            SessionDifficultyOverride.Apply(difficulty);
            // The host emits this beacon only after its readiness barrier started.
            // It recovers a missing START without guessing from a timeout or from
            // the client's own READY click.
            if (clientTransitionActive && string.Equals(sceneName, activeSceneName, System.StringComparison.Ordinal))
            {
                clientStartReceived = true;
                if (clientSceneReady && IsTargetSceneFullyLoaded(sceneName)) CompleteClientTransition(plugin);
                return;
            }
            if (IsTargetSceneFullyLoaded(sceneName))
            {
                // A late join can already be on the correct map while the host is
                // unable to open a join-only barrier (for example, during another
                // transition). Do not treat the scene beacon as a no-op: request
                // the authoritative object/enemy/item state directly.
                if (!plugin.Network.SceneSnapshotSyncReady) plugin.SetSceneSnapshotSyncReady(true);
                plugin.BeginAutomaticWorldResync();
                return;
            }
            if (!string.IsNullOrEmpty(SceneHelper.PendingScene))
            {
                plugin.LogLevelTransition("Waiting for the current scene load before joining the host's active scene " + sceneName + ".");
                return;
            }
            snapshotRecoveryScene = sceneName;
            plugin.SetSceneSnapshotSyncReady(false);
            allowSceneLoad = true;
            try { SceneHelper.LoadSceneAsync(sceneName); }
            finally { allowSceneLoad = false; }
            plugin.LogLevelTransition("Joined the host's active scene: " + sceneName);
        }
    }
}
