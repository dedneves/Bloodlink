using System.Collections.Generic;

namespace ULTRAKILL.Bloodlink.Networking
{
    // Reliable combat is independent of the world stream and may arrive out of
    // order. Retain a bounded replay window so an earlier valid hit is not lost.
    public sealed class WindowedEventSequenceTracker
    {
        private sealed class Window { public uint Latest; public readonly HashSet<uint> Seen = new HashSet<uint>(); }
        private readonly Dictionary<uint, Window> peers = new Dictionary<uint, Window>();
        public bool TryAccept(uint peer, uint sequence)
        {
            if (peer == 0 || sequence == 0) return false;
            Window window;
            if (!peers.TryGetValue(peer, out window)) peers[peer] = window = new Window { Latest = sequence };
            var delta = unchecked((int)(sequence - window.Latest));
            if (delta > 8192 || delta <= -1024 || !window.Seen.Add(sequence)) return false;
            if (delta > 0) window.Latest = sequence;
            if (window.Seen.Count > 1024)
                window.Seen.RemoveWhere(value => unchecked((int)(window.Latest - value)) >= 1024);
            return true;
        }
        public void Remove(uint peer) => peers.Remove(peer);
        public void Clear() => peers.Clear();
    }
}
