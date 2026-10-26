using System;
using System.IO;
using System.Text;

namespace ULTRAKILL.Bloodlink.Networking
{
    public sealed class SecretBonusLocation
    {
        public string SceneName { get; }
        public string ObjectPath { get; }
        public byte ComponentIndex { get; }
        public int SecretNumber { get; }

        public SecretBonusLocation(string sceneName, string objectPath, byte componentIndex, int secretNumber)
        {
            SceneName = sceneName;
            ObjectPath = objectPath;
            ComponentIndex = componentIndex;
            SecretNumber = secretNumber;
        }
    }

    public sealed class SecretBonusPickupRequest
    {
        public uint Sequence { get; }
        public SecretBonusLocation Location { get; }

        internal SecretBonusPickupRequest(uint sequence, SecretBonusLocation location)
        {
            Sequence = sequence;
            Location = location;
        }
    }

    public static class BonusPickupCodec
    {
        private const int MaxSceneBytes = 128;
        private const int MaxPathBytes = 3072;
        private const int MaximumSecretNumber = 4095;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static byte[] EncodeRequest(uint sequence, SecretBonusLocation location)
        {
            if (sequence == 0) throw new ArgumentOutOfRangeException(nameof(sequence));
            ValidateLocation(location);
            using (var stream = new MemoryStream(1 + 4 + 1 + 1 + MaxSceneBytes + MaxPathBytes))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(sequence);
                WriteLocation(writer, location);
                return stream.ToArray();
            }
        }

        public static byte[] EncodeRelay(SecretBonusLocation location)
        {
            ValidateLocation(location);
            using (var stream = new MemoryStream(1 + 1 + MaxSceneBytes + MaxPathBytes))
            using (var writer = new BinaryWriter(stream))
            {
                WriteLocation(writer, location);
                return stream.ToArray();
            }
        }

        public static SecretBonusPickupRequest DecodeRequest(byte[] payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            using (var stream = new MemoryStream(payload, false))
            using (var reader = new BinaryReader(stream))
            {
                if (stream.Length < 5) throw new InvalidDataException("Secret bonus request is too short.");
                var sequence = reader.ReadUInt32();
                if (sequence == 0) throw new InvalidDataException("Secret bonus request sequence is invalid.");
                return new SecretBonusPickupRequest(sequence, ReadLocation(reader, stream));
            }
        }

        public static SecretBonusLocation DecodeRelay(byte[] payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            using (var stream = new MemoryStream(payload, false))
            using (var reader = new BinaryReader(stream))
            {
                return ReadLocation(reader, stream);
            }
        }

        private static void WriteLocation(BinaryWriter writer, SecretBonusLocation location)
        {
            var scene = StrictUtf8.GetBytes(location.SceneName);
            var path = StrictUtf8.GetBytes(location.ObjectPath);
            if (scene.Length > MaxSceneBytes || path.Length > MaxPathBytes)
                throw new ArgumentOutOfRangeException(scene.Length > MaxSceneBytes ? nameof(location.SceneName) : nameof(location.ObjectPath));
            writer.Write(location.ComponentIndex);
            writer.Write(location.SecretNumber);
            writer.Write((byte)scene.Length);
            writer.Write((ushort)path.Length);
            writer.Write(scene);
            writer.Write(path);
        }

        private static SecretBonusLocation ReadLocation(BinaryReader reader, MemoryStream stream)
        {
            if (stream.Length - stream.Position < 8) throw new InvalidDataException("Secret bonus payload is too short.");
            var componentIndex = reader.ReadByte();
            var secretNumber = reader.ReadInt32();
            var sceneLength = reader.ReadByte();
            var pathLength = reader.ReadUInt16();
            if (sceneLength == 0 || sceneLength > MaxSceneBytes || pathLength == 0 || pathLength > MaxPathBytes ||
                stream.Length - stream.Position != sceneLength + pathLength)
                throw new InvalidDataException("Invalid secret bonus lengths.");
            string scene;
            string path;
            try
            {
                scene = StrictUtf8.GetString(reader.ReadBytes(sceneLength));
                path = StrictUtf8.GetString(reader.ReadBytes(pathLength));
            }
            catch (DecoderFallbackException ex) { throw new InvalidDataException("Invalid secret bonus encoding.", ex); }
            var location = new SecretBonusLocation(scene, path, componentIndex, secretNumber);
            try { ValidateLocation(location); }
            catch (ArgumentException ex) { throw new InvalidDataException("Invalid secret bonus identity.", ex); }
            return location;
        }

        private static void ValidateLocation(SecretBonusLocation location)
        {
            if (location == null) throw new ArgumentNullException(nameof(location));
            ValidateScene(location.SceneName);
            ValidatePath(location.ObjectPath);
            if (location.SecretNumber < 0 || location.SecretNumber > MaximumSecretNumber)
                throw new ArgumentOutOfRangeException(nameof(location));
        }

        private static void ValidateScene(string sceneName)
        {
            if (string.IsNullOrWhiteSpace(sceneName) || sceneName.Length > MaxSceneBytes)
                throw new ArgumentException("Scene name is empty or too long.", nameof(sceneName));
            foreach (var character in sceneName)
                if (char.IsControl(character)) throw new ArgumentException("Scene name contains control characters.", nameof(sceneName));
        }

        private static void ValidatePath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Length > MaxPathBytes)
                throw new ArgumentException("Object path is empty or too long.", nameof(path));
            foreach (var segment in path.Split('/'))
            {
                string objectName; int ordinal;
                if (!StableHierarchyPath.TryDecodeSegment(segment, out objectName, out ordinal))
                    throw new ArgumentException("Object path must contain stable named segments.", nameof(path));
            }
        }
    }
}
