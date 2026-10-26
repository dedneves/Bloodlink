using System;
using System.IO;
using UnityEngine;

namespace ULTRAKILL.Bloodlink.Networking
{
    public sealed class EnemyDamageState
    {
        public uint NetworkId;
        public uint Frame;
        public uint StableKey;
        public int EnemyType;
        public Vector3 Position;
        public Quaternion Rotation;
        public float Health;
        public bool Dead;
        public float HealthBefore;
        public byte[] ImpactPayload;
    }

    public static class EnemyDamageStateCodec
    {
        public static byte[] Encode(EnemyDamageState state)
        {
            Validate(state);
            using (var stream = new MemoryStream(49))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(state.NetworkId);
                writer.Write(state.Frame);
                writer.Write(state.StableKey);
                writer.Write(state.EnemyType);
                writer.Write(state.Position.x); writer.Write(state.Position.y); writer.Write(state.Position.z);
                writer.Write(state.Rotation.x); writer.Write(state.Rotation.y); writer.Write(state.Rotation.z); writer.Write(state.Rotation.w);
                writer.Write(state.Health);
                writer.Write(state.Dead);
                writer.Write(state.HealthBefore);
                var impact = state.ImpactPayload ?? Array.Empty<byte>();
                writer.Write((ushort)impact.Length); writer.Write(impact);
                return stream.ToArray();
            }
        }

        public static EnemyDamageState Decode(byte[] payload)
        {
            if (payload == null || payload.Length < 55 || payload.Length > 4151) throw new InvalidDataException("Invalid authoritative enemy damage state length.");
            using (var stream = new MemoryStream(payload, false))
            using (var reader = new BinaryReader(stream))
            {
                var state = new EnemyDamageState
                {
                    NetworkId = reader.ReadUInt32(),
                    Frame = reader.ReadUInt32(),
                    StableKey = reader.ReadUInt32(),
                    EnemyType = reader.ReadInt32(),
                    Position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                    Rotation = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                    Health = reader.ReadSingle(),
                    Dead = reader.ReadBoolean(), HealthBefore = reader.ReadSingle()
                };
                var length = reader.ReadUInt16();
                if (length > 4096 || stream.Length - stream.Position != length) throw new InvalidDataException("Invalid native impact length.");
                state.ImpactPayload = reader.ReadBytes(length);
                Validate(state);
                return state;
            }
        }

        private static void Validate(EnemyDamageState state)
        {
            if (state == null || state.NetworkId == 0 || state.Frame == 0 || state.EnemyType < 0 ||
                !Finite(state.HealthBefore) || state.HealthBefore < 0f || state.HealthBefore > 100000f || (state.ImpactPayload != null && state.ImpactPayload.Length > 4096) ||
                !Finite(state.Position.x) || !Finite(state.Position.y) || !Finite(state.Position.z) || state.Position.sqrMagnitude > 100000f * 100000f ||
                !Finite(state.Rotation.x) || !Finite(state.Rotation.y) || !Finite(state.Rotation.z) || !Finite(state.Rotation.w))
                throw new InvalidDataException("Invalid authoritative enemy damage state values.");
            var rotationMagnitude = state.Rotation.x * state.Rotation.x + state.Rotation.y * state.Rotation.y + state.Rotation.z * state.Rotation.z + state.Rotation.w * state.Rotation.w;
            if (rotationMagnitude < 0.01f || rotationMagnitude > 4f || !Finite(state.Health) || state.Health < 0f || state.Health > 100000f)
                throw new InvalidDataException("Invalid authoritative enemy damage state values.");
        }

        private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
    }
}
