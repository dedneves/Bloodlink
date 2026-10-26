using System;
using System.IO;

namespace ULTRAKILL.Bloodlink.Networking
{
    public sealed class EnemyDespawnEvent
    {
        public uint NetworkId;
        public uint StableKey;
        public int EnemyType;
    }

    public static class EnemyDespawnCodec
    {
        public static byte[] Encode(uint networkId, uint stableKey, int enemyType)
        {
            if (networkId == 0) throw new ArgumentOutOfRangeException(nameof(networkId));
            if (stableKey == 0) throw new ArgumentOutOfRangeException(nameof(stableKey));
            using (var stream = new MemoryStream(12))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(networkId);
                writer.Write(stableKey);
                writer.Write(enemyType);
                return stream.ToArray();
            }
        }

        public static EnemyDespawnEvent Decode(byte[] payload)
        {
            if (payload == null || payload.Length != 12) throw new InvalidDataException("Invalid enemy despawn payload length.");
            using (var stream = new MemoryStream(payload, false))
            using (var reader = new BinaryReader(stream))
            {
                var result = new EnemyDespawnEvent
                {
                    NetworkId = reader.ReadUInt32(),
                    StableKey = reader.ReadUInt32(),
                    EnemyType = reader.ReadInt32()
                };
                if (result.NetworkId == 0 || result.StableKey == 0) throw new InvalidDataException("Invalid enemy despawn identity.");
                if (!Enum.IsDefined(typeof(EnemyType), (EnemyType)result.EnemyType)) throw new InvalidDataException("Invalid enemy despawn type.");
                return result;
            }
        }
    }
}
