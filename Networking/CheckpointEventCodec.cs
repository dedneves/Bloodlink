using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace ULTRAKILL.Bloodlink.Networking
{
    public enum CheckpointEventKind : byte
    {
        Request = 1,
        Commit = 2
    }

    public sealed class CheckpointEventData
    {
        public CheckpointEventKind Kind { get; private set; }
        public string Path { get; private set; }
        public Vector3 Position { get; private set; }
        public bool HasPosition { get; private set; }

        internal CheckpointEventData(CheckpointEventKind kind, string path, Vector3? position)
        {
            Kind = kind;
            Path = path;
            HasPosition = position.HasValue;
            Position = position ?? Vector3.zero;
        }
    }

    public static class CheckpointEventCodec
    {
        private const int MaximumPathBytes = 4096;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static byte[] Encode(CheckpointEventKind kind, string path, Vector3? position = null)
        {
            if (kind != CheckpointEventKind.Request && kind != CheckpointEventKind.Commit)
                throw new ArgumentOutOfRangeException(nameof(kind));
            ValidatePath(path);
            if (position.HasValue && !ValidPosition(position.Value)) throw new ArgumentException("Invalid checkpoint position.", nameof(position));
            var pathBytes = StrictUtf8.GetBytes(path);
            if (pathBytes.Length > MaximumPathBytes) throw new ArgumentOutOfRangeException(nameof(path));
            using (var stream = new MemoryStream(pathBytes.Length + 2))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)kind);
                writer.Write((ushort)pathBytes.Length);
                writer.Write(pathBytes);
                writer.Write(position.HasValue);
                if (position.HasValue) { writer.Write(position.Value.x); writer.Write(position.Value.y); writer.Write(position.Value.z); }
                return stream.ToArray();
            }
        }

        public static CheckpointEventData Decode(byte[] payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            using (var stream = new MemoryStream(payload, false))
            using (var reader = new BinaryReader(stream))
            {
                if (stream.Length < 5) throw new InvalidDataException("Checkpoint event is too short.");
                var kind = (CheckpointEventKind)reader.ReadByte();
                var length = reader.ReadUInt16();
                if ((kind != CheckpointEventKind.Request && kind != CheckpointEventKind.Commit) ||
                    length == 0 || length > MaximumPathBytes || stream.Length - stream.Position < length + 1)
                    throw new InvalidDataException("Invalid checkpoint event kind or length.");
                string path;
                try { path = StrictUtf8.GetString(reader.ReadBytes(length)); }
                catch (DecoderFallbackException ex) { throw new InvalidDataException("Invalid checkpoint path encoding.", ex); }
                try { ValidatePath(path); }
                catch (ArgumentException ex) { throw new InvalidDataException("Invalid checkpoint path.", ex); }
                Vector3? position = null;
                if (reader.ReadBoolean()) position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                if (stream.Position != stream.Length || (position.HasValue && !ValidPosition(position.Value)))
                    throw new InvalidDataException("Invalid checkpoint position or trailing data.");
                return new CheckpointEventData(kind, path, position);
            }
        }

        private static void ValidatePath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Length > MaximumPathBytes)
                throw new ArgumentException("Checkpoint path is empty or too long.", nameof(path));
            foreach (var segment in path.Split('/'))
            {
                string name; int ordinal; int legacyIndex;
                if (!StableHierarchyPath.TryDecodeSegment(segment, out name, out ordinal) &&
                    (!int.TryParse(segment, out legacyIndex) || legacyIndex < 0))
                    throw new ArgumentException("Invalid checkpoint identity segment.", nameof(path));
            }
        }

        private static bool ValidPosition(Vector3 value) => !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
            !float.IsNaN(value.y) && !float.IsInfinity(value.y) && !float.IsNaN(value.z) && !float.IsInfinity(value.z) && value.sqrMagnitude < 100000f * 100000f;
    }
}
