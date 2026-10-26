namespace ULTRAKILL.Bloodlink.Networking
{
    public static class PlayerLifecycleStatePolicy
    {
        public static void ApplyToSnapshot(PlayerLifecycleUpdate lifecycle, PlayerSnapshot snapshot)
        {
            if (lifecycle == null || snapshot == null) return;
            // Health and alive/dead state are committed through reliable lifecycle
            // events. A sequenced snapshot can overtake that event on the network
            // while still carrying an older state, so it must never roll back the
            // latest reliable lifecycle state.
            snapshot.Health = lifecycle.Health;
            snapshot.MovementFlags = lifecycle.IsDead
                ? (byte)(snapshot.MovementFlags | 4)
                : (byte)(snapshot.MovementFlags & ~4);
        }
    }
}
