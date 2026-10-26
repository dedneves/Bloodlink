using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using ULTRAKILL.Bloodlink.Core;
using ULTRAKILL.Bloodlink.Networking;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ULTRAKILL.Bloodlink.Levels
{
    internal static class LevelProgressionCoordinator
    {
        private static readonly HashSet<ActivateNextWave> authorizedRemoteWaves = new HashSet<ActivateNextWave>();
        private static readonly Dictionary<ActivateArena, float> lastArenaRequestAt = new Dictionary<ActivateArena, float>();
        private static readonly DamageRequestRateLimiter hostArenaRequestLimiter = new DamageRequestRateLimiter(2f, 1.5f);
        private static bool applyingRemoteArenaActivation;
        private sealed class Deferred { public byte[] Payload; public float ExpiresAt; }
        private static readonly Dictionary<string, byte[]> committed = new Dictionary<string, byte[]>();
        private static readonly Dictionary<string, Deferred> deferred = new Dictionary<string, Deferred>();
        private static readonly Dictionary<string, byte[]> requested = new Dictionary<string, byte[]>();
        private static float retryAt;

        private static string EventKey(LevelProgressionEventData data) => data.SceneName + "|" + data.ObjectPath + "|" + data.ComponentIndex + "|" + data.Kind;
        private static void Broadcast(NetworkService network, byte[] payload)
        { var data = LevelProgressionEventCodec.Decode(payload); committed[EventKey(data)] = payload; network.BroadcastReliable(MessageType.LevelProgression, payload); }
        internal static void SendCurrentToPlayer(NetworkService network, uint playerId)
        { foreach (var payload in committed.Values) network.SendToPlayer(playerId, MessageType.LevelProgression, payload); }
        internal static void Update()
        {
            var plugin = BloodlinkPlugin.Instance;
            if (plugin == null || !plugin.IsConnected || plugin.IsHost || Time.unscaledTime < retryAt) return;
            retryAt = Time.unscaledTime + 1f;
            foreach (var payload in requested.Values) plugin.Network.SendToHost(MessageType.LevelProgression, payload);
            foreach (var key in new List<string>(deferred.Keys))
            { var pending = deferred[key]; if (pending.ExpiresAt < Time.unscaledTime) deferred.Remove(key); else Receive(1, pending.Payload); }
        }

        internal static void Reset()
        {
            authorizedRemoteWaves.Clear();
            lastArenaRequestAt.Clear();
            hostArenaRequestLimiter.Clear();
            applyingRemoteArenaActivation = false;
            committed.Clear(); deferred.Clear(); requested.Clear(); retryAt = 0f;
        }

        internal static void OnSceneChanged()
        {
            authorizedRemoteWaves.Clear();
            lastArenaRequestAt.Clear();
            applyingRemoteArenaActivation = false;
            committed.Clear(); deferred.Clear(); requested.Clear(); retryAt = 0f;
        }

        internal static void ForgetPlayer(uint playerId)
        {
            hostArenaRequestLimiter.Remove(playerId);
        }

        internal static void RequestArenaActivation(ActivateArena arena)
        {
            var plugin = BloodlinkPlugin.Instance;
            if (plugin == null || !plugin.IsConnected || plugin.IsHost || arena == null) return;
            byte componentIndex;
            string sceneName;
            string path;
            if (!SceneObjectIdentity.TryBuild(arena, out sceneName, out path, out componentIndex)) return;
            var now = Time.unscaledTime;
            float lastSent;
            if (lastArenaRequestAt.TryGetValue(arena, out lastSent) && now - lastSent < 1f) return;
            var payload = LevelProgressionEventCodec.Encode(LevelProgressionEventKind.ArenaActivationRequest, sceneName, path, componentIndex);
            requested[sceneName + "|" + path + "|" + componentIndex] = payload;
            if (plugin.Network.SendToHost(MessageType.LevelProgression, payload)) lastArenaRequestAt[arena] = now;
        }

        internal static bool ShouldRunArenaActivation(ActivateArena arena)
        {
            var plugin = BloodlinkPlugin.Instance;
            if (plugin == null || !plugin.IsConnected || plugin.IsHost) return true;
            if (ItemSync.IsApplyingScenario) return true;
            if (applyingRemoteArenaActivation) return true;
            RequestArenaActivation(arena);
            return false;
        }

        internal static void ArenaActivationCompleted(ActivateArena arena, bool wasActivated)
        {
            var plugin = BloodlinkPlugin.Instance;
            if (plugin == null || !plugin.IsHost || !plugin.IsConnected || wasActivated || arena == null || !arena.activated) return;
            foreach (var player in plugin.Network.Players.Values) plugin.Network.AllowPlayerReposition(player.PlayerId);
            plugin.RelocateLocalPlayerIntoArena(arena);
            byte componentIndex;
            string sceneName;
            string path;
            if (!SceneObjectIdentity.TryBuild(arena, out sceneName, out path, out componentIndex)) return;
            Broadcast(plugin.Network,
                LevelProgressionEventCodec.Encode(LevelProgressionEventKind.ArenaActivated, sceneName, path, componentIndex));
        }

        internal static bool ShouldRunWaveTick(ActivateNextWave wave, out bool wasActivated)
        {
            wasActivated = wave != null && wave.activated;
            var plugin = BloodlinkPlugin.Instance;
            if (plugin == null || !plugin.IsConnected || plugin.IsHost) return true;
            if (wave == null || !authorizedRemoteWaves.Remove(wave)) return false;
            wave.deadEnemies = Mathf.Max(wave.deadEnemies, wave.enemyCount);
            return true;
        }

        internal static void WaveTickCompleted(ActivateNextWave wave, bool wasActivated)
        {
            var plugin = BloodlinkPlugin.Instance;
            if (plugin == null || !plugin.IsHost || !plugin.IsConnected || wasActivated || wave == null || !wave.activated) return;
            byte componentIndex;
            string sceneName;
            string path;
            if (!SceneObjectIdentity.TryBuild(wave, out sceneName, out path, out componentIndex)) return;
            Broadcast(plugin.Network,
                LevelProgressionEventCodec.Encode(LevelProgressionEventKind.WaveAdvanced, sceneName, path, componentIndex));
        }

        internal static void Receive(uint senderId, byte[] payload)
        {
            var plugin = BloodlinkPlugin.Instance;
            if (plugin == null || !plugin.IsConnected) return;
            var eventData = LevelProgressionEventCodec.Decode(payload);
            if (!SceneObjectIdentity.IsCurrentScene(eventData.SceneName)) return;

            if (plugin.IsHost)
            {
                if (eventData.Kind != LevelProgressionEventKind.ArenaActivationRequest ||
                    !plugin.Network.HasFreshPlayerSnapshot(senderId, 0.5f) ||
                    !hostArenaRequestLimiter.TryAccept(senderId, plugin.Network.NetworkTimeSeconds)) return;
                PlayerSnapshot player;
                if (!plugin.Network.Players.TryGetValue(senderId, out player) || (player.MovementFlags & 4) != 0) return;
                var arena = SceneObjectIdentity.Resolve<ActivateArena>(eventData.SceneName, eventData.ObjectPath, eventData.ComponentIndex);
                if (arena == null || arena.activated || !IsPlayerInsideArenaTrigger(arena, player.Position)) return;
                if (arena.waitForStatus > 0)
                {
                    var arenaStatus = arena.GetComponentInParent<ArenaStatus>();
                    if (arenaStatus == null || arenaStatus.currentStatus < arena.waitForStatus) return;
                }
                arena.Activate();
                return;
            }

            if (senderId != 1) return;
            var eventKey = EventKey(eventData);
            if (eventData.Kind == LevelProgressionEventKind.ArenaActivated)
            {
                var arena = SceneObjectIdentity.Resolve<ActivateArena>(eventData.SceneName, eventData.ObjectPath, eventData.ComponentIndex);
                if (arena == null) { if (!deferred.ContainsKey(eventKey)) deferred[eventKey] = new Deferred { Payload = payload, ExpiresAt = Time.unscaledTime + 30f }; return; }
                deferred.Remove(eventKey);
                requested.Remove(eventData.SceneName + "|" + eventData.ObjectPath + "|" + eventData.ComponentIndex);
                lastArenaRequestAt.Remove(arena);
                if (arena.activated)
                {
                    plugin.RelocateLocalPlayerIntoArena(arena);
                    return;
                }
                applyingRemoteArenaActivation = true;
                try { arena.Activate(); }
                finally { applyingRemoteArenaActivation = false; }
                plugin.RelocateLocalPlayerIntoArena(arena);
            }
            else if (eventData.Kind == LevelProgressionEventKind.WaveAdvanced)
            {
                var wave = SceneObjectIdentity.Resolve<ActivateNextWave>(eventData.SceneName, eventData.ObjectPath, eventData.ComponentIndex);
                if (wave == null) { if (!deferred.ContainsKey(eventKey)) deferred[eventKey] = new Deferred { Payload = payload, ExpiresAt = Time.unscaledTime + 30f }; return; }
                deferred.Remove(eventKey);
                if (wave.activated) return;
                wave.deadEnemies = Mathf.Max(wave.deadEnemies, wave.enemyCount);
                authorizedRemoteWaves.Add(wave);
                try { AccessTools.Method(typeof(ActivateNextWave), "FixedUpdate").Invoke(wave, null); }
                finally { authorizedRemoteWaves.Remove(wave); }
            }
        }

        private static bool IsPlayerInsideArenaTrigger(ActivateArena arena, Vector3 playerPosition)
        {
            var colliders = new HashSet<Collider>();
            foreach (var collider in arena.GetComponentsInParent<Collider>(true)) if (collider != null) colliders.Add(collider);
            foreach (var collider in arena.GetComponentsInChildren<Collider>(true)) if (collider != null) colliders.Add(collider);
            var foundTrigger = false;
            foreach (var collider in colliders)
            {
                if (collider == null || !collider.enabled || !collider.isTrigger || !collider.gameObject.activeInHierarchy) continue;
                foundTrigger = true;
                if ((collider.ClosestPoint(playerPosition) - playerPosition).sqrMagnitude <= 3.5f * 3.5f) return true;
            }
            // A few arena prefabs keep their trigger on a sibling object. Preserve a bounded
            // fallback for those prefabs instead of rejecting every remote-player activation.
            return !foundTrigger && (playerPosition - arena.transform.position).sqrMagnitude <= 100f * 100f;
        }
    }

    [HarmonyPatch(typeof(ActivateArena), nameof(ActivateArena.Activate))]
    internal static class ActivateArenaReplicationPatch
    {
        private static bool Prefix(ActivateArena __instance, out bool __state)
        {
            __state = __instance != null && __instance.activated;
            return LevelProgressionCoordinator.ShouldRunArenaActivation(__instance);
        }

        private static void Postfix(ActivateArena __instance, bool __state)
        {
            LevelProgressionCoordinator.ArenaActivationCompleted(__instance, __state);
        }
    }

    [HarmonyPatch(typeof(ActivateNextWave), "FixedUpdate")]
    internal static class ActivateNextWaveReplicationPatch
    {
        private static bool Prefix(ActivateNextWave __instance, out bool __state)
        {
            return LevelProgressionCoordinator.ShouldRunWaveTick(__instance, out __state);
        }

        private static void Postfix(ActivateNextWave __instance, bool __state)
        {
            LevelProgressionCoordinator.WaveTickCompleted(__instance, __state);
        }
    }
}
