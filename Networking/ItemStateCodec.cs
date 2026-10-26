using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace ULTRAKILL.Bloodlink.Networking
{
    public enum ItemAction : byte { PickUp = 1, PutDown = 2, Pose = 3 }
    public sealed class ItemState
    {
        public uint Sequence, Revision, OwnerId, ActorId;
        public string Scene, Path, ParentPath, SourcePath;
        public byte ComponentIndex;
        public ItemAction Action;
        public Vector3 Position, Velocity;
        public Quaternion Rotation = Quaternion.identity;
    }
    public static class ItemStateCodec
    {
        public static byte[] Encode(ItemState state)
        {
            Validate(state);
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(state.Sequence); writer.Write(state.Revision); writer.Write(state.OwnerId);
                writer.Write(state.ActorId);
                writer.Write((byte)state.Action); writer.Write(state.ComponentIndex);
                WriteString(writer, state.Scene); WriteString(writer, state.Path); WriteString(writer, state.ParentPath); WriteString(writer, state.SourcePath);
                Vector(writer, state.Position); Vector(writer, state.Velocity);
                writer.Write(state.Rotation.x); writer.Write(state.Rotation.y); writer.Write(state.Rotation.z); writer.Write(state.Rotation.w);
                return stream.ToArray();
            }
        }
        public static ItemState Decode(byte[] payload)
        {
            if (payload == null || payload.Length > 16384) throw new InvalidDataException("Invalid item state size.");
            using (var stream = new MemoryStream(payload, false))
            using (var reader = new BinaryReader(stream))
            {
                var state = new ItemState { Sequence = reader.ReadUInt32(), Revision = reader.ReadUInt32(), OwnerId = reader.ReadUInt32(), ActorId = reader.ReadUInt32(),
                    Action = (ItemAction)reader.ReadByte(), ComponentIndex = reader.ReadByte(),
                    Scene = ReadString(reader), Path = ReadString(reader), ParentPath = ReadString(reader), SourcePath = ReadString(reader),
                    Position = Vector(reader), Velocity = Vector(reader), Rotation = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()) };
                if (stream.Position != stream.Length) throw new InvalidDataException("Trailing item bytes."); Validate(state); return state;
            }
        }
        private static void Validate(ItemState state)
        {
            if (state == null || string.IsNullOrEmpty(state.Scene) || string.IsNullOrEmpty(state.Path) ||
                state.Action < ItemAction.PickUp || state.Action > ItemAction.Pose || !Finite(state.Position.sqrMagnitude) ||
                !Finite(state.Velocity.sqrMagnitude) || state.Position.sqrMagnitude > 1e10f || state.Velocity.sqrMagnitude > 1e8f ||
                !Finite(state.Rotation.x) || !Finite(state.Rotation.y) || !Finite(state.Rotation.z) || !Finite(state.Rotation.w) ||
                state.Rotation.x * state.Rotation.x + state.Rotation.y * state.Rotation.y + state.Rotation.z * state.Rotation.z + state.Rotation.w * state.Rotation.w < 0.01f)
                throw new InvalidDataException("Invalid item state values.");
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static void WriteString(BinaryWriter writer, string value) { var bytes = Encoding.UTF8.GetBytes(value ?? ""); if (bytes.Length > 3072) throw new InvalidDataException("Item identity too long."); writer.Write((ushort)bytes.Length); writer.Write(bytes); }
        private static string ReadString(BinaryReader reader) { var size = reader.ReadUInt16(); if (size > 3072) throw new InvalidDataException("Item identity too long."); var bytes = reader.ReadBytes(size); if (bytes.Length != size) throw new EndOfStreamException(); return new UTF8Encoding(false, true).GetString(bytes); }
        private static void Vector(BinaryWriter writer, Vector3 value) { writer.Write(value.x); writer.Write(value.y); writer.Write(value.z); }
        private static Vector3 Vector(BinaryReader reader) => new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }
}
