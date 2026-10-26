using System;
using System.IO;
using UnityEngine;

namespace ULTRAKILL.Bloodlink.Networking
{
    public sealed class CoinProjectileEvent
    {
        public uint OwnerPlayerId;
        public byte WeaponVariation;
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Velocity;
        public Vector3 Gravity;
        public Vector3 AngularVelocity;
    }

    public static class CoinProjectileCodec
    {
        private const int LaunchPayloadLength = 65;
        private const int RelayPayloadLength = LaunchPayloadLength + 4;

        public static byte[] EncodeLaunchRequest(CoinProjectileEvent value)
        {
            Validate(value);
            using (var stream = new MemoryStream(LaunchPayloadLength))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(value.WeaponVariation);
                WriteState(writer, value);
                return stream.ToArray();
            }
        }

        public static byte[] EncodeRelay(uint ownerPlayerId, CoinProjectileEvent value)
        {
            if (ownerPlayerId == 0) throw new ArgumentOutOfRangeException(nameof(ownerPlayerId));
            Validate(value);
            using (var stream = new MemoryStream(RelayPayloadLength))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(ownerPlayerId);
                writer.Write(value.WeaponVariation);
                WriteState(writer, value);
                return stream.ToArray();
            }
        }

        public static CoinProjectileEvent DecodeLaunchRequest(byte[] payload)
        {
            if (payload == null || payload.Length != LaunchPayloadLength)
                throw new InvalidDataException("Invalid coin launch request length.");
            using (var stream = new MemoryStream(payload, false))
            using (var reader = new BinaryReader(stream)) return ReadState(reader, 0, reader.ReadByte());
        }

        public static CoinProjectileEvent DecodeRelay(byte[] payload)
        {
            if (payload == null || payload.Length != RelayPayloadLength)
                throw new InvalidDataException("Invalid coin relay length.");
            using (var stream = new MemoryStream(payload, false))
            using (var reader = new BinaryReader(stream))
            {
                var owner = reader.ReadUInt32();
                if (owner == 0) throw new InvalidDataException("Invalid coin owner.");
                return ReadState(reader, owner, reader.ReadByte());
            }
        }

        public static bool IsLaunchNearPlayer(CoinProjectileEvent value, Vector3 playerPosition, float maximumDistance = 4f)
        {
            if (value == null || !Finite(playerPosition) || !Finite(maximumDistance) || maximumDistance < 0f) return false;
            var difference = value.Position - playerPosition;
            return difference.sqrMagnitude <= maximumDistance * maximumDistance;
        }

        private static void WriteState(BinaryWriter writer, CoinProjectileEvent value)
        {
            WriteVector(writer, value.Position);
            writer.Write(value.Rotation.x); writer.Write(value.Rotation.y); writer.Write(value.Rotation.z); writer.Write(value.Rotation.w);
            WriteVector(writer, value.Velocity);
            WriteVector(writer, value.Gravity);
            WriteVector(writer, value.AngularVelocity);
        }

        private static CoinProjectileEvent ReadState(BinaryReader reader, uint owner, byte variation)
        {
            var value = new CoinProjectileEvent
            {
                OwnerPlayerId = owner,
                WeaponVariation = variation,
                Position = ReadVector(reader),
                Rotation = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                Velocity = ReadVector(reader),
                Gravity = ReadVector(reader),
                AngularVelocity = ReadVector(reader)
            };
            if (reader.BaseStream.Position != reader.BaseStream.Length) throw new InvalidDataException("Trailing coin projectile data.");
            try { Validate(value); }
            catch (ArgumentException ex) { throw new InvalidDataException("Invalid coin projectile values.", ex); }
            return value;
        }

        private static void Validate(CoinProjectileEvent value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (value.WeaponVariation > 8) throw new ArgumentOutOfRangeException(nameof(value), "Invalid coin weapon variation.");
            if (!Finite(value.Position) || value.Position.sqrMagnitude > 100000f * 100000f ||
                !Finite(value.Rotation) || value.Rotation.x * value.Rotation.x + value.Rotation.y * value.Rotation.y +
                    value.Rotation.z * value.Rotation.z + value.Rotation.w * value.Rotation.w < 0.25f ||
                value.Rotation.x * value.Rotation.x + value.Rotation.y * value.Rotation.y +
                    value.Rotation.z * value.Rotation.z + value.Rotation.w * value.Rotation.w > 2.25f ||
                !Finite(value.Velocity) || value.Velocity.sqrMagnitude > 2000f * 2000f ||
                !Finite(value.Gravity) || value.Gravity.sqrMagnitude < 1f || value.Gravity.sqrMagnitude > 20f * 20f ||
                !Finite(value.AngularVelocity) || value.AngularVelocity.sqrMagnitude > 500f * 500f)
                throw new ArgumentOutOfRangeException(nameof(value), "Coin projectile state is outside supported bounds.");
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

    public static class CoinProjectilePolicy
    {
        public static bool IsAuthorizedLaunch(PlayerSnapshot player, CoinProjectileEvent launch)
        {
            if (player == null || launch == null || (player.MovementFlags & 4) != 0 ||
                player.WeaponSlot != 1 || player.WeaponVariation != launch.WeaponVariation ||
                !WeaponInventoryPolicy.IsUnlocked(player.WeaponInventoryMask, 1, player.WeaponVariation) ||
                !DamageRequestPolicy.IsSelectedWeapon("Revolver", player.WeaponName) ||
                !CoinProjectileCodec.IsLaunchNearPlayer(launch, player.Position) ||
                !WeaponFireCodec.IsPlausibleAimRotation(launch.Rotation, player.ViewRotation)) return false;
            return true;
        }
    }
}
