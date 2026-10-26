using System;
using UnityEngine;

namespace ULTRAKILL.Bloodlink.Networking
{
    public static class PlayerAppearanceColor
    {
        public const uint DefaultPrimary = 0xCC2F3D;
        public const uint DefaultSecondary = 0x30343B;
        public const uint DefaultLight = 0x75D9FF;
        public const byte DefaultGlow = 96;

        public static uint Pack(Color color)
        {
            return ((uint)Mathf.RoundToInt(Mathf.Clamp01(color.r) * 255f) << 16) |
                   ((uint)Mathf.RoundToInt(Mathf.Clamp01(color.g) * 255f) << 8) |
                   (uint)Mathf.RoundToInt(Mathf.Clamp01(color.b) * 255f);
        }

        public static Color Unpack(uint packed)
        {
            if (packed > 0xFFFFFF) throw new ArgumentOutOfRangeException(nameof(packed));
            return new Color(((packed >> 16) & 0xFF) / 255f, ((packed >> 8) & 0xFF) / 255f, (packed & 0xFF) / 255f, 1f);
        }
    }
}
