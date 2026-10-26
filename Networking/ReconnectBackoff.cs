using System;

namespace ULTRAKILL.Bloodlink.Networking
{
    public static class ReconnectBackoff
    {
        public const int MaximumDelaySeconds = 20;

        public static float DelaySeconds(int zeroBasedAttempt)
        {
            if (zeroBasedAttempt < 0) throw new ArgumentOutOfRangeException(nameof(zeroBasedAttempt));
            var delay = 1;
            for (var i = 0; i < zeroBasedAttempt && delay < MaximumDelaySeconds; i++)
                delay = Math.Min(MaximumDelaySeconds, delay * 2);
            return delay;
        }
    }
}
