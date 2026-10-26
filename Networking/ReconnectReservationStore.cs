using System;
using System.Collections.Generic;

namespace ULTRAKILL.Bloodlink.Networking
{
    public sealed class ReconnectReservation
    {
        public uint PlayerId { get; private set; }
        public string PlayerName { get; private set; }
        public PlayerSnapshot Snapshot { get; private set; }
        public float ExpiresAt { get; private set; }

        internal ReconnectReservation(uint playerId, string playerName, PlayerSnapshot snapshot, float expiresAt)
        {
            PlayerId = playerId;
            PlayerName = playerName;
            Snapshot = snapshot.Clone();
            ExpiresAt = expiresAt;
        }
    }

    public sealed class ReconnectReservationStore
    {
        private readonly Dictionary<string, ReconnectReservation> reservations = new Dictionary<string, ReconnectReservation>(StringComparer.Ordinal);
        private readonly List<string> expiredKeys = new List<string>(4);
        private readonly object gate = new object();

        public int Count { get { lock (gate) return reservations.Count; } }

        public void Store(string token, uint playerId, string playerName, PlayerSnapshot snapshot, float expiresAt)
        {
            SessionHandshakeCodec.ValidateToken(token);
            if (playerId == 0) throw new ArgumentOutOfRangeException(nameof(playerId));
            if (string.IsNullOrWhiteSpace(playerName)) throw new ArgumentException("Player name is required.", nameof(playerName));
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (!Finite(expiresAt) || expiresAt < 0f) throw new ArgumentOutOfRangeException(nameof(expiresAt));
            lock (gate)
                reservations[token.ToLowerInvariant()] = new ReconnectReservation(playerId, playerName, snapshot.Clone(), expiresAt);
        }

        public bool TryTake(string token, float now, out ReconnectReservation reservation)
        {
            reservation = null;
            if (!SessionHandshakeTokenIsValid(token) || !Finite(now) || now < 0f) return false;
            lock (gate)
            {
                var key = token.ToLowerInvariant();
                if (!reservations.TryGetValue(key, out reservation)) return false;
                reservations.Remove(key);
                if (reservation.ExpiresAt <= now)
                {
                    reservation = null;
                    return false;
                }
                return true;
            }
        }

        public void RemoveExpired(float now)
        {
            RemoveExpired(now, null);
        }

        public void RemoveExpired(float now, IList<uint> expiredPlayerIds)
        {
            if (!Finite(now) || now < 0f) return;
            lock (gate) RemoveExpiredLocked(now, expiredPlayerIds);
        }

        public bool HasLiveReservation(float now)
        {
            if (!Finite(now) || now < 0f) return false;
            lock (gate)
            {
                foreach (var reservation in reservations.Values)
                    if (reservation.ExpiresAt > now) return true;
                return false;
            }
        }

        public void Clear() { lock (gate) reservations.Clear(); }

        private void RemoveExpiredLocked(float now, IList<uint> expiredPlayerIds)
        {
            expiredKeys.Clear();
            foreach (var pair in reservations)
            {
                if (pair.Value.ExpiresAt > now) continue;
                expiredKeys.Add(pair.Key);
                if (expiredPlayerIds != null) expiredPlayerIds.Add(pair.Value.PlayerId);
            }
            for (var i = 0; i < expiredKeys.Count; i++) reservations.Remove(expiredKeys[i]);
            expiredKeys.Clear();
        }

        private static bool SessionHandshakeTokenIsValid(string token)
        {
            try { SessionHandshakeCodec.ValidateToken(token); return true; }
            catch (ArgumentException) { return false; }
        }

        private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
    }
}
