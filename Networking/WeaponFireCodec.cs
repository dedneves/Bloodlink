using System;
using System.IO;
using UnityEngine;

namespace ULTRAKILL.Bloodlink.Networking
{
    public sealed class WeaponFireEvent
    {
        public uint OwnerPlayerId;
        public byte WeaponSlot;
        public byte WeaponVariation;
        public byte FireKind;
        public bool WeaponAlternate;
        public Quaternion AimRotation;
    }

    public static class WeaponFireCodec
    {
        public static bool IsValidWeaponFire(byte weaponSlot, byte fireKind)
        {
            switch (weaponSlot)
            {
                case 1: return fireKind <= 1; // Revolver: normal or alternate shot.
                case 2: return fireKind >= 2 && fireKind <= 4; // Shotgun: primary, Sinks or Saw.
                case 3: return fireKind == 6 || fireKind == 7; // Nailgun: primary, Magnet or Zapper.
                case 4: return fireKind == 5; // Railcannon.
                case 5: return fireKind >= 7 && fireKind <= 9; // Rocket, Cannonball or Napalm.
                default: return false;
            }
        }

        public static byte[] Encode(byte weaponSlot, byte weaponVariation, byte fireKind)
        {
            return Encode(weaponSlot, weaponVariation, fireKind, Quaternion.identity);
        }

        public static byte[] Encode(byte weaponSlot, byte weaponVariation, byte fireKind, Quaternion aimRotation, bool weaponAlternate = false)
        {
            if (weaponSlot > 5) throw new ArgumentOutOfRangeException(nameof(weaponSlot));
            if (weaponVariation > 8) throw new ArgumentOutOfRangeException(nameof(weaponVariation));
            if (fireKind > 9) throw new ArgumentOutOfRangeException(nameof(fireKind));
            if (!IsValidWeaponFire(weaponSlot, fireKind)) throw new ArgumentException("Fire kind does not belong to this weapon slot.", nameof(fireKind));
            if (!IsValidRotation(aimRotation)) throw new ArgumentException("Aim rotation must be a finite unit quaternion.", nameof(aimRotation));
            using (var stream = new MemoryStream(19))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(weaponSlot);
                writer.Write(weaponVariation);
                writer.Write(fireKind);
                writer.Write(aimRotation.x); writer.Write(aimRotation.y); writer.Write(aimRotation.z); writer.Write(aimRotation.w);
                writer.Write(weaponAlternate);
                return stream.ToArray();
            }
        }

        public static byte[] EncodeRelay(uint ownerPlayerId, WeaponFireEvent fire)
        {
            if (ownerPlayerId == 0) throw new ArgumentOutOfRangeException(nameof(ownerPlayerId));
            if (fire == null) throw new ArgumentNullException(nameof(fire));
            var body = Encode(fire.WeaponSlot, fire.WeaponVariation, fire.FireKind, fire.AimRotation, fire.WeaponAlternate);
            using (var stream = new MemoryStream(4 + body.Length))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(ownerPlayerId);
                writer.Write(body);
                return stream.ToArray();
            }
        }

        public static byte[] EncodeRelay(uint ownerPlayerId, byte weaponSlot, byte weaponVariation, byte fireKind, Quaternion aimRotation, bool weaponAlternate = false)
        {
            return EncodeRelay(ownerPlayerId, new WeaponFireEvent
            {
                WeaponSlot = weaponSlot,
                WeaponVariation = weaponVariation,
                FireKind = fireKind,
                AimRotation = aimRotation, WeaponAlternate = weaponAlternate
            });
        }

        public static WeaponFireEvent DecodeRelay(byte[] payload)
        {
            if (payload == null || payload.Length != 24) throw new InvalidDataException("Invalid relayed weapon-fire event length.");
            using (var stream = new MemoryStream(payload, false))
            using (var reader = new BinaryReader(stream))
            {
                var ownerPlayerId = reader.ReadUInt32();
                if (ownerPlayerId == 0) throw new InvalidDataException("Invalid relayed weapon-fire owner.");
                var body = reader.ReadBytes(20);
                if (body.Length != 20) throw new InvalidDataException("Truncated relayed weapon-fire event.");
                var fire = Decode(body);
                fire.OwnerPlayerId = ownerPlayerId;
                return fire;
            }
        }

        public static WeaponFireEvent Decode(byte[] payload)
        {
            if (payload == null || payload.Length != 20) throw new InvalidDataException("Invalid weapon-fire event length.");
            if (payload[0] > 5 || payload[1] > 8 || payload[2] > 9 || !IsValidWeaponFire(payload[0], payload[2]))
                throw new InvalidDataException("Invalid weapon-fire event values.");
            using (var stream = new MemoryStream(payload, false))
            using (var reader = new BinaryReader(stream))
            {
                reader.ReadBytes(3);
                var aimRotation = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                if (!IsValidRotation(aimRotation)) throw new InvalidDataException("Invalid weapon-fire aim rotation.");
                return new WeaponFireEvent { WeaponSlot = payload[0], WeaponVariation = payload[1], FireKind = payload[2], AimRotation = NormalizeRotation(aimRotation), WeaponAlternate = reader.ReadBoolean() };
            }
        }

        public static bool IsPlausibleAimRotation(Quaternion aimRotation, Quaternion snapshotRotation, float maximumDifferenceDegrees = 120f)
        {
            if (!IsValidRotation(aimRotation) || !IsValidRotation(snapshotRotation) ||
                !Finite(maximumDifferenceDegrees) || maximumDifferenceDegrees < 0f || maximumDifferenceDegrees > 180f) return false;
            var aim = NormalizeRotation(aimRotation);
            var snapshot = NormalizeRotation(snapshotRotation);
            var dot = Math.Abs((double)aim.x * snapshot.x + (double)aim.y * snapshot.y + (double)aim.z * snapshot.z + (double)aim.w * snapshot.w);
            dot = Math.Min(1d, Math.Max(0d, dot));
            var difference = 2d * Math.Acos(dot) * (180d / Math.PI);
            return difference <= maximumDifferenceDegrees;
        }

        private static bool IsValidRotation(Quaternion value)
        {
            if (!Finite(value.x) || !Finite(value.y) || !Finite(value.z) || !Finite(value.w)) return false;
            var magnitude = value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w;
            return magnitude >= 0.25f && magnitude <= 2.25f;
        }

        private static Quaternion NormalizeRotation(Quaternion value)
        {
            var inverseMagnitude = 1f / (float)Math.Sqrt(value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w);
            return new Quaternion(value.x * inverseMagnitude, value.y * inverseMagnitude, value.z * inverseMagnitude, value.w * inverseMagnitude);
        }

        private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
    }
}
