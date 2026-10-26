using System.Collections.Generic;
using HarmonyLib;
using ULTRAKILL.Bloodlink.Core;
using ULTRAKILL.Bloodlink.Networking;
using UnityEngine;

namespace ULTRAKILL.Bloodlink.Enemies
{
    // Native SwingCheck2 finds a NewMovement on a local collider. Remote target
    // proxies have neither, so the host must test its active attack volumes itself.
    internal static class EnemyMeleeSync
    {
        private sealed class Attack
        {
            public SwingCheck2 Swing;
            public Vector3 PreviousPosition;
        }

        private static readonly Dictionary<int, Attack> attacks = new Dictionary<int, Attack>();
        private static readonly EnemyMeleeHitLedger hits = new EnemyMeleeHitLedger();
        private static readonly List<int> expired = new List<int>();
        private static int acceptedHits;
        internal static string Diagnostics => "melee: active=" + attacks.Count + ", hits=" + acceptedHits;

        internal static void Begin(SwingCheck2 swing)
        {
            var plugin = BloodlinkPlugin.Instance;
            if (plugin == null || !plugin.IsHost || !plugin.IsConnected || swing == null) return;
            attacks[swing.GetInstanceID()] = new Attack { Swing = swing, PreviousPosition = swing.transform.position };
            hits.BeginAttack(swing.GetInstanceID());
        }

        internal static void End(SwingCheck2 swing)
        {
            if (swing != null) { attacks.Remove(swing.GetInstanceID()); hits.EndAttack(swing.GetInstanceID()); }
        }

        internal static void Reset() { attacks.Clear(); hits.Reset(); expired.Clear(); acceptedHits = 0; }
        internal static void ForgetPlayer(uint playerId) { hits.ForgetPlayer(playerId); }

        internal static void Update(NetworkService network)
        {
            if (network == null || !network.IsHost || !network.IsConnected || !network.SceneSnapshotSyncReady || Time.timeScale <= 0f) return;
            expired.Clear();
            var now = Time.unscaledTime;
            foreach (var pair in attacks)
            {
                var attack = pair.Value;
                var swing = attack.Swing;
                if (swing == null || !swing.damaging || !swing.gameObject.activeInHierarchy || swing.eid == null || swing.eid.dead)
                { expired.Add(pair.Key); continue; }
                if (!swing.enabled || swing.damage <= 0) continue;
                var displacement = swing.transform.position - attack.PreviousPosition;
                var travel = swing.interpolateBetweenFrames && displacement.sqrMagnitude < 25f
                    ? displacement : Vector3.zero;
                foreach (var player in network.Players.Values)
                {
                    if (player == null || player.PlayerId == network.LocalPlayerId || player.Health == 0 ||
                        (player.MovementFlags & 5) != 0 || !network.HasFreshPlayerSnapshot(player.PlayerId, 0.5f) ||
                        (swing.ignoreSlidingPlayer && (player.MovementFlags & 2) != 0)) continue;
                    var position = player.Position + Vector3.ClampMagnitude(player.Velocity *
                        Mathf.Min(network.EstimateOneWayLatencySeconds(player.PlayerId), 0.1f), 2f);
                    var touches = TouchesPlayer(swing.col, position, travel);
                    if (!touches && swing.additionalColliders != null)
                        foreach (var collider in swing.additionalColliders)
                            if (TouchesPlayer(collider, position, travel)) { touches = true; break; }
                    if (!touches) continue;
                    if (swing.useRaycastCheck)
                    {
                        var center = position + Vector3.up * 0.9f;
                        var origin = new Vector3(swing.eid.transform.position.x, center.y, swing.eid.transform.position.z);
                        if (Physics.Linecast(origin, center, LayerMask.GetMask("Environment", "Outdoors", "EnvironmentBaked", "OutdoorsBaked"), QueryTriggerInteraction.Ignore)) continue;
                    }
                    var damage = Mathf.Clamp(Mathf.RoundToInt(swing.damage * swing.eid.totalDamageModifier), 1, 200);
                    if (!hits.TryAccept(pair.Key, player.PlayerId, now, damage, swing.canHitPlayerMultipleTimes)) continue;
                    acceptedHits++;
                    var health = (ushort)Mathf.Max(0, player.Health - damage);
                    network.SetAuthoritativePlayerVitals(player.PlayerId, health, health == 0);
                }
                attack.PreviousPosition = swing.transform.position;
            }
            foreach (var id in expired) { attacks.Remove(id); hits.EndAttack(id); }
        }

        private static bool TouchesPlayer(Collider collider, Vector3 position, Vector3 travel)
        {
            if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy) return false;
            for (var i = 0; i < 3; i++)
            {
                var sample = position + Vector3.up * (0.25f + i * 0.65f);
                var closest = collider.ClosestPoint(sample);
                var previous = closest - travel;
                var segment = closest - previous;
                var length = segment.sqrMagnitude;
                var t = length > 0.000001f ? Mathf.Clamp01(Vector3.Dot(sample - previous, segment) / length) : 0f;
                if ((sample - (previous + segment * t)).sqrMagnitude <= 0.6f * 0.6f) return true;
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(SwingCheck2), nameof(SwingCheck2.DamageStart))]
    internal static class RemoteMeleeStartPatch
    {
        private static void Postfix(SwingCheck2 __instance) { EnemyMeleeSync.Begin(__instance); }
    }

    [HarmonyPatch(typeof(SwingCheck2), nameof(SwingCheck2.DamageStop))]
    internal static class RemoteMeleeStopPatch
    {
        private static void Postfix(SwingCheck2 __instance) { EnemyMeleeSync.End(__instance); }
    }
}
