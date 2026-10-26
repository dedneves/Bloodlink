using System;
using System.IO;
using UnityEngine;

namespace ULTRAKILL.Bloodlink.Networking
{
    public sealed class RocketProjectileEvent
    {
        public uint OwnerPlayerId;
        public byte WeaponVariation;
        // 0 rocket, 1 cannonball, 2 napalm.
        public byte VisualKind;
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Velocity;
        public Vector3 Gravity;
        public Vector3 AngularVelocity;
    }

    public static class RocketProjectileCodec
    {
        private const int LaunchPayloadLength = 66;
        private const int RelayPayloadLength = LaunchPayloadLength + 4;

        public static byte[] EncodeLaunchRequest(RocketProjectileEvent value)
        {
            Validate(value);
            using (var stream = new MemoryStream(LaunchPayloadLength))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(value.WeaponVariation);
                writer.Write(value.VisualKind);
                WriteState(writer, value);
                return stream.ToArray();
            }
        }

        public static byte[] EncodeRelay(uint ownerPlayerId, RocketProjectileEvent value)
        {
            if (ownerPlayerId == 0) throw new ArgumentOutOfRangeException(nameof(ownerPlayerId));
            Validate(value);
            using (var stream = new MemoryStream(RelayPayloadLength))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(ownerPlayerId);
                writer.Write(value.WeaponVariation);
                writer.Write(value.VisualKind);
                WriteState(writer, value);
                return stream.ToArray();
            }
        }

        public static RocketProjectileEvent DecodeLaunchRequest(byte[] payload)
        {
            if (payload == null || payload.Length != LaunchPayloadLength)
                throw new InvalidDataException("Invalid rocket projectile launch request length.");
            using (var stream = new MemoryStream(payload, false))
            using (var reader = new BinaryReader(stream)) return ReadState(reader, 0, reader.ReadByte(), reader.ReadByte());
        }

        public static RocketProjectileEvent DecodeRelay(byte[] payload)
        {
            if (payload == null || payload.Length != RelayPayloadLength)
                throw new InvalidDataException("Invalid rocket projectile relay length.");
            using (var stream = new MemoryStream(payload, false))
            using (var reader = new BinaryReader(stream))
            {
                var owner = reader.ReadUInt32();
                if (owner == 0) throw new InvalidDataException("Invalid rocket projectile owner.");
                return ReadState(reader, owner, reader.ReadByte(), reader.ReadByte());
            }
        }

        public static bool IsLaunchNearPlayer(RocketProjectileEvent value, Vector3 playerPosition, float maximumDistance = 7f)
        {
            if (value == null || !Finite(playerPosition) || !Finite(maximumDistance) || maximumDistance < 0f) return false;
            var difference = value.Position - playerPosition;
            return difference.sqrMagnitude <= maximumDistance * maximumDistance;
        }

        private static void WriteState(BinaryWriter writer, RocketProjectileEvent value)
        {
            WriteVector(writer, value.Position);
            writer.Write(value.Rotation.x); writer.Write(value.Rotation.y); writer.Write(value.Rotation.z); writer.Write(value.Rotation.w);
            WriteVector(writer, value.Velocity);
            WriteVector(writer, value.Gravity);
            WriteVector(writer, value.AngularVelocity);
        }

        private static RocketProjectileEvent ReadState(BinaryReader reader, uint owner, byte variation, byte visualKind)
        {
            var value = new RocketProjectileEvent
            {
                OwnerPlayerId = owner,
                WeaponVariation = variation,
                VisualKind = visualKind,
                Position = ReadVector(reader),
                Rotation = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                Velocity = ReadVector(reader),
                Gravity = ReadVector(reader),
                AngularVelocity = ReadVector(reader)
            };
            if (reader.BaseStream.Position != reader.BaseStream.Length) throw new InvalidDataException("Trailing rocket projectile data.");
            try { Validate(value); }
            catch (ArgumentException ex) { throw new InvalidDataException("Invalid rocket projectile values.", ex); }
            return value;
        }

        private static void Validate(RocketProjectileEvent value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (value.WeaponVariation > 8 || value.VisualKind > 2 ||
                !Finite(value.Position) || value.Position.sqrMagnitude > 100000f * 100000f ||
                !Finite(value.Rotation) || value.Rotation.x * value.Rotation.x + value.Rotation.y * value.Rotation.y +
                    value.Rotation.z * value.Rotation.z + value.Rotation.w * value.Rotation.w < 0.25f ||
                value.Rotation.x * value.Rotation.x + value.Rotation.y * value.Rotation.y +
                    value.Rotation.z * value.Rotation.z + value.Rotation.w * value.Rotation.w > 2.25f ||
                !Finite(value.Velocity) || value.Velocity.sqrMagnitude < 1f || value.Velocity.sqrMagnitude > 600f * 600f ||
                !Finite(value.Gravity) || value.Gravity.sqrMagnitude > 20f * 20f ||
                !Finite(value.AngularVelocity) || value.AngularVelocity.sqrMagnitude > 1000f * 1000f)
                throw new ArgumentOutOfRangeException(nameof(value), "Rocket projectile state is outside supported bounds.");
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
            return Finite(value.x) && Finite(value.y) && Finite(value.z);
        }

        private static bool Finite(Quaternion value)
        {
            return Finite(value.x) && Finite(value.y) && Finite(value.z) && Finite(value.w);
        }

        private static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }

    public static class RocketProjectilePolicy
    {
        public static bool IsAuthorizedLaunch(PlayerSnapshot player, RocketProjectileEvent launch)
        {
            if (player == null || launch == null || (player.MovementFlags & 4) != 0 ||
                player.WeaponSlot != 5 || player.WeaponVariation != launch.WeaponVariation ||
                !WeaponInventoryPolicy.IsUnlocked(player.WeaponInventoryMask, 5, player.WeaponVariation) ||
                !DamageRequestPolicy.IsSelectedWeapon("RocketLauncher", player.WeaponName) ||
                !RocketProjectileCodec.IsLaunchNearPlayer(launch, player.Position) ||
                !WeaponFireCodec.IsPlausibleAimRotation(launch.Rotation, player.ViewRotation, 155f)) return false;
            return true;
        }
    }
}
