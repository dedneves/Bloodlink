using System;
using System.Collections.Generic;

namespace ULTRAKILL.Bloodlink.Networking
{
    public sealed class RequestReceiptJournal
    {
        private readonly Dictionary<ulong, byte[]> receipts = new Dictionary<ulong, byte[]>();
        private readonly Queue<ulong> order = new Queue<ulong>();
        private readonly int capacity;
        public RequestReceiptJournal(int capacity = 32768) { if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity)); this.capacity = capacity; }
        private static ulong Key(uint player, uint sequence) => ((ulong)player << 32) | sequence;
        public bool TryGet(uint player, uint sequence, out byte[] payload) => receipts.TryGetValue(Key(player, sequence), out payload);
        public void Record(uint player, uint sequence, byte[] payload)
        {
            if (player == 0 || sequence == 0 || payload == null) throw new ArgumentException("A receipt needs an owner, request sequence and payload.");
            var key = Key(player, sequence);
            if (!receipts.ContainsKey(key)) order.Enqueue(key);
            receipts[key] = payload;
            while (order.Count > capacity) receipts.Remove(order.Dequeue());
        }
        public void Clear() { receipts.Clear(); order.Clear(); }
    }
}
