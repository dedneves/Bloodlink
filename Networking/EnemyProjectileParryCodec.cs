using System;
using System.IO;
using UnityEngine;

namespace ULTRAKILL.Bloodlink.Networking
{
    public sealed class EnemyProjectileParryRequest
    {
        public uint Sequence;
        public uint SnapshotTick;
        public uint ProjectileId;
    }

    public sealed class EnemyProjectileParryResult
    {
        public uint ProjectileId;
        public uint PlayerId;
        public Vector3 Position;
        public Vector3 Velocity;
    }

    public static class EnemyProjectileParryCodec
    {
        public static byte[] EncodeRequest(uint sequence, uint snapshotTick, uint projectileId)
        {
            if (sequence == 0 || snapshotTick == 0 || projectileId == 0) throw new ArgumentOutOfRangeException();
            using (var stream = new MemoryStream(12))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(sequence);
                writer.Write(snapshotTick);
                writer.Write(projectileId);
                return stream.ToArray();
            }
        }

        public static EnemyProjectileParryRequest DecodeRequest(byte[] payload)
        {
            if (payload == null || payload.Length != 12) throw new InvalidDataException("Invalid projectile parry request length.");
            using (var stream = new MemoryStream(payload, false))
            using (var reader = new BinaryReader(stream))
            {
                var request = new EnemyProjectileParryRequest
                {
                    Sequence = reader.ReadUInt32(),
                    SnapshotTick = reader.ReadUInt32(),
                    ProjectileId = reader.ReadUInt32()
                };
                if (request.Sequence == 0 || request.SnapshotTick == 0 || request.ProjectileId == 0)
                    throw new InvalidDataException("Invalid projectile parry request values.");
                return request;
            }
        }

        public static byte[] EncodeResult(EnemyProjectileParryResult result)
        {
            Validate(result);
            using (var stream = new MemoryStream(32))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(result.ProjectileId);
                writer.Write(result.PlayerId);
                WriteVector(writer, result.Position);
                WriteVector(writer, result.Velocity);
                return stream.ToArray();
            }
        }

        public static EnemyProjectileParryResult DecodeResult(byte[] payload)
        {
            if (payload == null || payload.Length != 32) throw new InvalidDataException("Invalid projectile parry result length.");
            using (var stream = new MemoryStream(payload, false))
            using (var reader = new BinaryReader(stream))
            {
                var result = new EnemyProjectileParryResult
                {
                    ProjectileId = reader.ReadUInt32(),
                    PlayerId = reader.ReadUInt32(),
                    Position = ReadVector(reader),
                    Velocity = ReadVector(reader)
                };
                Validate(result);
                return result;
            }
        }

        private static void Validate(EnemyProjectileParryResult result)
        {
            if (result == null || result.ProjectileId == 0 || result.PlayerId == 0 ||
                !Finite(result.Position) || result.Position.sqrMagnitude > 100000f * 100000f ||
                !Finite(result.Velocity) || result.Velocity.sqrMagnitude > 100000000f)
                throw new InvalidDataException("Invalid projectile parry result values.");
        }

        private static void WriteVector(BinaryWriter writer, Vector3 value)
        {
            writer.Write(value.x); writer.Write(value.y); writer.Write(value.z);
        }

        private static Vector3 ReadVector(BinaryReader reader)
        {
            return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        private static bool Finite(Vector3 value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
                !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
                !float.IsNaN(value.z) && !float.IsInfinity(value.z);
        }
    }
}
