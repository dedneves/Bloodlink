using HarmonyLib;
using System.Collections.Generic;
using ULTRAKILL.Bloodlink.Core;
using UnityEngine;

namespace ULTRAKILL.Bloodlink.Levels
{
    internal static class SessionDifficultyOverride
    {
        private static bool active;
        private static int difficulty;
        private static readonly Dictionary<NewMovement, int> previousDifficulties = new Dictionary<NewMovement, int>();
        private static readonly System.Reflection.FieldInfo movementDifficultyField =
            AccessTools.Field(typeof(NewMovement), "difficulty");

        public static bool IsActive { get { return active; } }
        public static int Value { get { return active ? difficulty : 2; } }

        public static void Apply(int value)
        {
            if (value < 0 || value > 4) return;
            difficulty = value;
            active = true;
            ApplyToLoadedPlayers();
        }

        public static void ApplyToLoadedPlayers()
        {
            if (!active || movementDifficultyField == null) return;
            foreach (var movement in Object.FindObjectsOfType<NewMovement>())
            {
                if (movement == null) continue;
                if (!previousDifficulties.ContainsKey(movement))
                    previousDifficulties.Add(movement, (int)movementDifficultyField.GetValue(movement));
                movementDifficultyField.SetValue(movement, difficulty);
            }
        }

        public static void Clear()
        {
            if (movementDifficultyField != null)
            {
                foreach (var entry in previousDifficulties)
                    if (entry.Key != null) movementDifficultyField.SetValue(entry.Key, entry.Value);
            }
            previousDifficulties.Clear();
            active = false;
            difficulty = 2;
        }

        [HarmonyPatch(typeof(PrefsManager), "GetInt", new[] { typeof(string), typeof(int) })]
        private static class DifficultyPreferencePatch
        {
            private static bool Prefix(string key, ref int __result)
            {
                var plugin = BloodlinkPlugin.Instance;
                if (!active || plugin == null || !plugin.IsConnected || key != "difficulty") return true;
                __result = difficulty;
                return false;
            }
        }
    }
}
