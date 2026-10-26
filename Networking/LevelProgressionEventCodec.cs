using System;
using System.IO;
using System.Text;

namespace ULTRAKILL.Bloodlink.Networking
{
    public enum LevelProgressionEventKind : byte
    {
        ArenaActivationRequest = 1,
        ArenaActivated = 2,
        WaveAdvanced = 3
    }

    public sealed class LevelProgressionEventData
    {
        public LevelProgressionEventKind Kind { get; private set; }
        public string SceneName { get; private set; }
        public string ObjectPath { get; private set; }
        public byte ComponentIndex { get; private set; }

        internal LevelProgressionEventData(LevelProgressionEventKind kind, string sceneName, string objectPath, byte componentIndex)
        {
            Kind = kind;
            SceneName = sceneName;
            ObjectPath = objectPath;
            ComponentIndex = componentIndex;
        }
    }

    public static class LevelProgressionEventCodec
    {
        private const int MaxSceneBytes = 64;
        private const int MaxPathBytes = 3072;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static byte[] Encode(LevelProgressionEventKind kind, string sceneName, string objectPath, byte componentIndex)
        {
            ValidateKind(kind);
            ValidateScene(sceneName);
            ValidatePath(objectPath);
            var scene = StrictUtf8.GetBytes(sceneName);
            var path = StrictUtf8.GetBytes(objectPath);
            if (scene.Length > MaxSceneBytes || path.Length > MaxPathBytes)
                throw new ArgumentOutOfRangeException(scene.Length > MaxSceneBytes ? nameof(sceneName) : nameof(objectPath));
            using (var stream = new MemoryStream(4 + scene.Length + path.Length))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)kind);
                writer.Write(componentIndex);
                writer.Write((byte)scene.Length);
                writer.Write((ushort)path.Length);
                writer.Write(scene);
                writer.Write(path);
                return stream.ToArray();
            }
        }

        public static LevelProgressionEventData Decode(byte[] payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            using (var stream = new MemoryStream(payload, false))
            using (var reader = new BinaryReader(stream))
            {
                if (stream.Length < 5) throw new InvalidDataException("Progression event is too short.");
                var kind = (LevelProgressionEventKind)reader.ReadByte();
                var componentIndex = reader.ReadByte();
                var sceneLength = reader.ReadByte();
                var pathLength = reader.ReadUInt16();
                ValidateKind(kind);
                if (sceneLength == 0 || sceneLength > MaxSceneBytes || pathLength == 0 || pathLength > MaxPathBytes ||
                    stream.Length - stream.Position != sceneLength + pathLength)
                    throw new InvalidDataException("Invalid progression event lengths.");
                string scene;
                string path;
                try
                {
                    scene = StrictUtf8.GetString(reader.ReadBytes(sceneLength));
                    path = StrictUtf8.GetString(reader.ReadBytes(pathLength));
                }
                catch (DecoderFallbackException ex) { throw new InvalidDataException("Invalid progression event encoding.", ex); }
                try
                {
                    ValidateScene(scene);
                    ValidatePath(path);
                }
                catch (ArgumentException ex) { throw new InvalidDataException("Invalid progression event identity.", ex); }
                return new LevelProgressionEventData(kind, scene, path, componentIndex);
            }
        }

        private static void ValidateKind(LevelProgressionEventKind kind)
        {
            if (kind != LevelProgressionEventKind.ArenaActivationRequest &&
                kind != LevelProgressionEventKind.ArenaActivated && kind != LevelProgressionEventKind.WaveAdvanced)
                throw new ArgumentOutOfRangeException(nameof(kind));
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
