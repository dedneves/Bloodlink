using ULTRAKILL.Bloodlink.Combat;
using ULTRAKILL.Bloodlink.Networking;
using UnityEngine;

internal static partial class Program
{
    private static void FriendlyFireGeometryChecks()
    {
        float distance;
        Assert(FriendlyFirePolicy.Intersects(Vector3.zero, Vector3.forward, new Vector3(0, 0, 12), 30, 0.55f, out distance) && distance == 12,
            "aimed shot intersects a teammate's body");
        Assert(FriendlyFirePolicy.Intersects(Vector3.zero, Vector3.forward, new Vector3(0, -0.65f, 12), 30, 0.55f, out distance),
            "friendly-fire geometry includes the upper part of the player capsule");
        Assert(!FriendlyFirePolicy.Intersects(Vector3.zero, Vector3.forward, new Vector3(0, 0, -12), 30, 0.55f, out distance),
            "friendly fire cannot hit a teammate behind the shooter");
        Assert(!FriendlyFirePolicy.Intersects(Vector3.zero, Vector3.forward, new Vector3(3, 0, 12), 30, 0.55f, out distance),
            "a clear miss does not damage a teammate");
        Assert(!FriendlyFirePolicy.Intersects(Vector3.zero, Vector3.forward, new Vector3(0, 0, 50), 30, 0.55f, out distance),
            "friendly fire respects weapon range");
        Assert(FriendlyFirePolicy.Damage(1, 0) == 15 && FriendlyFirePolicy.Damage(3, 6) == 3 && FriendlyFirePolicy.Damage(4, 5) == 50,
            "friendly-fire damage remains bounded by weapon class");
        Assert(FriendlyFirePolicy.Range(0) == 3f && FriendlyFirePolicy.Damage(0, 0) == 15,
            "melee friendly fire uses close-range geometry rather than gun range");
        var rules = NetworkMessage.Decode(new NetworkMessage(MessageType.SessionRules, 1, new byte[] { 1 }).Encode());
        Assert(rules.Type == MessageType.SessionRules && rules.Payload[0] == 1,
            "host-friendly-fire setting survives protocol framing");
    }
}
