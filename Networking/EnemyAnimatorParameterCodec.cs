using System;
using System.IO;

namespace ULTRAKILL.Bloodlink.Networking
{
    internal struct EnemyAnimatorParameter
    {
        public int Hash;
        // Unity AnimatorControllerParameterType: float=1, int=3, bool=4.
        public byte Type;
        public float Value;
    }

    internal static class EnemyAnimatorParameterCodec
    {
        internal const int MaximumParameters = 32;
        internal static void Write(BinaryWriter writer, EnemyAnimatorParameter[] values)
        {
            var count = values == null ? 0 : values.Length;
            if (count > MaximumParameters) throw new InvalidDataException("Too many enemy animator parameters.");
            writer.Write((byte)count);
            for (var i = 0; i < count; i++)
            {
                Validate(values[i]);
                writer.Write(values[i].Hash);
                writer.Write(values[i].Type);
                writer.Write(values[i].Value);
            }
        }

        internal static EnemyAnimatorParameter[] Read(BinaryReader reader)
        {
            var count = reader.ReadByte();
            if (count > MaximumParameters) throw new InvalidDataException("Too many enemy animator parameters.");
            var result = new EnemyAnimatorParameter[count];
            for (var i = 0; i < count; i++)
            {
                result[i] = new EnemyAnimatorParameter { Hash = reader.ReadInt32(), Type = reader.ReadByte(), Value = reader.ReadSingle() };
                Validate(result[i]);
            }
            return result;
        }

        private static void Validate(EnemyAnimatorParameter value)
        {
            if ((value.Type != 1 && value.Type != 3 && value.Type != 4) || float.IsNaN(value.Value) || float.IsInfinity(value.Value) ||
                (value.Type == 4 && value.Value != 0f && value.Value != 1f) ||
                (value.Type == 3 && (value.Value < int.MinValue || value.Value >= 2147483648f || value.Value != Math.Truncate(value.Value))))
                throw new InvalidDataException("Invalid enemy animator parameter.");
        }
    }
}
