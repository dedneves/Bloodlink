using System.Linq;
using ULTRAKILL.Bloodlink.Core;
using UnityEngine;

namespace ULTRAKILL.Bloodlink.Levels
{
    internal static class EncounterRecovery
    {
        private static Door[] doors;
        private static GameObject room;
        private static Vector3 anchor;
        private static bool hasAnchor;
        private static float nextBossScan;
        internal static void Reset() { doors = null; room = null; hasAnchor = false; nextBossScan = 0f; }
        internal static bool IsLocked => hasAnchor && doors != null && doors.Any(door => door != null && door.locked);

        internal static void ObserveBoss(BloodlinkPlugin plugin)
        {
            if (IsLocked || !plugin.IsConnected || !plugin.Network.SceneSnapshotSyncReady || Time.unscaledTime < nextBossScan) return;
            nextBossScan = Time.unscaledTime + 1f;
            // Some bosses start from a cutscene rather than ActivateArena.
            foreach (var enemy in Object.FindObjectsOfType<EnemyIdentifier>())
            {
                if (enemy == null || !enemy.isBoss || enemy.dead) continue;
                var locked = Object.FindObjectsOfType<Door>().Where(door => door.locked &&
                    (door.transform.position - enemy.transform.position).sqrMagnitude < 80f * 80f).ToArray();
                if (locked.Length == 0) continue;
                foreach (var offset in new[] { Vector3.right * 3f, Vector3.left * 3f, Vector3.forward * 3f, Vector3.back * 3f })
                {
                    Vector3 safe;
                    if (!plugin.TryFindStandingPosition(enemy.transform.position + offset + Vector3.up * 1.25f, out safe)) continue;
                    doors = locked; anchor = safe; hasAnchor = true;
                    var zone = enemy.GetComponentInParent<GoreZone>();
                    room = zone != null ? zone.gameObject : null;
                    return;
                }
                // Flying bosses can be much higher than the usable floor. A
                // nearby party member (including a body in the arena) supplies
                // a floor-height reference without casting down from the roof.
                foreach (var player in plugin.Network.Players.Values.OrderBy(player => (player.Position - enemy.transform.position).sqrMagnitude))
                {
                    if ((player.Position - enemy.transform.position).sqrMagnitude > 50f * 50f) continue;
                    Vector3 safe;
                    if (!plugin.TryFindStandingPosition(player.Position + Vector3.up, out safe)) continue;
                    doors = locked; anchor = safe; hasAnchor = true;
                    var zone = enemy.GetComponentInParent<GoreZone>();
                    room = zone != null ? zone.gameObject : null;
                    return;
                }
            }
        }

        internal static void Capture(ActivateArena arena, BloodlinkPlugin plugin)
        {
            if (arena == null || arena.onlyWave || arena.forEnemy || arena.doors == null ||
                !arena.doors.Any(door => door != null && door.locked)) return;
            if (arena.enemies != null)
                foreach (var source in arena.enemies)
                {
                    if (source == null) continue;
                    var enemy = source.GetComponentInChildren<EnemyIdentifier>(true);
                    if (enemy == null) continue;
                    foreach (var offset in new[] { Vector3.right * 3f, Vector3.left * 3f, Vector3.forward * 3f, Vector3.back * 3f })
                    {
                        Vector3 safe;
                        if (!plugin.TryFindStandingPosition(enemy.transform.position + offset + Vector3.up * 1.25f, out safe)) continue;
                        doors = arena.doors; anchor = safe; hasAnchor = true;
                        var zone = enemy.GetComponentInParent<GoreZone>();
                        room = zone != null ? zone.gameObject : null;
                        return;
                    }
                }
            var trigger = arena.GetComponent<Collider>();
            if (trigger == null) return;
            foreach (var player in plugin.Network.Players.Values)
            {
                if ((player.MovementFlags & 4) != 0 || !trigger.bounds.Contains(player.Position)) continue;
                Vector3 safe;
                if (!plugin.TryFindStandingPosition(player.Position, out safe)) continue;
                doors = arena.doors; anchor = safe; hasAnchor = true;
                var zone = arena.GetComponentInParent<GoreZone>();
                room = zone != null ? zone.gameObject : null;
                return;
            }
        }

        internal static bool TryGetSpawn(BloodlinkPlugin plugin, out Vector3 position)
        {
            position = anchor;
            if (!IsLocked) return false;
            if (room != null)
                for (var parent = room.transform; parent != null; parent = parent.parent)
                    if (!parent.gameObject.activeSelf) parent.gameObject.SetActive(true);
            return plugin.TryFindStandingPosition(anchor, out position);
        }
    }
}
