using System;
using System.IO;
using System.Text;
using System.Linq;
using ULTRAKILL.Bloodlink.Enemies;
using ULTRAKILL.Bloodlink.Networking;
using UnityEngine;

namespace ULTRAKILL.Bloodlink.Combat
{
    internal sealed class DamageRequestData
    {
        public uint RequestSequence, EnemyId, StableKey, ShooterTick;
        public int EnemyType;
        public Vector3 EnemyPosition, ShooterPosition, ShotDirection, Force, HitPoint;
        public string TargetPath, WeaponName, Hitter;
        public float Multiplier, CritMultiplier;
        public bool TryForExplode, IgnoreDamageMultiplier, FromExplosion;
        public int[] HitterAttributes;
    }

    internal static class DamageRequestCodec
    {
        public static byte[] Encode(uint sequence, uint enemyId, EnemyIdentifier enemy, GameObject target,
            PlayerSnapshot shooter, Vector3 origin, Vector3 force, Vector3 hitPoint, float multiplier,
            bool explode, float critical, GameObject sourceWeapon, bool ignoreScale, bool fromExplosion)
        {
            if (enemy == null || shooter == null) throw new ArgumentNullException();
            return Encode(new DamageRequestData { RequestSequence = sequence, EnemyId = enemyId,
                StableKey = EnemyIdentityKey.For(enemy.transform), EnemyType = (int)enemy.enemyType,
                EnemyPosition = enemy.transform.position, ShooterTick = shooter.Tick,
                ShooterPosition = origin, ShotDirection = shooter.ViewRotation * Vector3.forward,
                TargetPath = RelativePath(target != null ? target.transform : null, enemy.transform),
                WeaponName = sourceWeapon != null ? sourceWeapon.name : DamageRequestPolicy.IsFistHitter(enemy.hitter) ? "Fist" : shooter.WeaponName ?? "Unarmed",
                Hitter = enemy.hitter ?? "", HitterAttributes = enemy.hitterAttributes != null ? enemy.hitterAttributes.Select(value => (int)value).ToArray() : Array.Empty<int>(), Force = force, HitPoint = hitPoint, Multiplier = multiplier,
                CritMultiplier = critical, TryForExplode = explode, IgnoreDamageMultiplier = ignoreScale, FromExplosion = fromExplosion });
        }

        public static byte[] Encode(DamageRequestData data)
        {
            Validate(data);
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(data.RequestSequence); writer.Write(data.EnemyId); writer.Write(data.StableKey); writer.Write(data.EnemyType);
                WriteVector(writer, data.EnemyPosition); writer.Write(data.ShooterTick);
                WriteVector(writer, data.ShooterPosition); WriteVector(writer, data.ShotDirection);
                WriteString(writer, data.TargetPath, 2048); WriteString(writer, data.WeaponName, 256); WriteString(writer, data.Hitter, 128);
                WriteVector(writer, data.Force); WriteVector(writer, data.HitPoint);
                writer.Write(data.Multiplier); writer.Write(data.CritMultiplier);
                writer.Write(data.TryForExplode); writer.Write(data.IgnoreDamageMultiplier); writer.Write(data.FromExplosion);
                var attributes = data.HitterAttributes ?? Array.Empty<int>();
                writer.Write((byte)attributes.Length); foreach (var attribute in attributes) writer.Write(attribute);
                return stream.ToArray();
            }
        }

        public static DamageRequestData Decode(byte[] payload)
        {
            if (payload == null || payload.Length > 4096) throw new InvalidDataException("Invalid native damage request length.");
            using (var stream = new MemoryStream(payload, false))
            using (var reader = new BinaryReader(stream))
            {
                var data = new DamageRequestData { RequestSequence = reader.ReadUInt32(), EnemyId = reader.ReadUInt32(), StableKey = reader.ReadUInt32(), EnemyType = reader.ReadInt32(),
                    EnemyPosition = ReadVector(reader), ShooterTick = reader.ReadUInt32(), ShooterPosition = ReadVector(reader), ShotDirection = ReadVector(reader),
                    TargetPath = ReadString(reader, 2048), WeaponName = ReadString(reader, 256), Hitter = ReadString(reader, 128),
                    Force = ReadVector(reader), HitPoint = ReadVector(reader), Multiplier = reader.ReadSingle(), CritMultiplier = reader.ReadSingle(),
                    TryForExplode = reader.ReadBoolean(), IgnoreDamageMultiplier = reader.ReadBoolean(), FromExplosion = reader.ReadBoolean() };
                var count = reader.ReadByte(); if (count > 32) throw new InvalidDataException("Too many hit attributes.");
                data.HitterAttributes = new int[count]; for (var i = 0; i < count; i++) data.HitterAttributes[i] = reader.ReadInt32();
                if (stream.Position != stream.Length) throw new InvalidDataException("Trailing native hit bytes.");
                Validate(data); return data;
            }
        }

        private static void Validate(DamageRequestData data)
        {
            if (data == null || data.RequestSequence == 0 || (data.EnemyId == 0 && data.StableKey == 0) || data.EnemyType < 0 || (data.HitterAttributes != null && data.HitterAttributes.Length > 32) ||
                !Finite(data.Multiplier) || data.Multiplier < 0f || data.Multiplier > 100000f ||
                !Finite(data.CritMultiplier) || data.CritMultiplier < 0f || data.CritMultiplier > 10000f ||
                !VectorValid(data.EnemyPosition) || !VectorValid(data.ShooterPosition) || !VectorValid(data.HitPoint) ||
                !VectorValid(data.Force, 10000000f) || !VectorValid(data.ShotDirection))
                throw new InvalidDataException("Invalid native hit values.");
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool VectorValid(Vector3 value, float maximum = 100000f) => Finite(value.x) && Finite(value.y) && Finite(value.z) && value.sqrMagnitude <= maximum * maximum;
        private static string RelativePath(Transform target, Transform root)
        {
            if (target == null || target == root) return "";
            var parts = new System.Collections.Generic.Stack<string>();
            for (var item = target; item != null; item = item.parent)
            { if (item == root) return string.Join("/", parts.ToArray()); parts.Push(item.name); }
            return "";
        }
        private static void WriteString(BinaryWriter writer, string value, int maximum)
        { var bytes = Encoding.UTF8.GetBytes(value ?? ""); if (bytes.Length > maximum) throw new InvalidDataException("Native hit string too long."); writer.Write((ushort)bytes.Length); writer.Write(bytes); }
        private static string ReadString(BinaryReader reader, int maximum)
        { var length = reader.ReadUInt16(); if (length > maximum) throw new InvalidDataException("Native hit string too long."); var bytes = reader.ReadBytes(length); if (bytes.Length != length) throw new EndOfStreamException(); return new UTF8Encoding(false, true).GetString(bytes); }
        private static void WriteVector(BinaryWriter writer, Vector3 value) { writer.Write(value.x); writer.Write(value.y); writer.Write(value.z); }
        private static Vector3 ReadVector(BinaryReader reader) => new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }

}
