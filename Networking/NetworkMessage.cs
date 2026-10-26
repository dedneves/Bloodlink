using System;
using System.IO;

namespace ULTRAKILL.Bloodlink.Networking
{
    public enum MessageType : byte
    {
        Hello = 1,
        Welcome = 2,
        PlayerSnapshot = 3,
        Ping = 4,
        Pong = 5,
        Disconnect = 6,
        PlayerLeft = 7,
        DiscoveryRequest = 8,
        DiscoveryResponse = 9,
        DamageRequest = 10,
        EnemySnapshot = 11,
        LevelTransition = 12,
        PlayerLifecycle = 13,
        CheckpointEvent = 14,
        WeaponFire = 15,
        JoinSessionState = 16,
        EnemyDamageState = 17,
        LevelProgression = 18,
        PlayerAnimation = 19,
        CoinProjectile = 20,
        NailProjectile = 21,
        RocketProjectile = 22,
        PlayerVitalsRequest = 23,
        TeamMarker = 24,
        EnemyProjectileSnapshot = 25,
        EnemyExplosion = 26,
        DoorState = 27,
        DoorRequest = 28,
        WeaponPickupRequest = 29,
        WeaponPickupGrant = 30,
        EnemyDespawn = 31,
        EnemyProjectileParryRequest = 32,
        EnemyProjectileParryResult = 33,
        BonusPickupRequest = 34,
        BonusPickup = 35,
        WorldMutationRequest = 36,
        WorldMutation = 37,
        WorldAudio = 38,
        ItemRequest = 39,
        ItemState = 40,
        ResyncRequest = 41,
        SceneObjectRequest = 42,
        SceneObjectState = 43,
        EnemyPresence = 44,
        SessionRules = 45
    }

    public sealed class NetworkMessage
    {
        public const byte CurrentProtocolVersion = 56;
        public const int HeaderLength = 16;
        public const int MaximumPayloadLength = 1024 * 1024;
        private const uint Magic = 0x4B4C4242; // "BBLK" as little-endian bytes.

        public byte ProtocolVersion { get; }
        public MessageType Type { get; }
        public uint Sequence { get; }
        public byte[] Payload { get; }

        public NetworkMessage(MessageType type, uint sequence, byte[] payload)
            : this(CurrentProtocolVersion, type, sequence, payload)
        {
        }

        private NetworkMessage(byte protocolVersion, MessageType type, uint sequence, byte[] payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (payload.Length > MaximumPayloadLength) throw new ArgumentOutOfRangeException(nameof(payload));
            ProtocolVersion = protocolVersion;
            Type = type;
            Sequence = sequence;
            Payload = payload;
        }

        public byte[] Encode()
        {
            using (var stream = new MemoryStream(HeaderLength + Payload.Length))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Magic);
                writer.Write(ProtocolVersion);
                writer.Write((byte)Type);
                writer.Write((ushort)0); // Reserved for future flags.
                writer.Write(Sequence);
                writer.Write((uint)Payload.Length);
                writer.Write(Payload);
                return stream.ToArray();
            }
        }

        public static NetworkMessage Decode(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (data.Length < HeaderLength) throw new InvalidDataException("Packet is shorter than the protocol header.");

            using (var stream = new MemoryStream(data, false))
            using (var reader = new BinaryReader(stream))
            {
                if (reader.ReadUInt32() != Magic) throw new InvalidDataException("Invalid Bloodlink packet magic.");
                var version = reader.ReadByte();
                if (version != CurrentProtocolVersion) throw new InvalidDataException("Unsupported Bloodlink protocol version.");

                var type = (MessageType)reader.ReadByte();
                if (!Enum.IsDefined(typeof(MessageType), type)) throw new InvalidDataException("Unknown Bloodlink message type.");
                reader.ReadUInt16(); // Reserved.
                var sequence = reader.ReadUInt32();
                var payloadLength = reader.ReadUInt32();
                if (payloadLength > MaximumPayloadLength || payloadLength != data.Length - HeaderLength)
                    throw new InvalidDataException("Invalid Bloodlink payload length.");

                var payload = reader.ReadBytes((int)payloadLength);
                return new NetworkMessage(version, type, sequence, payload);
            }
        }
    }
}




