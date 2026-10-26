using HarmonyLib;
using ULTRAKILL.Bloodlink.Core;

namespace ULTRAKILL.Bloodlink.Combat
{
    internal static class WeaponFirePatchRelay
    {
        internal static void Fire(byte kind)
        {
            var plugin = BloodlinkPlugin.Instance;
            if (plugin != null) plugin.NotifyLocalWeaponFire(kind);
        }
    }

    [HarmonyPatch(typeof(Revolver), "Shoot")]
    internal static class RevolverFirePatch
    {
        private static void Postfix(int shotType) { WeaponFirePatchRelay.Fire((byte)(shotType == 2 ? 1 : 0)); }
    }

    [HarmonyPatch(typeof(Shotgun), "Shoot")]
    internal static class ShotgunFirePatch
    {
        private static void Postfix() { WeaponFirePatchRelay.Fire(2); }
    }

    [HarmonyPatch(typeof(Shotgun), "ShootSinks")]
    internal static class ShotgunSinksFirePatch
    {
        private static void Postfix() { WeaponFirePatchRelay.Fire(3); }
    }

    [HarmonyPatch(typeof(Shotgun), "ShootSaw", new[] { typeof(bool) })]
    internal static class ShotgunSawFirePatch
    {
        private static void Postfix() { WeaponFirePatchRelay.Fire(4); }
    }

    [HarmonyPatch(typeof(Railcannon), "Shoot")]
    internal static class RailcannonFirePatch
    {
        private static void Postfix() { WeaponFirePatchRelay.Fire(5); }
    }

    [HarmonyPatch(typeof(Nailgun), "Shoot")]
    internal static class NailgunFirePatch
    {
        private static void Postfix() { WeaponFirePatchRelay.Fire(6); }
    }

    [HarmonyPatch(typeof(Nailgun), "ShootMagnet")]
    internal static class NailgunMagnetFirePatch
    {
        private static void Postfix() { WeaponFirePatchRelay.Fire(7); }
    }

    [HarmonyPatch(typeof(Nailgun), "ShootZapper")]
    internal static class NailgunZapperFirePatch
    {
        private static void Postfix() { WeaponFirePatchRelay.Fire(7); }
    }

    [HarmonyPatch(typeof(RocketLauncher), "Shoot")]
    internal static class RocketLauncherFirePatch
    {
        private static void Postfix() { WeaponFirePatchRelay.Fire(7); }
    }

    [HarmonyPatch(typeof(RocketLauncher), "ShootCannonball")]
    internal static class RocketCannonballFirePatch
    {
        private static void Postfix() { WeaponFirePatchRelay.Fire(8); }
    }

    [HarmonyPatch(typeof(RocketLauncher), "ShootNapalm")]
    internal static class RocketNapalmFirePatch
    {
        private static void Postfix() { WeaponFirePatchRelay.Fire(9); }
    }
}
