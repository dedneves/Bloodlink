using System;
using System.Collections.Generic;

namespace ULTRAKILL.Bloodlink.Networking
{
    /// <summary>Tracks local receive times for the latest player snapshots.</summary>
    public sealed class SnapshotFreshnessTracker
    {
        private readonly Dictionary<uint, float> receivedAtByPlayer = new Dictionary<uint, float>();

        public void Record(uint playerId, float now)
        {
            if (playerId == 0 || !Finite(now) || now < 0f) return;
            receivedAtByPlayer[playerId] = now;
        }

        public bool IsFresh(uint playerId, float now, float maximumAgeSeconds)
        {
            float receivedAt;
            if (playerId == 0 || !Finite(now) || now < 0f || !Finite(maximumAgeSeconds) || maximumAgeSeconds < 0f ||
                !receivedAtByPlayer.TryGetValue(playerId, out receivedAt)) return false;
            var age = now - receivedAt;
            return age >= 0f && age <= maximumAgeSeconds;
        }

        public void Remove(uint playerId) { receivedAtByPlayer.Remove(playerId); }
        public void Clear() { receivedAtByPlayer.Clear(); }

        private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
    }
}
