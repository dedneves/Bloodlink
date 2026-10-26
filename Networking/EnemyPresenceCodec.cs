using System;
using System.Collections.Generic;
using System.IO;

namespace ULTRAKILL.Bloodlink.Networking
{
    internal sealed class EnemyPresenceState
    {
        public string Scene;
        public uint Frame;
        public uint[] Ids;
    }

    internal static class EnemyPresenceCodec
    {
        internal static byte[] Encode(string scene, uint[] ids, uint frame = 1)
        {
            if (string.IsNullOrEmpty(scene) || scene.Length > 128 || ids == null || ids.Length > 4096)
                throw new InvalidDataException("Invalid enemy roster.");
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(scene); writer.Write(frame); writer.Write((ushort)ids.Length);
                foreach (var id in ids) { if (id == 0) throw new InvalidDataException("Invalid enemy id."); writer.Write(id); }
                return stream.ToArray();
            }
        }

        internal static EnemyPresenceState Decode(byte[] payload)
        {
            if (payload == null || payload.Length > 17000) throw new InvalidDataException("Invalid enemy roster size.");
            using (var stream = new MemoryStream(payload, false))
            using (var reader = new BinaryReader(stream))
            {
                var scene = reader.ReadString();
                var frame = reader.ReadUInt32();
                var count = reader.ReadUInt16();
                if (string.IsNullOrEmpty(scene) || scene.Length > 128 || count > 4096 || stream.Length - stream.Position != count * 4)
                    throw new InvalidDataException("Invalid enemy roster.");
                var ids = new uint[count]; var unique = new HashSet<uint>();
                for (var i = 0; i < count; i++)
                { ids[i] = reader.ReadUInt32(); if (ids[i] == 0 || !unique.Add(ids[i])) throw new InvalidDataException("Invalid enemy roster id."); }
                return new EnemyPresenceState { Scene = scene, Frame = frame, Ids = ids };
            }
        }
    }
}
