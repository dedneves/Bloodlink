using System;
using System.Collections.Generic;

namespace ULTRAKILL.Bloodlink.Networking
{
    public sealed class LatencyWindow
    {
        private readonly Queue<int> samples;
        private readonly int capacity;

        public LatencyWindow(int capacity = 128)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            this.capacity = capacity;
            samples = new Queue<int>(capacity);
        }

        public int Count { get { return samples.Count; } }

        public void Add(int milliseconds)
        {
            if (milliseconds < 0) return;
            while (samples.Count >= capacity) samples.Dequeue();
            samples.Enqueue(milliseconds);
        }

        public int Percentile95
        {
            get
            {
                if (samples.Count == 0) return 0;
                var ordered = new List<int>(samples);
                ordered.Sort();
                var index = (int)Math.Ceiling(ordered.Count * 0.95d) - 1;
                return ordered[Math.Max(0, Math.Min(index, ordered.Count - 1))];
            }
        }

        public void Clear() { samples.Clear(); }
    }
}
