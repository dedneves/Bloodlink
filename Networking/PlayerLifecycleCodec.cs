using System;
using System.IO;

namespace ULTRAKILL.Bloodlink.Networking
{
    public sealed class PlayerLifecycleUpdate
    {
        public uint PlayerId { get; private set; }
        public ushort Health { get; private set; }
        public bool IsDead { get; private set; }
        public uint SnapshotTick { get; private set; }

        internal PlayerLifecycleUpdate(uint playerId, ushort health, bool isDead, uint snapshotTick)
        {
            PlayerId = playerId;
            Health = health;
            IsDead = isDead;
            SnapshotTick = snapshotTick;
        }
    }

    public static class PlayerLifecycleCodec
    {
        public static byte[] Encode(uint playerId, ushort health, bool isDead, uint snapshotTick)
        {
            if (playerId == 0) throw new ArgumentOutOfRangeException(nameof(playerId));
            if (health > 200) throw new ArgumentOutOfRangeException(nameof(health));
            using (var stream = new MemoryStream(11))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(playerId);
                writer.Write(health);
                writer.Write(isDead);
                writer.Write(snapshotTick);
                return stream.ToArray();
            }
        }

        public static PlayerLifecycleUpdate Decode(byte[] payload)
        {
            if (payload == null || payload.Length != 11) throw new InvalidDataException("Invalid player lifecycle payload length.");
            using (var stream = new MemoryStream(payload, false))
            using (var reader = new BinaryReader(stream))
            {
                var playerId = reader.ReadUInt32();
                var health = reader.ReadUInt16();
                var dead = reader.ReadBoolean();
                var snapshotTick = reader.ReadUInt32();
                if (playerId == 0 || health > 200) throw new InvalidDataException("Invalid player lifecycle values.");
                return new PlayerLifecycleUpdate(playerId, health, dead, snapshotTick);
            }
        }
    }
}
