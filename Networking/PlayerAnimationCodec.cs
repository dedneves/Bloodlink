using System;
using System.IO;

namespace ULTRAKILL.Bloodlink.Networking
{
    public enum PlayerAnimationAction : byte
    {
        Punch = 1,
        CoinToss = 2,
        Parry = 3,
        Jump = 4,
        Land = 5
    }

    public sealed class PlayerAnimationEvent
    {
        public uint OwnerPlayerId;
        public PlayerAnimationAction Action;
        public float Speed;
    }

    public static class PlayerAnimationCodec
    {
        private const byte MinimumSpeedPercent = 25;
        private const byte MaximumSpeedPercent = 200;

        public static byte[] Encode(PlayerAnimationAction action, float speed = 1f)
        {
            if (!IsSupported(action))
                throw new ArgumentOutOfRangeException(nameof(action));
            if (float.IsNaN(speed) || float.IsInfinity(speed) || speed < MinimumSpeedPercent / 100f || speed > MaximumSpeedPercent / 100f)
                throw new ArgumentOutOfRangeException(nameof(speed));
            return new[] { (byte)action, (byte)Math.Round(speed * 100f) };
        }

        public static PlayerAnimationEvent Decode(byte[] payload)
        {
            if (payload == null || payload.Length != 2) throw new InvalidDataException("Invalid player-animation event length.");
            var action = (PlayerAnimationAction)payload[0];
            var speedPercent = payload[1];
            if (!IsSupported(action) ||
                speedPercent < MinimumSpeedPercent || speedPercent > MaximumSpeedPercent)
                throw new InvalidDataException("Invalid player-animation event values.");
            return new PlayerAnimationEvent { Action = action, Speed = speedPercent / 100f };
        }

        public static byte[] EncodeRelay(uint ownerPlayerId, PlayerAnimationEvent animation)
        {
            if (ownerPlayerId == 0) throw new ArgumentOutOfRangeException(nameof(ownerPlayerId));
            if (animation == null) throw new ArgumentNullException(nameof(animation));
            var body = Encode(animation.Action, animation.Speed);
            using (var stream = new MemoryStream(4 + body.Length))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(ownerPlayerId);
                writer.Write(body);
                return stream.ToArray();
            }
        }

        public static byte[] EncodeRelay(uint ownerPlayerId, PlayerAnimationAction action, float speed = 1f)
        {
            return EncodeRelay(ownerPlayerId, new PlayerAnimationEvent { Action = action, Speed = speed });
        }

        public static PlayerAnimationEvent DecodeRelay(byte[] payload)
        {
            if (payload == null || payload.Length != 6) throw new InvalidDataException("Invalid relayed player-animation event length.");
            using (var stream = new MemoryStream(payload, false))
            using (var reader = new BinaryReader(stream))
            {
                var ownerPlayerId = reader.ReadUInt32();
                if (ownerPlayerId == 0) throw new InvalidDataException("Invalid relayed player-animation owner.");
                var body = reader.ReadBytes(2);
                if (body.Length != 2) throw new InvalidDataException("Truncated relayed player-animation event.");
                var animation = Decode(body);
                animation.OwnerPlayerId = ownerPlayerId;
                return animation;
            }
        }

        public static bool RequiresReliableDelivery(PlayerAnimationAction action)
        {
            return action == PlayerAnimationAction.Parry || action == PlayerAnimationAction.Jump || action == PlayerAnimationAction.Land;
        }

        private static bool IsSupported(PlayerAnimationAction action)
        {
            return action == PlayerAnimationAction.Punch || action == PlayerAnimationAction.CoinToss ||
                action == PlayerAnimationAction.Parry || action == PlayerAnimationAction.Jump || action == PlayerAnimationAction.Land;
        }
    }
}
