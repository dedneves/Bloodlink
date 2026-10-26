using UnityEngine;
using ULTRAKILL.Enemy;
using GameEnemy = global::Enemy;

namespace ULTRAKILL.Bloodlink.Enemies
{
    /// <summary>Writes host health snapshots into the game's actual enemy health component.</summary>
    public static class EnemyHealthSynchronizer
    {
        public static bool Apply(EnemyIdentifier identity, float health)
        {
            if (identity == null || !EnemyHealthSyncPolicy.IsValid(health)) return false;

            GameEnemy healthOwner = null;
            if (identity.enemyType == EnemyType.Drone || identity.enemyType == EnemyType.Virtue || identity.enemyType == EnemyType.Providence)
            {
                if (identity.drone == null) identity.drone = identity.GetComponent<Drone>();
                if (identity.drone != null) healthOwner = identity.drone.Enemy;
            }
            else if (identity.enemyType == EnemyType.MaliciousFace)
            {
                if (identity.spider == null) identity.spider = identity.GetComponent<MaliciousFace>();
                if (identity.spider != null) healthOwner = identity.spider.spider;
            }
            else
            {
                switch (identity.enemyClass)
                {
                    case EnemyClass.Husk:
                        if (identity.zombie == null) identity.zombie = identity.GetComponent<GameEnemy>();
                        healthOwner = identity.zombie;
                        break;
                    case EnemyClass.Machine:
                        if (identity.machine == null) identity.machine = identity.GetComponent<GameEnemy>();
                        healthOwner = identity.machine;
                        break;
                    case EnemyClass.Demon:
                        if (identity.statue == null) identity.statue = identity.GetComponent<GameEnemy>();
                        healthOwner = identity.statue;
                        break;
                }
            }

            if (healthOwner == null) return false;
            healthOwner.health = health;
            identity.health = health;
            return true;
        }
    }
}
