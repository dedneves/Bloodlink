using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ULTRAKILL.Bloodlink.Networking
{
    public enum LevelTransitionStage : byte
    {
        Prepare = 1,
        Ready = 2,
        Start = 3,
        Progress = 4,
        Abort = 5
    }

    public sealed class LevelTransition
    {
        public uint TransitionId { get; private set; }
        public LevelTransitionStage Stage { get; private set; }
        public string SceneName { get; private set; }
        public bool ForceReload { get; private set; }
        public byte ReadyPlayerCount { get; private set; }
        public byte TotalPlayerCount { get; private set; }
        public byte Difficulty { get; private set; }
        public uint[] ReadyPlayerIds { get; private set; }

        internal LevelTransition(uint transitionId, LevelTransitionStage stage, string sceneName, bool forceReload,
            byte readyPlayerCount, byte totalPlayerCount, uint[] readyPlayerIds, byte difficulty)
        {
            TransitionId = transitionId;
            Stage = stage;
            SceneName = sceneName;
            ForceReload = forceReload;
            ReadyPlayerCount = readyPlayerCount;
            TotalPlayerCount = totalPlayerCount;
            Difficulty = difficulty;
            ReadyPlayerIds = readyPlayerIds;
        }
    }

    public static class LevelTransitionCodec
    {
        private const int MaximumSceneNameBytes = 128;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static byte[] Encode(uint transitionId, LevelTransitionStage stage, string sceneName, bool forceReload = false,
            byte readyPlayerCount = 0, byte totalPlayerCount = 0, uint[] readyPlayerIds = null, byte difficulty = 2)
        {
            if (transitionId == 0) throw new ArgumentOutOfRangeException(nameof(transitionId));
            if (!Enum.IsDefined(typeof(LevelTransitionStage), stage)) throw new ArgumentOutOfRangeException(nameof(stage));
            ValidateSceneName(sceneName);
            ValidatePlayerCounts(readyPlayerCount, totalPlayerCount);
            ValidateDifficulty(difficulty);
            if (stage != LevelTransitionStage.Ready && totalPlayerCount == 0)
                throw new ArgumentOutOfRangeException(nameof(totalPlayerCount), "A host transition must include at least the host participant.");
            ValidateReadyPlayerIds(readyPlayerCount, readyPlayerIds);
            var nameBytes = StrictUtf8.GetBytes(sceneName);
            if (nameBytes.Length > MaximumSceneNameBytes) throw new ArgumentOutOfRangeException(nameof(sceneName));
            using (var stream = new MemoryStream(11 + nameBytes.Length + readyPlayerCount * 4))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(transitionId);
                writer.Write((byte)stage);
                writer.Write(forceReload);
                writer.Write((byte)nameBytes.Length);
                writer.Write(nameBytes);
                writer.Write(difficulty);
                writer.Write(readyPlayerCount);
                writer.Write(totalPlayerCount);
                writer.Write(readyPlayerCount);
                if (readyPlayerIds != null)
                    for (var i = 0; i < readyPlayerIds.Length; i++) writer.Write(readyPlayerIds[i]);
                return stream.ToArray();
            }
        }

        public static LevelTransition Decode(byte[] payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            using (var stream = new MemoryStream(payload, false))
            using (var reader = new BinaryReader(stream))
            {
                if (stream.Length < 11) throw new InvalidDataException("Level transition payload is too short.");
                var id = reader.ReadUInt32();
                var stage = (LevelTransitionStage)reader.ReadByte();
                var forceReload = reader.ReadBoolean();
                var length = reader.ReadByte();
                if (id == 0 || !Enum.IsDefined(typeof(LevelTransitionStage), stage) || length == 0 || length > MaximumSceneNameBytes || stream.Length - stream.Position < length + 4)
                    throw new InvalidDataException("Invalid level transition payload length or ID.");
                string sceneName;
                try { sceneName = StrictUtf8.GetString(reader.ReadBytes(length)); }
                catch (DecoderFallbackException ex) { throw new InvalidDataException("Invalid UTF-8 scene name.", ex); }
                try { ValidateSceneName(sceneName); }
                catch (ArgumentException ex) { throw new InvalidDataException("Invalid scene name.", ex); }
                var difficulty = reader.ReadByte();
                try { ValidateDifficulty(difficulty); }
                catch (ArgumentOutOfRangeException ex) { throw new InvalidDataException("Invalid session difficulty.", ex); }
                var readyPlayerCount = reader.ReadByte();
                var totalPlayerCount = reader.ReadByte();
                var readyIdCount = reader.ReadByte();
                try { ValidatePlayerCounts(readyPlayerCount, totalPlayerCount); }
                catch (ArgumentOutOfRangeException ex) { throw new InvalidDataException("Invalid transition player counts.", ex); }
                if (stage != LevelTransitionStage.Ready && totalPlayerCount == 0)
                    throw new InvalidDataException("Host transitions must include at least the host participant.");
                if (readyIdCount != readyPlayerCount || stream.Length - stream.Position != readyIdCount * 4L)
                    throw new InvalidDataException("Transition ready-player list does not match its declared count.");
                var readyPlayerIds = new uint[readyIdCount];
                for (var i = 0; i < readyPlayerIds.Length; i++) readyPlayerIds[i] = reader.ReadUInt32();
                try { ValidateReadyPlayerIds(readyPlayerCount, readyPlayerIds); }
                catch (ArgumentException ex) { throw new InvalidDataException("Invalid transition ready-player IDs.", ex); }
                return new LevelTransition(id, stage, sceneName, forceReload, readyPlayerCount, totalPlayerCount, readyPlayerIds, difficulty);
            }
        }

        private static void ValidatePlayerCounts(byte readyPlayerCount, byte totalPlayerCount)
        {
            if (readyPlayerCount > totalPlayerCount || totalPlayerCount > 4)
                throw new ArgumentOutOfRangeException(nameof(totalPlayerCount), "Transition player counts must be between zero and four, with ready not exceeding total.");
        }

        private static void ValidateReadyPlayerIds(byte readyPlayerCount, uint[] readyPlayerIds)
        {
            var count = readyPlayerIds == null ? 0 : readyPlayerIds.Length;
            if (count != readyPlayerCount || count > 4)
                throw new ArgumentException("The ready-player ID list must match the declared ready count.", nameof(readyPlayerIds));
            var seen = new HashSet<uint>();
            for (var i = 0; i < count; i++)
                if (readyPlayerIds[i] == 0 || !seen.Add(readyPlayerIds[i]))
                    throw new ArgumentException("Ready-player IDs must be nonzero and unique.", nameof(readyPlayerIds));
        }

        private static void ValidateDifficulty(byte difficulty)
        {
            if (difficulty > 4) throw new ArgumentOutOfRangeException(nameof(difficulty), "Session difficulty must be between 0 and 4.");
        }

        private static void ValidateSceneName(string sceneName)
        {
            if (string.IsNullOrWhiteSpace(sceneName)) throw new ArgumentException("Scene name is required.", nameof(sceneName));
            if (sceneName.Length > MaximumSceneNameBytes) throw new ArgumentException("Scene name is too long.", nameof(sceneName));
            for (var i = 0; i < sceneName.Length; i++)
                if (char.IsControl(sceneName[i])) throw new ArgumentException("Scene name contains control characters.", nameof(sceneName));
        }
    }
}
