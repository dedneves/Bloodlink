using System.Collections.Generic;

namespace ULTRAKILL.Bloodlink.Enemies
{
    public sealed class EnemyMeleeHitLedger
    {
        private readonly Dictionary<int, HashSet<uint>> attacks = new Dictionary<int, HashSet<uint>>();
        private readonly Dictionary<uint, float> hurtUntil = new Dictionary<uint, float>();

        public void BeginAttack(int attackId) { attacks[attackId] = new HashSet<uint>(); }
        public void EndAttack(int attackId) { attacks.Remove(attackId); }
        public void Reset() { attacks.Clear(); hurtUntil.Clear(); }
        public void ForgetPlayer(uint playerId)
        {
            hurtUntil.Remove(playerId);
            foreach (var hit in attacks.Values) hit.Remove(playerId);
        }

        public bool TryAccept(int attackId, uint playerId, float now, int damage, bool canRepeat)
        {
            if (playerId == 0 || damage <= 0 || float.IsNaN(now) || float.IsInfinity(now)) return false;
            HashSet<uint> hits;
            if (!attacks.TryGetValue(attackId, out hits) || (!canRepeat && hits.Contains(playerId))) return false;
            float until;
            if (hurtUntil.TryGetValue(playerId, out until) && now < until) return false;
            hits.Add(playerId);
            hurtUntil[playerId] = now + (damage >= 50 ? 0.8f : 0.5f);
            return true;
        }
    }
}
