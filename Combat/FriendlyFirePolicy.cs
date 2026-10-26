using UnityEngine;

namespace ULTRAKILL.Bloodlink.Combat
{
    internal static class FriendlyFirePolicy
    {
        internal static int Damage(byte slot, byte kind) => slot == 0 ? 15 : slot == 1 ? (kind == 1 ? 25 : 15) :
            slot == 2 ? 30 : slot == 3 ? 3 : slot == 4 ? 50 : slot == 5 ? 35 : 0;
        internal static float Range(byte slot) => slot == 0 ? 3f : slot == 2 ? 35f : slot == 3 ? 100f : slot == 5 ? 120f : 500f;
        internal static bool Intersects(Vector3 origin, Vector3 direction, Vector3 target, float range, float radius, out float distance)
        {
            distance = float.PositiveInfinity;
            if (direction.sqrMagnitude < 0.9f || range <= 0 || radius <= 0) return false;
            direction.Normalize();
            foreach (var offset in new[] { Vector3.down * 0.65f, Vector3.zero, Vector3.up * 0.65f })
            {
                var delta = target + offset - origin;
                var along = Vector3.Dot(delta, direction);
                if (along <= 0f || along > range) continue;
                if ((delta - direction * along).sqrMagnitude <= radius * radius) distance = Mathf.Min(distance, along);
            }
            return !float.IsPositiveInfinity(distance);
        }
    }
}
