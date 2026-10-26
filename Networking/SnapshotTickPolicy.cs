namespace ULTRAKILL.Bloodlink.Networking
{
    public static class SnapshotTickPolicy
    {
        /// <summary>Returns true when candidate is newer in a wrapping uint sequence.</summary>
        public static bool IsNewer(uint candidate, uint previous)
        {
            return unchecked((int)(candidate - previous)) > 0;
        }
    }
}
