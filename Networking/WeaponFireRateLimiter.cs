using System;
using System.Collections.Generic;

namespace ULTRAKILL.Bloodlink.Networking
{
    public sealed class WeaponFireRateLimiter
    {
        private sealed class Bucket
        {
            public float Tokens;
            public float UpdatedAt;
        }

        private readonly Dictionary<uint, Bucket> buckets = new Dictionary<uint, Bucket>();
        private readonly float capacity;
        private readonly float refillPerSecond;

        public WeaponFireRateLimiter(float capacity = 12f, float refillPerSecond = 60f)
        {
            if (capacity < 1f || refillPerSecond <= 0f) throw new ArgumentOutOfRangeException();
            this.capacity = capacity;
            this.refillPerSecond = refillPerSecond;
        }

        public bool TryAccept(uint playerId, float now)
        {
            if (float.IsNaN(now) || float.IsInfinity(now) || now < 0f) return false;
            Bucket bucket;
            if (!buckets.TryGetValue(playerId, out bucket))
            {
                bucket = new Bucket { Tokens = capacity, UpdatedAt = now };
                buckets.Add(playerId, bucket);
            }
            var elapsed = Math.Max(0f, now - bucket.UpdatedAt);
            bucket.Tokens = Math.Min(capacity, bucket.Tokens + elapsed * refillPerSecond);
            bucket.UpdatedAt = Math.Max(bucket.UpdatedAt, now);
            if (bucket.Tokens < 1f) return false;
            bucket.Tokens -= 1f;
            return true;
        }

        public void Remove(uint playerId) { buckets.Remove(playerId); }
        public void Clear() { buckets.Clear(); }
    }
}
