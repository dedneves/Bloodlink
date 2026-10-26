using System;
using System.IO;
using System.Text;

namespace ULTRAKILL.Bloodlink.Networking
{
    public sealed class JoinSessionState
    {
        public string SceneName { get; private set; }
        public byte Difficulty { get; private set; }

        internal JoinSessionState(string sceneName, byte difficulty)
        {
            SceneName = sceneName;
            Difficulty = difficulty;
        }
    }

    public static class JoinSessionStateCodec
    {
        private const int MaximumSceneBytes = 128;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static byte[] Encode(string sceneName, byte difficulty = 2)
        {
            if (string.IsNullOrWhiteSpace(sceneName) || sceneName.Length > MaximumSceneBytes)
                throw new ArgumentException("A valid active scene is required.", nameof(sceneName));
            if (difficulty > 4) throw new ArgumentOutOfRangeException(nameof(difficulty));
            for (var i = 0; i < sceneName.Length; i++)
                if (char.IsControl(sceneName[i])) throw new ArgumentException("Scene name contains control characters.", nameof(sceneName));
            var name = StrictUtf8.GetBytes(sceneName);
            if (name.Length > MaximumSceneBytes) throw new ArgumentOutOfRangeException(nameof(sceneName));
            using (var stream = new MemoryStream(name.Length + 2))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)name.Length);
                writer.Write(name);
                writer.Write(difficulty);
                return stream.ToArray();
            }
        }

        public static string Decode(byte[] payload)
        {
            return DecodeState(payload).SceneName;
        }

        public static JoinSessionState DecodeState(byte[] payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            using (var stream = new MemoryStream(payload, false))
            using (var reader = new BinaryReader(stream))
            {
                if (stream.Length < 3) throw new InvalidDataException("Join-state payload is too short.");
                var length = reader.ReadByte();
                if (length == 0 || length > MaximumSceneBytes || stream.Length - stream.Position != length + 1L)
                    throw new InvalidDataException("Invalid join-state scene length.");
                string scene;
                try { scene = StrictUtf8.GetString(reader.ReadBytes(length)); }
                catch (DecoderFallbackException ex) { throw new InvalidDataException("Invalid UTF-8 join-state scene.", ex); }
                try { Encode(scene); }
                catch (ArgumentException ex) { throw new InvalidDataException("Invalid join-state scene.", ex); }
                var difficulty = reader.ReadByte();
                if (difficulty > 4) throw new InvalidDataException("Invalid join-state difficulty.");
                return new JoinSessionState(scene, difficulty);
            }
        }
    }
}
