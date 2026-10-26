using System.Collections.Generic;

namespace ULTRAKILL.Bloodlink.Networking
{
    public sealed class ReliableEventSequenceTracker
    {
        private const uint MaximumForwardSequenceJump = 8192;
        private readonly Dictionary<uint, uint> lastSequenceByPeer = new Dictionary<uint, uint>();

        public bool TryAccept(uint peerId, uint sequence)
        {
            if (peerId == 0 || sequence == 0) return false;
            uint previous;
            if (lastSequenceByPeer.TryGetValue(peerId, out previous))
            {
                var delta = unchecked(sequence - previous);
                if (unchecked((int)delta) <= 0 || delta > MaximumForwardSequenceJump) return false;
            }
            lastSequenceByPeer[peerId] = sequence;
            return true;
        }

        public void Remove(uint peerId) { lastSequenceByPeer.Remove(peerId); }
        public void Clear() { lastSequenceByPeer.Clear(); }
    }
}
