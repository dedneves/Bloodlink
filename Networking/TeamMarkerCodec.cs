using System;
using System.IO;
using UnityEngine;

namespace ULTRAKILL.Bloodlink.Networking
{
    public sealed class TeamMarker
    {
        public uint OwnerPlayerId;
        public uint Sequence;
        public Vector3 Position;
        public byte Kind;
    }

    public static class TeamMarkerCodec
    {
        public static byte[] EncodeRequest(uint sequence, Vector3 position, byte kind)
        {
            Validate(sequence, position, kind);
            using (var stream = new MemoryStream(17))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(sequence);
                WritePosition(writer, position);
                writer.Write(kind);
                return stream.ToArray();
            }
        }

        public static TeamMarker DecodeRequest(byte[] payload)
        {
            if (payload == null || payload.Length != 17) throw new InvalidDataException("Invalid team marker request length.");
            using (var stream = new MemoryStream(payload, false))
            using (var reader = new BinaryReader(stream))
            {
                var marker = new TeamMarker
                {
                    Sequence = reader.ReadUInt32(),
                    Position = ReadPosition(reader),
                    Kind = reader.ReadByte()
                };
                Validate(marker.Sequence, marker.Position, marker.Kind);
                return marker;
            }
        }

        public static byte[] EncodeRelay(uint ownerPlayerId, uint sequence, Vector3 position, byte kind)
        {
            if (ownerPlayerId == 0) throw new ArgumentOutOfRangeException(nameof(ownerPlayerId));
            Validate(sequence, position, kind);
            using (var stream = new MemoryStream(21))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(ownerPlayerId);
                writer.Write(sequence);
                WritePosition(writer, position);
                writer.Write(kind);
                return stream.ToArray();
            }
        }

        public static TeamMarker DecodeRelay(byte[] payload)
        {
            if (payload == null || payload.Length != 21) throw new InvalidDataException("Invalid team marker relay length.");
            using (var stream = new MemoryStream(payload, false))
            using (var reader = new BinaryReader(stream))
            {
                var marker = new TeamMarker
                {
                    OwnerPlayerId = reader.ReadUInt32(),
                    Sequence = reader.ReadUInt32(),
                    Position = ReadPosition(reader),
                    Kind = reader.ReadByte()
                };
                if (marker.OwnerPlayerId == 0) throw new InvalidDataException("Invalid team marker owner.");
                Validate(marker.Sequence, marker.Position, marker.Kind);
                return marker;
            }
        }

        private static void Validate(uint sequence, Vector3 position, byte kind)
        {
            if (sequence == 0 || kind > 1 || !Finite(position) || position.sqrMagnitude > 100000f * 100000f)
                throw new InvalidDataException("Invalid team marker values.");
        }

        private static bool Finite(Vector3 value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
                !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
                !float.IsNaN(value.z) && !float.IsInfinity(value.z);
        }

        private static void WritePosition(BinaryWriter writer, Vector3 position)
        {
            writer.Write(position.x);
            writer.Write(position.y);
            writer.Write(position.z);
        }

        private static Vector3 ReadPosition(BinaryReader reader)
        {
            return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }
    }
}
