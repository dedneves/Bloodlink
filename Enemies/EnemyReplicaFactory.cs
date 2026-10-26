using UnityEngine;

namespace ULTRAKILL.Bloodlink.Enemies
{
    internal static class EnemyReplicaFactory
    {
        internal static EnemyIdentifier Create(EnemyType type, Vector3 position, Quaternion rotation)
        {
            EnemyIdentifier prefab = null;
            foreach (var candidate in Resources.FindObjectsOfTypeAll<EnemyIdentifier>())
            {
                if (candidate == null || candidate.enemyType != type || candidate.gameObject.scene.IsValid()) continue;
                prefab = candidate; break;
            }
            if (prefab == null) return null;
            var guard = new GameObject("Bloodlink inactive enemy construction");
            guard.SetActive(false);
            GameObject replica = null;
            try
            {
                replica = Object.Instantiate(prefab.gameObject, guard.transform, false);
                replica.name = prefab.name + " (Bloodlink replica)";
                replica.transform.SetPositionAndRotation(position, rotation);
                var identity = replica.GetComponent<EnemyIdentifier>();
                if (identity == null) { Object.Destroy(replica); return null; }
                var authority = replica.GetComponent<EnemyReplicaAuthority>();
                if (authority == null) authority = replica.AddComponent<EnemyReplicaAuthority>();
                authority.Activate();
                replica.SetActive(true);
                replica.transform.SetParent(null, true);
                return identity;
            }
            finally { Object.Destroy(guard); }
        }
    }
}
