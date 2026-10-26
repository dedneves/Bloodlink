using ULTRAKILL.Bloodlink.Levels;
using UnityEngine;

namespace ULTRAKILL.Bloodlink.Enemies
{
    public static class EnemyIdentityKey
    {
        public static uint HashPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return 0;
            const uint offset = 2166136261;
            const uint prime = 16777619;
            var hash = offset;
            for (var i = 0; i < path.Length; i++)
            {
                hash ^= path[i];
                hash *= prime;
            }
            return hash == 0 ? 1u : hash;
        }

        public static uint For(Transform transform)
        {
            return SceneObjectIdentity.GetStableTransformKey(transform);
        }
    }
}
