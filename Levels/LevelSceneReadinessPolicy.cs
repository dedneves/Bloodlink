using System;

namespace ULTRAKILL.Bloodlink.Levels
{
    public static class LevelSceneReadinessPolicy
    {
        public static bool MatchesCurrentScene(string identity, string logicalScene, string unityScene)
        {
            return !string.IsNullOrWhiteSpace(identity) &&
                (string.Equals(identity, logicalScene, StringComparison.Ordinal) ||
                 string.Equals(identity, unityScene, StringComparison.Ordinal));
        }

        public static bool IsFullyLoaded(string target, string current, string pending,
            bool activeSceneValid, bool activeSceneLoaded)
        {
            // SceneHelper uses Addressables keys ("Level 0-1"); Unity's Scene.name
            // can be an asset GUID. They are different identifiers for the same map.
            // PendingScene is cleared by SceneHelper after Addressables completes.
            return !string.IsNullOrWhiteSpace(target) && string.IsNullOrEmpty(pending) &&
                string.Equals(target, current, StringComparison.Ordinal) && activeSceneValid && activeSceneLoaded;
        }
    }
}
