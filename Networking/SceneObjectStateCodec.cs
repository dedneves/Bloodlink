using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace ULTRAKILL.Bloodlink.Networking
{
    internal enum SceneObjectOperation : byte { SetActive = 1, Destroy = 2, Pose = 3 }

    internal sealed class SceneObjectState
    {
        public uint Sequence;
        public uint Revision;
        public uint OwnerId;
        public SceneObjectOperation Operation;
        public string Scene;
        public string Path;
        public string ComponentType;
        public byte ComponentIndex;
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Velocity;
        public float Delay;
        public bool Active;
    }

    internal static class SceneObjectStateCodec
    {
        private const int MaxScene = 96;
        private const int MaxPath = 3072;
        private const int MaxComponentType = 256;

        public static byte[] Encode(SceneObjectState state)
        {
            Validate(state);
            var scene = Encoding.UTF8.GetBytes(state.Scene);
            var path = Encoding.UTF8.GetBytes(state.Path);
            var componentType = Encoding.UTF8.GetBytes(state.ComponentType ?? string.Empty);
            if (scene.Length > MaxScene || path.Length > MaxPath || componentType.Length > MaxComponentType) throw new InvalidDataException("Scene object identity exceeds limits.");
            using (var stream = new MemoryStream(67 + scene.Length + path.Length + componentType.Length))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(state.Sequence); writer.Write(state.Revision); writer.Write(state.OwnerId);
                writer.Write((byte)state.Operation); writer.Write((byte)scene.Length); writer.Write(scene);
                writer.Write((ushort)path.Length); writer.Write(path);
                writer.Write(state.ComponentIndex); writer.Write((ushort)componentType.Length); writer.Write(componentType);
                writer.Write(state.Position.x); writer.Write(state.Position.y); writer.Write(state.Position.z);
                writer.Write(state.Rotation.x); writer.Write(state.Rotation.y); writer.Write(state.Rotation.z); writer.Write(state.Rotation.w);
                writer.Write(state.Velocity.x); writer.Write(state.Velocity.y); writer.Write(state.Velocity.z);
                writer.Write(state.Delay); writer.Write(state.Active);
                return stream.ToArray();
            }
        }

        public static SceneObjectState Decode(byte[] payload)
        {
            if (payload == null || payload.Length < 66) throw new InvalidDataException("Scene object state is truncated.");
            using (var stream = new MemoryStream(payload, false))
            using (var reader = new BinaryReader(stream))
            {
                var state = new SceneObjectState { Sequence = reader.ReadUInt32(), Revision = reader.ReadUInt32(), OwnerId = reader.ReadUInt32(),
                    Operation = (SceneObjectOperation)reader.ReadByte() };
                var sceneLength = reader.ReadByte();
                if (sceneLength == 0 || sceneLength > MaxScene || sceneLength > stream.Length - stream.Position) throw new InvalidDataException("Invalid scene name length.");
                state.Scene = ReadUtf8(reader.ReadBytes(sceneLength));
                var pathLength = reader.ReadUInt16();
                if (pathLength == 0 || pathLength > MaxPath || pathLength > stream.Length - stream.Position - 48) throw new InvalidDataException("Invalid scene path length.");
                state.Path = ReadUtf8(reader.ReadBytes(pathLength));
                state.ComponentIndex = reader.ReadByte();
                var componentTypeLength = reader.ReadUInt16();
                if (componentTypeLength > MaxComponentType || componentTypeLength > stream.Length - stream.Position - 45) throw new InvalidDataException("Invalid component type length.");
                state.ComponentType = ReadUtf8(reader.ReadBytes(componentTypeLength));
                state.Position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                state.Rotation = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                state.Velocity = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                state.Delay = reader.ReadSingle(); state.Active = reader.ReadBoolean();
                if (stream.Position != stream.Length) throw new InvalidDataException("Trailing scene object bytes.");
                Validate(state);
                return state;
            }
        }

        private static string ReadUtf8(byte[] bytes)
        {
            try { return new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException error) { throw new InvalidDataException("Invalid scene object UTF-8.", error); }
        }

        private static void Validate(SceneObjectState state)
        {
            if (state == null || state.Sequence == 0 || !Enum.IsDefined(typeof(SceneObjectOperation), state.Operation) ||
                string.IsNullOrWhiteSpace(state.Scene) || string.IsNullOrWhiteSpace(state.Path) ||
                (state.Operation != SceneObjectOperation.Destroy && !string.IsNullOrEmpty(state.ComponentType)) ||
                !Finite(state.Position) || !Finite(state.Velocity) || !Finite(state.Rotation) ||
                state.Position.sqrMagnitude > 100000f * 100000f || state.Velocity.sqrMagnitude > 10000f * 10000f ||
                float.IsNaN(state.Delay) || float.IsInfinity(state.Delay) || state.Delay < 0f || state.Delay > 60f)
                throw new InvalidDataException("Invalid scene object state values.");
        }

        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        private static bool Finite(Quaternion value) => Finite(value.x) && Finite(value.y) && Finite(value.z) && Finite(value.w) &&
            value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w > 0.00001f;
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
