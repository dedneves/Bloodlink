using System;

namespace ULTRAKILL.Bloodlink.Enemies
{
    public static class EnemyMatchPolicy
    {
        public const float FallbackGraceSeconds = 0.5f;
        public const float MaximumFallbackDistance = 25f;
        public const float MinimumSquaredSeparation = 4f;

        public static bool ShouldAllowNearestFallback(uint stableKey, float unresolvedAgeSeconds)
        {
            if (stableKey == 0) return true;
            return Finite(unresolvedAgeSeconds) && unresolvedAgeSeconds >= FallbackGraceSeconds;
        }

        public static bool IsUnambiguousNearest(float nearestDistanceSquared, float secondNearestDistanceSquared)
        {
            if (!Finite(nearestDistanceSquared) || nearestDistanceSquared < 0f ||
                nearestDistanceSquared > MaximumFallbackDistance * MaximumFallbackDistance) return false;

            if (float.IsPositiveInfinity(secondNearestDistanceSquared)) return true;
            if (!Finite(secondNearestDistanceSquared) || secondNearestDistanceSquared < nearestDistanceSquared) return false;
            return secondNearestDistanceSquared - nearestDistanceSquared >= MinimumSquaredSeparation;
        }

        private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
    }
}
