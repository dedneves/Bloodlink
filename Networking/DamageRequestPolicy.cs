using System;

namespace ULTRAKILL.Bloodlink.Networking
{
    public static class DamageRequestPolicy
    {
        public const float MaximumDamageMultiplier = 25f;
        public const float MaximumCriticalMultiplier = 5f;
        public const float MaximumForceMagnitude = 20000f;
        public const float MaximumWorldCoordinate = 100000f;

        public static bool IsValid(uint enemyId, string weaponName, float multiplier, float criticalMultiplier,
            bool ignoreEnemyDamageScale, float forceX, float forceY, float forceZ,
            float hitX, float hitY, float hitZ, string hitter = null)
        {
            if (enemyId == 0 || string.IsNullOrWhiteSpace(weaponName) || weaponName.Length > 48) return false;
            var normalizedWeapon = NormalizeWeaponName(weaponName);
            var maximumMultiplier = GetMaximumDamageMultiplier(normalizedWeapon);
            var maximumForce = MaximumForceMagnitude;
            if (IsFistWeapon(weaponName))
            {
                if (hitter == "punch") { maximumMultiplier = 1f; maximumForce = 25000f; }
                else if (hitter == "heavypunch") { maximumMultiplier = 2.5f; maximumForce = 100000f; }
                else if (hitter == "drillpunch") { maximumMultiplier = 10f; maximumForce = 1000f; }
                else return false;
            }
            var maximumCriticalMultiplier = normalizedWeapon.StartsWith("Revolver", StringComparison.Ordinal) ||
                normalizedWeapon.StartsWith("Railcannon", StringComparison.Ordinal) ||
                normalizedWeapon.StartsWith("Railgun", StringComparison.Ordinal) || hitter == "coin"
                ? MaximumCriticalMultiplier
                : 0f;
            if (!Finite(multiplier) || multiplier < 0f || multiplier > MaximumDamageMultiplier ||
                !Finite(criticalMultiplier) || criticalMultiplier < 0f || criticalMultiplier > maximumCriticalMultiplier) return false;
            if (multiplier > maximumMultiplier) return false;
            if (!Finite(forceX) || !Finite(forceY) || !Finite(forceZ) ||
                forceX * forceX + forceY * forceY + forceZ * forceZ > maximumForce * maximumForce) return false;
            if (!Finite(hitX) || !Finite(hitY) || !Finite(hitZ) ||
                hitX * hitX + hitY * hitY + hitZ * hitZ > MaximumWorldCoordinate * MaximumWorldCoordinate) return false;
            return true;
        }

        public static bool IsSelectedWeapon(string requestedWeapon, string selectedWeapon)
        {
            if (string.IsNullOrWhiteSpace(requestedWeapon) || string.IsNullOrWhiteSpace(selectedWeapon)) return false;
            return string.Equals(NormalizeWeaponName(requestedWeapon), NormalizeWeaponName(selectedWeapon), StringComparison.Ordinal);
        }

        public static bool IsImpactNearTarget(float hitX, float hitY, float hitZ,
            float targetX, float targetY, float targetZ, float maximumDistance)
        {
            if (!Finite(hitX) || !Finite(hitY) || !Finite(hitZ) ||
                !Finite(targetX) || !Finite(targetY) || !Finite(targetZ) ||
                !Finite(maximumDistance) || maximumDistance < 0f) return false;

            var dx = hitX - targetX;
            var dy = hitY - targetY;
            var dz = hitZ - targetZ;
            return dx * dx + dy * dy + dz * dz <= maximumDistance * maximumDistance;
        }

        public static bool IsValidHitterForWeapon(string weaponName, string hitter)
        {
            var weapon = NormalizeWeaponName(weaponName);
            if (IsFistWeapon(weapon)) return hitter == "punch" || hitter == "heavypunch" || hitter == "drillpunch";
            if (string.IsNullOrEmpty(hitter)) return IsSupportedWeaponName(weapon);
            if (hitter.Length > 32) return false;
            if (weapon.StartsWith("Revolver", StringComparison.Ordinal))
                return hitter == "revolver" || hitter == "coin" || hitter == "explosion" || hitter == "ffexplosion";
            if (weapon.StartsWith("Shotgun", StringComparison.Ordinal))
                return hitter == "shotgun" || hitter == "shotgunzone" || hitter == "hammer" || hitter == "hammerzone" ||
                    hitter == "chainsaw" || hitter == "chainsawbounce" || hitter == "chainsawzone" ||
                    hitter == "explosion" || hitter == "ffexplosion";
            if (weapon.StartsWith("Railcannon", StringComparison.Ordinal) || weapon.StartsWith("Railgun", StringComparison.Ordinal))
                return hitter == "railcannon" || hitter == "lightningbolt" || hitter == "harpoon" || hitter == "drill" ||
                    hitter == "explosion" || hitter == "ffexplosion";
            if (weapon.StartsWith("Nailgun", StringComparison.Ordinal))
                return hitter == "nail" || hitter == "sawblade" || hitter == "chainsawprojectile" || hitter == "drill" ||
                    hitter == "zap" || hitter == "zapper" || hitter == "explosion" || hitter == "ffexplosion";
            if (weapon.StartsWith("RocketLauncher", StringComparison.Ordinal) || weapon.StartsWith("Rocket Launcher", StringComparison.Ordinal))
                return hitter == "rocket" || hitter == "cannonball" || hitter == "explosion" || hitter == "ffexplosion" || hitter == "heavypunch";
            return false;
        }

        public static bool IsSupportedWeaponName(string weaponName)
        {
            var weapon = NormalizeWeaponName(weaponName);
            return IsFistWeapon(weapon) || weapon.StartsWith("Revolver", StringComparison.Ordinal) ||
                weapon.StartsWith("Shotgun", StringComparison.Ordinal) ||
                weapon.StartsWith("Railcannon", StringComparison.Ordinal) || weapon.StartsWith("Railgun", StringComparison.Ordinal) ||
                weapon.StartsWith("Nailgun", StringComparison.Ordinal) ||
                weapon.StartsWith("RocketLauncher", StringComparison.Ordinal) || weapon.StartsWith("Rocket Launcher", StringComparison.Ordinal);
        }

        public static bool IsFistWeapon(string weaponName)
        {
            return string.Equals(NormalizeWeaponName(weaponName), "Fist", StringComparison.Ordinal);
        }

        public static bool IsFistHitter(string hitter)
        {
            return hitter == "punch" || hitter == "heavypunch" || hitter == "drillpunch";
        }

        public static bool CanRelayWeaponlessDamage(string hitter)
        {
            return IsFistHitter(hitter);
        }

        private static string NormalizeWeaponName(string name)
        {
            const string cloneSuffix = "(Clone)";
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;
            var trimmed = name.Trim();
            return trimmed.EndsWith(cloneSuffix, StringComparison.Ordinal)
                ? trimmed.Substring(0, trimmed.Length - cloneSuffix.Length)
                : trimmed;
        }

        private static float GetMaximumDamageMultiplier(string weaponName)
        {
            if (weaponName.StartsWith("Revolver", StringComparison.Ordinal)) return 12f;
            if (weaponName.StartsWith("Shotgun", StringComparison.Ordinal)) return 20f;
            if (weaponName.StartsWith("Railcannon", StringComparison.Ordinal) || weaponName.StartsWith("Railgun", StringComparison.Ordinal)) return 20f;
            if (weaponName.StartsWith("Nailgun", StringComparison.Ordinal)) return 20f;
            if (weaponName.StartsWith("RocketLauncher", StringComparison.Ordinal) || weaponName.StartsWith("Rocket Launcher", StringComparison.Ordinal)) return 20f;
            return MaximumDamageMultiplier;
        }

        private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
    }
}
