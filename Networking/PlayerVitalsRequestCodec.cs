using System;
using System.IO;

namespace ULTRAKILL.Bloodlink.Networking
{
    public enum PlayerVitalsAction : byte
    {
        Damage = 1,
        Heal = 2,
        Respawn = 3
    }

    public sealed class PlayerVitalsRequest
    {
        public uint Sequence;
        public uint SnapshotTick;
        public PlayerVitalsAction Action;
        public ushort Amount;
    }

    public static class PlayerVitalsRequestCodec
    {
        public static byte[] Encode(uint sequence, uint snapshotTick, PlayerVitalsAction action, int amount)
        {
            if (sequence == 0 || snapshotTick == 0) throw new ArgumentOutOfRangeException();
            if (!Enum.IsDefined(typeof(PlayerVitalsAction), action)) throw new ArgumentOutOfRangeException(nameof(action));
            var maximum = action == PlayerVitalsAction.Damage ? 200 : action == PlayerVitalsAction.Heal ? 1000 : 1;
            if (amount < 1 || amount > maximum || (action == PlayerVitalsAction.Respawn && amount != 1))
                throw new ArgumentOutOfRangeException(nameof(amount));
            using (var stream = new MemoryStream(11))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(sequence);
                writer.Write(snapshotTick);
                writer.Write((byte)action);
                writer.Write((ushort)amount);
                return stream.ToArray();
            }
        }

        public static PlayerVitalsRequest Decode(byte[] payload)
        {
            if (payload == null || payload.Length != 11) throw new InvalidDataException("Invalid player vitals request length.");
            using (var stream = new MemoryStream(payload, false))
            using (var reader = new BinaryReader(stream))
            {
                var sequence = reader.ReadUInt32();
                var snapshotTick = reader.ReadUInt32();
                var action = (PlayerVitalsAction)reader.ReadByte();
                var amount = reader.ReadUInt16();
                if (sequence == 0 || snapshotTick == 0 || !Enum.IsDefined(typeof(PlayerVitalsAction), action) || amount == 0 ||
                    (action == PlayerVitalsAction.Damage && amount > 200) ||
                    (action == PlayerVitalsAction.Heal && amount > 1000) ||
                    (action == PlayerVitalsAction.Respawn && amount != 1))
                    throw new InvalidDataException("Invalid player vitals request values.");
                return new PlayerVitalsRequest { Sequence = sequence, SnapshotTick = snapshotTick, Action = action, Amount = amount };
            }
        }
    }
}
