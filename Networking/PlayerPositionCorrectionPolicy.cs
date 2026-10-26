using System;

namespace ULTRAKILL.Bloodlink.Networking
{
    public static class PlayerPositionCorrectionPolicy
    {
        public static bool ShouldCorrect(float squaredDistance)
        {
            return !float.IsNaN(squaredDistance) && !float.IsInfinity(squaredDistance) &&
                squaredDistance > 0.075f * 0.075f && squaredDistance <= 8f * 8f;
        }

        public static float MaximumStep(float deltaTime)
        {
            if (float.IsNaN(deltaTime) || float.IsInfinity(deltaTime) || deltaTime <= 0f) return 0f;
            return 12f * Math.Min(deltaTime, 0.05f);
        }

        public static float RemoteBlend(float deltaTime)
        {
            if (float.IsNaN(deltaTime) || float.IsInfinity(deltaTime) || deltaTime <= 0f) return 0f;
            return (float)(1.0 - Math.Exp(-35.0 * deltaTime));
        }
    }
}
