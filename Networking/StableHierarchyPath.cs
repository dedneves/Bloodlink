using System;
using System.Collections.Generic;
using System.Globalization;

namespace ULTRAKILL.Bloodlink.Networking
{
    internal static class StableHierarchyPath
    {
        public static string EncodeSegment(string objectName, int sameNameOrdinal)
        {
            if (objectName == null) throw new ArgumentNullException(nameof(objectName));
            if (sameNameOrdinal < 0) throw new ArgumentOutOfRangeException(nameof(sameNameOrdinal));
            var escaped = objectName.Replace("%", "%25").Replace("/", "%2F").Replace(":", "%3A");
            return sameNameOrdinal.ToString(CultureInfo.InvariantCulture) + ":" + escaped;
        }

        public static bool TryDecodeSegment(string segment, out string objectName, out int sameNameOrdinal)
        {
            objectName = null;
            sameNameOrdinal = 0;
            if (string.IsNullOrEmpty(segment)) return false;
            var separator = segment.IndexOf(':');
            if (separator <= 0 || !int.TryParse(segment.Substring(0, separator), NumberStyles.None,
                CultureInfo.InvariantCulture, out sameNameOrdinal) || sameNameOrdinal < 0) return false;
            var escaped = segment.Substring(separator + 1);
            objectName = escaped.Replace("%3A", ":").Replace("%2F", "/").Replace("%25", "%");
            return objectName.Length > 0;
        }

        public static int GetSameNameOrdinal(IList<string> siblingNames, int absoluteIndex)
        {
            if (siblingNames == null || absoluteIndex < 0 || absoluteIndex >= siblingNames.Count) return -1;
            var name = siblingNames[absoluteIndex];
            var ordinal = 0;
            for (var i = 0; i < absoluteIndex; i++)
                if (string.Equals(siblingNames[i], name, StringComparison.Ordinal)) ordinal++;
            return ordinal;
        }

        public static int FindAbsoluteIndex(IList<string> siblingNames, string objectName, int sameNameOrdinal)
        {
            if (siblingNames == null || string.IsNullOrEmpty(objectName) || sameNameOrdinal < 0) return -1;
            var ordinal = 0;
            for (var i = 0; i < siblingNames.Count; i++)
            {
                if (!string.Equals(siblingNames[i], objectName, StringComparison.Ordinal)) continue;
                if (ordinal == sameNameOrdinal) return i;
                ordinal++;
            }
            return -1;
        }
    }
}
