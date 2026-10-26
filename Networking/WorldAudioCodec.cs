using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace ULTRAKILL.Bloodlink.Networking
{
    public sealed class WorldAudioEvent
    {
        public uint OwnerPlayerId;
        public string ClipName;
        public Vector3 Position;
        public float Volume;
        public float Pitch;
        public float SpatialBlend;
    }

    public static class WorldAudioCodec
    {
        private const int MaximumClipNameBytes = 96;
        private const int PayloadLengthWithoutName = 29;

        public static byte[] Encode(WorldAudioEvent value)
        {
            Validate(value);
            var name = Encoding.UTF8.GetBytes(value.ClipName);
            if (name.Length == 0 || name.Length > MaximumClipNameBytes)
                throw new InvalidDataException("World audio clip name is outside the wire limit.");
            using (var stream = new MemoryStream(PayloadLengthWithoutName + name.Length))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(value.OwnerPlayerId);
                writer.Write((byte)name.Length);
                writer.Write(name);
                writer.Write(value.Position.x);
                writer.Write(value.Position.y);
                writer.Write(value.Position.z);
                writer.Write(value.Volume);
                writer.Write(value.Pitch);
                writer.Write(value.SpatialBlend);
                return stream.ToArray();
            }
        }

        public static WorldAudioEvent Decode(byte[] payload)
        {
            if (payload == null || payload.Length < PayloadLengthWithoutName + 1)
                throw new InvalidDataException("World audio payload is truncated.");
            using (var stream = new MemoryStream(payload, false))
            using (var reader = new BinaryReader(stream))
            {
                var owner = reader.ReadUInt32();
                var nameLength = reader.ReadByte();
                if (nameLength == 0 || nameLength > MaximumClipNameBytes ||
                    nameLength != stream.Length - stream.Position - 24)
                    throw new InvalidDataException("Invalid world audio clip name length.");
                string clipName;
                try { clipName = new UTF8Encoding(false, true).GetString(reader.ReadBytes(nameLength)); }
                catch (DecoderFallbackException ex) { throw new InvalidDataException("Invalid UTF-8 in world audio clip name.", ex); }
                var value = new WorldAudioEvent
                {
                    OwnerPlayerId = owner,
                    ClipName = clipName,
                    Position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                    Volume = reader.ReadSingle(),
                    Pitch = reader.ReadSingle(),
                    SpatialBlend = reader.ReadSingle()
                };
                if (stream.Position != stream.Length) throw new InvalidDataException("Trailing world audio bytes.");
                Validate(value);
                return value;
            }
        }

        private static void Validate(WorldAudioEvent value)
        {
            if (value == null || value.OwnerPlayerId == 0 || string.IsNullOrWhiteSpace(value.ClipName) ||
                !IsFinite(value.Position.x) || !IsFinite(value.Position.y) || !IsFinite(value.Position.z) ||
                Mathf.Abs(value.Position.x) > 100000f || Mathf.Abs(value.Position.y) > 100000f || Mathf.Abs(value.Position.z) > 100000f ||
                !IsFinite(value.Volume) || value.Volume < 0f || value.Volume > 2f ||
                !IsFinite(value.Pitch) || value.Pitch < 0.1f || value.Pitch > 3f ||
                !IsFinite(value.SpatialBlend) || value.SpatialBlend < 0f || value.SpatialBlend > 1f)
                throw new InvalidDataException("Invalid world audio event values.");
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
