using System;

namespace ULTRAKILL.Bloodlink.Networking
{
    /// <summary>Retains the last committed checkpoint for one loaded campaign scene.</summary>
    public sealed class CheckpointSessionState
    {
        private string sceneName;
        private string checkpointPath;

        public void BeginScene(string loadedSceneName)
        {
            if (string.IsNullOrWhiteSpace(loadedSceneName)) throw new ArgumentException("Scene name is required.", nameof(loadedSceneName));
            sceneName = loadedSceneName;
            checkpointPath = null;
        }

        public void RecordCommit(string committedSceneName, string path)
        {
            if (string.IsNullOrWhiteSpace(committedSceneName)) throw new ArgumentException("Scene name is required.", nameof(committedSceneName));
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Checkpoint path is required.", nameof(path));
            sceneName = committedSceneName;
            checkpointPath = path;
        }

        public bool TryGetForScene(string requestedSceneName, out string path)
        {
            path = null;
            if (string.IsNullOrWhiteSpace(requestedSceneName) || checkpointPath == null ||
                !string.Equals(sceneName, requestedSceneName, StringComparison.Ordinal)) return false;
            path = checkpointPath;
            return true;
        }
    }
}
