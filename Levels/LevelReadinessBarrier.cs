using System;
using System.Collections.Generic;
using ULTRAKILL.Bloodlink.Networking;

namespace ULTRAKILL.Bloodlink.Levels
{
    /// <summary>Tracks which participants have loaded a specific host-selected scene.</summary>
    public sealed class LevelReadinessBarrier
    {
        private readonly HashSet<uint> participants = new HashSet<uint>();
        private readonly HashSet<uint> readyPlayers = new HashSet<uint>();
        private uint hostId;
        private bool hostReady;

        public uint TransitionId { get; private set; }
        public string SceneName { get; private set; }
        public bool IsStarted { get; private set; }
        public bool IsHostReady { get { return hostReady; } }
        public int ReadyCount { get { return readyPlayers.Count; } }
        public int ParticipantCount { get { return participants.Count; } }

        public void Begin(uint transitionId, string sceneName, IEnumerable<uint> playerIds, uint localHostId)
        {
            if (transitionId == 0) throw new ArgumentOutOfRangeException(nameof(transitionId));
            if (string.IsNullOrWhiteSpace(sceneName)) throw new ArgumentException("Scene name is required.", nameof(sceneName));
            if (playerIds == null) throw new ArgumentNullException(nameof(playerIds));
            if (localHostId == 0) throw new ArgumentOutOfRangeException(nameof(localHostId));

            TransitionId = transitionId;
            SceneName = sceneName;
            hostId = localHostId;
            hostReady = false;
            IsStarted = false;
            participants.Clear();
            readyPlayers.Clear();
            foreach (var playerId in playerIds) AddPlayer(playerId);
        }

        public void AddPlayer(uint playerId)
        {
            if (playerId != 0 && playerId != hostId) participants.Add(playerId);
        }

        public void RemovePlayer(uint playerId)
        {
            participants.Remove(playerId);
            readyPlayers.Remove(playerId);
        }

        public bool MarkHostReady(uint transitionId, string sceneName)
        {
            if (!Matches(transitionId, sceneName)) return false;
            hostReady = true;
            return true;
        }

        public bool MarkPlayerReady(uint playerId, uint transitionId, string sceneName)
        {
            if (playerId == 0 || playerId == hostId || !participants.Contains(playerId) || !Matches(transitionId, sceneName)) return false;
            readyPlayers.Add(playerId);
            return true;
        }

        public bool IsPlayerReady(uint playerId)
        {
            return readyPlayers.Contains(playerId);
        }

        public bool CanStart()
        {
            if (TransitionId == 0 || IsStarted || !hostReady) return false;
            return readyPlayers.Count == participants.Count;
        }

        // Kept for source compatibility with earlier callers. A timeout can never
        // replace explicit readiness from every connected participant.
        public bool CanStart(bool ignoredTimeout)
        {
            return CanStart();
        }

        public void MarkStarted()
        {
            if (!CanStart()) throw new InvalidOperationException("The host and every connected player must be ready before starting the barrier.");
            IsStarted = true;
        }

        public uint[] GetReadyPlayerIds()
        {
            var ids = new List<uint>(readyPlayers.Count + (hostReady ? 1 : 0));
            if (hostReady) ids.Add(hostId);
            ids.AddRange(readyPlayers);
            ids.Sort();
            return ids.ToArray();
        }

        public void Reset()
        {
            TransitionId = 0;
            SceneName = null;
            hostId = 0;
            hostReady = false;
            IsStarted = false;
            participants.Clear();
            readyPlayers.Clear();
        }

        private bool Matches(uint transitionId, string sceneName)
        {
            return TransitionId != 0 && transitionId == TransitionId &&
                string.Equals(SceneName, sceneName, StringComparison.Ordinal);
        }
    }
}
