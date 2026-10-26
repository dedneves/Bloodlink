using System;

namespace ULTRAKILL.Bloodlink.Enemies
{
    public static class EnemyHealthSyncPolicy
    {
        public static bool IsValid(float health)
        {
            return !float.IsNaN(health) && !float.IsInfinity(health) && health >= 0f && health <= 100000f;
        }
    }
}
