using System;
using HarmonyLib;
using ULTRAKILL.Bloodlink.Core;
using UnityEngine;

namespace ULTRAKILL.Bloodlink.Combat
{
    [HarmonyPatch(typeof(AudioSource), nameof(AudioSource.PlayOneShot), new[] { typeof(AudioClip) })]
    internal static class AudioSourcePlayOneShotDefaultVolumeSyncPatch
    {
        private static void Postfix(AudioSource __instance, AudioClip clip)
        {
            BloodlinkPlugin.Instance?.NotifyLocalWorldAudio(__instance, clip, 1f);
        }
    }

    [HarmonyPatch(typeof(AudioSource), nameof(AudioSource.PlayOneShot), new[] { typeof(AudioClip), typeof(float) })]
    internal static class AudioSourcePlayOneShotSyncPatch
    {
        private static void Postfix(AudioSource __instance, AudioClip clip, float volumeScale)
        {
            BloodlinkPlugin.Instance?.NotifyLocalWorldAudio(__instance, clip, volumeScale);
        }
    }

    [HarmonyPatch(typeof(AudioSource), nameof(AudioSource.Play), new Type[0])]
    internal static class AudioSourcePlaySyncPatch
    {
        private static void Postfix(AudioSource __instance)
        {
            if (__instance != null && !__instance.loop)
                BloodlinkPlugin.Instance?.NotifyLocalWorldAudio(__instance, __instance.clip, 1f);
        }
    }
}
