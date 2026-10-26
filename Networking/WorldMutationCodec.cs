using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace ULTRAKILL.Bloodlink.Networking
{
    internal enum WorldMutationKind : byte { Breakable = 1, Glass = 2 }

    internal sealed class WorldMutation
    {
        public WorldMutationKind Kind;
        public string Scene;
        public string Path;
        public byte ComponentIndex;
        public float Strength;
        public uint RequestId;
        public uint Revision;
        public uint OwnerId;
        public Vector3 Position;
        public bool Broken;
        public float Durability;
        internal Component Target { get; set; }
    }

    internal static class WorldMutationCodec
    {
        private const int MaxSceneBytes = 96;
        private const int MaxPathBytes = 3072;

        public static byte[] Encode(WorldMutation value)
        {
            Validate(value);
            var scene = Encoding.UTF8.GetBytes(value.Scene);
            var path = Encoding.UTF8.GetBytes(value.Path);
            if (scene.Length > MaxSceneBytes || path.Length > MaxPathBytes) throw new InvalidDataException("Scene object identity exceeds the wire limit.");
            using (var stream = new MemoryStream(12 + scene.Length + path.Length))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)value.Kind);
                writer.Write((byte)scene.Length); writer.Write(scene);
                writer.Write((ushort)path.Length); writer.Write(path);
                writer.Write(value.ComponentIndex);
                writer.Write(value.Strength);
                writer.Write(value.RequestId); writer.Write(value.Revision); writer.Write(value.OwnerId);
                writer.Write(value.Position.x); writer.Write(value.Position.y); writer.Write(value.Position.z);
                writer.Write(value.Broken); writer.Write(value.Durability);
                return stream.ToArray();
            }
        }

        public static WorldMutation Decode(byte[] payload)
        {
            if (payload == null || payload.Length < 40) throw new InvalidDataException("World mutation payload is truncated.");
            using (var stream = new MemoryStream(payload, false))
            using (var reader = new BinaryReader(stream))
            {
                var kind = (WorldMutationKind)reader.ReadByte();
                var sceneLength = reader.ReadByte();
                if (sceneLength == 0 || sceneLength > MaxSceneBytes || sceneLength > stream.Length - stream.Position) throw new InvalidDataException("Invalid world mutation scene length.");
                var scene = StrictUtf8(reader.ReadBytes(sceneLength));
                var pathLength = reader.ReadUInt16();
                if (pathLength == 0 || pathLength > MaxPathBytes || pathLength > stream.Length - stream.Position - 34) throw new InvalidDataException("Invalid world mutation path length.");
                var path = StrictUtf8(reader.ReadBytes(pathLength));
                var componentIndex = reader.ReadByte();
                var strength = reader.ReadSingle();
                var result = new WorldMutation { Kind = kind, Scene = scene, Path = path, ComponentIndex = componentIndex, Strength = strength,
                    RequestId = reader.ReadUInt32(), Revision = reader.ReadUInt32(), OwnerId = reader.ReadUInt32(),
                    Position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                    Broken = reader.ReadBoolean(), Durability = reader.ReadSingle() };
                if (stream.Position != stream.Length) throw new InvalidDataException("Trailing world mutation bytes.");
                Validate(result);
                return result;
            }
        }

        private static string StrictUtf8(byte[] value)
        {
            try { return new UTF8Encoding(false, true).GetString(value); }
            catch (DecoderFallbackException ex) { throw new InvalidDataException("Invalid world mutation UTF-8.", ex); }
        }

        private static void Validate(WorldMutation value)
        {
            if (value == null || (value.Kind != WorldMutationKind.Breakable && value.Kind != WorldMutationKind.Glass) ||
                string.IsNullOrWhiteSpace(value.Scene) || string.IsNullOrWhiteSpace(value.Path) ||
                float.IsNaN(value.Strength) || float.IsInfinity(value.Strength) || value.Strength < 0f || value.Strength > 100000f)
                throw new InvalidDataException("Invalid world mutation values.");
            if (float.IsNaN(value.Durability) || float.IsInfinity(value.Durability) || Mathf.Abs(value.Durability) > 1000000f ||
                float.IsNaN(value.Position.sqrMagnitude) || float.IsInfinity(value.Position.sqrMagnitude) || value.Position.sqrMagnitude > 100000f * 100000f)
                throw new InvalidDataException("Invalid world mutation outcome.");
        }
    }
}
