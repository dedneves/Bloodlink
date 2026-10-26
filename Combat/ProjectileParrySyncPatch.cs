using HarmonyLib;
using ULTRAKILL.Bloodlink.Core;
using ULTRAKILL.Bloodlink.Enemies;
using UnityEngine;

namespace ULTRAKILL.Bloodlink.Combat
{
    [HarmonyPatch(typeof(Punch), "ParryProjectile")]
    internal static class RemoteEnemyProjectileParryPatch
    {
        private static bool Prefix(Projectile proj)
        {
            if (proj != null && proj.GetComponent<RemoteEnemyProjectile>() != null)
            {
                BloodlinkPlugin.Instance?.RequestEnemyProjectileParry(proj);
                return false;
            }
            return true;
        }
    }
}
