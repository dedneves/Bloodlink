using System;
using System.IO;
using ULTRAKILL.Bloodlink.Networking;
using UnityEngine;

internal static partial class Program
{
    private static void CheckpointAndCombatInteractionChecks()
    {
        var path = "0:World/0:Room%2FCheckpoint/0:" + new string('x', 240);
        var payload = CheckpointEventCodec.Encode(CheckpointEventKind.Request, path, new Vector3(12, -3, 81));
        var checkpoint = CheckpointEventCodec.Decode(payload);
        Assert(checkpoint.Path == path && checkpoint.HasPosition && checkpoint.Position.x == 12 && checkpoint.Position.y == -3,
            "checkpoint accepts stable named paths over 128 bytes with a spatial fallback");
        var commit = CheckpointEventCodec.Decode(CheckpointEventCodec.Encode(CheckpointEventKind.Commit, path, checkpoint.Position));
        Assert(commit.Kind == CheckpointEventKind.Commit && commit.HasPosition && commit.Position.z == 81,
            "checkpoint confirmation preserves the target independently of sibling ordering");
        var invalid = false;
        try { CheckpointEventCodec.Encode(CheckpointEventKind.Request, path, new Vector3(float.NaN, 0, 0)); }
        catch (ArgumentException) { invalid = true; }
        Assert(invalid, "checkpoint rejects invalid recovery coordinates");
        invalid = false;
        try { CheckpointEventCodec.Decode(new byte[] { 1, 255, 255, 0, 0 }); }
        catch (InvalidDataException) { invalid = true; }
        Assert(invalid, "checkpoint rejects oversized path declarations before reading");

        var tracker = new WindowedEventSequenceTracker();
        Assert(tracker.TryAccept(2, 10) && tracker.TryAccept(2, 12) && tracker.TryAccept(2, 11),
            "combat retains earlier hits when the independent reliable stream reorders packets");
        Assert(!tracker.TryAccept(2, 11) && !tracker.TryAccept(2, 12), "duplicate combat requests never apply damage twice");
        Assert(tracker.TryAccept(3, 11), "combat replay windows are independent per player");
        Assert(!tracker.TryAccept(2, 100000), "combat rejects unreasonable forward sequence jumps");
        Assert(tracker.TryAccept(2, 1036) && !tracker.TryAccept(2, 10), "combat rejects hits outside the bounded replay window");
        tracker.Remove(2);
        Assert(tracker.TryAccept(2, uint.MaxValue) && tracker.TryAccept(2, 1) && tracker.TryAccept(2, uint.MaxValue - 1),
            "combat replay window supports sequence wrap and reordered older packets");

        var roster = EnemyPresenceCodec.Decode(EnemyPresenceCodec.Encode("Level 0-1", new uint[] { 1, 3, 5 }, 27));
        Assert(roster.Scene == "Level 0-1" && roster.Frame == 27 && roster.Ids.Length == 3 && roster.Ids[1] == 3,
            "enemy existence roster carries scene, ordering and exact entity ids");
        Assert(EnemyPresenceCodec.Decode(EnemyPresenceCodec.Encode("Level 0-1", Array.Empty<uint>())).Ids.Length == 0,
            "empty enemy roster correctly represents a cleared room");
        invalid = false;
        try { EnemyPresenceCodec.Decode(EnemyPresenceCodec.Encode("Level 0-1", new uint[] { 1, 1 })); }
        catch (InvalidDataException) { invalid = true; }
        Assert(invalid, "enemy roster rejects duplicated ids");
    }
}
