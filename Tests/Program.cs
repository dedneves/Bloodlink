using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ULTRAKILL.Bloodlink.Networking;
using ULTRAKILL.Bloodlink.Enemies;
using ULTRAKILL.Bloodlink.Levels;
using Unity.Services.Relay;
using UnityEngine;

internal static partial class Program
{
    private static int checks;

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "--respawn-enemy-only")
            {
                EnemyAnimatorParameterChecks();
                EnemySnapshotWireRegressionChecks();
                RespawnMovementNetworkChecks();
                Console.WriteLine("PASS: " + checks + " respawn/animation checks (local UDP host/client; game not launched)");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--ready-only")
            {
                LevelTransitionRoundTrip();
                LevelReadinessBarrierChecks();
                ReadinessSceneIdentityChecks();
                ReadinessTransportChecks();
                Console.WriteLine("PASS: " + checks + " readiness checks (local UDP host/client; game not launched)");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--sync-only")
            {
                CheckpointAndCombatInteractionChecks();
                FriendlyFireGeometryChecks();
                StableHierarchyPathChecks(); WorldMutationCodecChecks(); WeaponFireRoundTrip();
                SynchronizationRewriteChecks(); NativeReplicationNetworkSession();
                Console.WriteLine("PASS: " + checks + " synchronization checks (host + two clients; game not launched)");
                return 0;
            }
            ProtocolRoundTrip();
            StableHierarchyPathChecks();
            WorldMutationCodecChecks();
            SynchronizationRewriteChecks();
            WorldAudioCodecChecks();
            RelayThreadHelperBootstrapChecks();
            LevelTransitionRoundTrip();
            LevelProgressionEventChecks();
            LevelReadinessBarrierChecks();
            ReadinessSceneIdentityChecks();
            ReadinessTransportChecks();
            PlayerLifecycleRoundTrip();
            PlayerLifecycleSnapshotOrderingChecks();
            SessionHandshakeCodecChecks();
            ReconnectReservationStoreChecks();
            CheckpointEventRoundTrip();
            CheckpointSessionStateChecks();
            DamageRequestPolicyChecks();
            EnemyDamageStateCodecChecks();
            WeaponFireRoundTrip();
            CoinProjectileChecks();
            PlayerAnimationCodecChecks();
            WeaponFireRateLimitChecks();
            WeaponInventoryChecks();
            EnemyIdentityKeyChecks();
            JoinSessionStateChecks();
            ReconnectBackoffChecks();
            ReliableEventSequenceChecks();
            DamageRequestRateLimitChecks();
            AppearanceColorChecks();
            LatencyWindowChecks();
            SnapshotFreshnessChecks();
            SnapshotTickChecks();
            EnemyHealthPolicyChecks();
            EnemyMatchPolicyChecks();
            PlayerSnapshotRoundTrip();
            ProtocolRejectsMalformedFrames();
            TwoPlayerHostClientSession();
            NativeReplicationNetworkSession();
            ClientReconnectsWithSameSessionIdentity();
            ClientReconnectsAfterHostRestart();
            LossAndLatencyResilience();
            FourPlayerCapacityAndSnapshots();
            IndependentInventoryNetworkSession();
            LanDiscoveryAndJoin();
            PasswordRejectsWrongClient();
            Console.WriteLine("PASS: " + checks + " Bloodlink checks");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex);
            return 1;
        }
    }

    private static void LatencyWindowChecks()
    {
        var window = new LatencyWindow(5);
        Assert(window.Percentile95 == 0, "empty latency window reports zero percentile");
        foreach (var sample in new[] { 10, 20, 30, 40, 50 }) window.Add(sample);
        Assert(window.Percentile95 == 50, "RTT p95 uses nearest-rank percentile");
        window.Add(-1);
        Assert(window.Count == 5 && window.Percentile95 == 50, "negative RTT samples are ignored");
        window.Add(5);
        Assert(window.Count == 5 && window.Percentile95 == 50, "latency window evicts its oldest sample at capacity");
        window.Clear();
        Assert(window.Count == 0 && window.Percentile95 == 0, "latency window clears between sessions");
    }

    private static void StableHierarchyPathChecks()
    {
        var server = new[] { "wood", "glass", "pickup", "glass" };
        var client = new[] { "pickup", "glass", "wood", "glass", "new-runtime-object" };
        var glassOrdinal = StableHierarchyPath.GetSameNameOrdinal(server, 1);
        Assert(glassOrdinal == 0, "hierarchy identity stores same-name ordinal instead of absolute sibling index");
        Assert(StableHierarchyPath.FindAbsoluteIndex(client, "glass", glassOrdinal) == 1,
            "hierarchy identity resolves correctly when unrelated siblings have different positions");
        var secondGlass = StableHierarchyPath.GetSameNameOrdinal(server, 3);
        Assert(secondGlass == 1 && StableHierarchyPath.FindAbsoluteIndex(client, "glass", secondGlass) == 3,
            "hierarchy identity distinguishes duplicate names by occurrence");
        var encoded = StableHierarchyPath.EncodeSegment("door/secret:50%", 2);
        string decoded;
        int ordinal;
        Assert(StableHierarchyPath.TryDecodeSegment(encoded, out decoded, out ordinal) && decoded == "door/secret:50%" && ordinal == 2,
            "hierarchy path segments safely encode delimiters in object names");
        Assert(!StableHierarchyPath.TryDecodeSegment("x:not-an-index", out decoded, out ordinal),
            "hierarchy path decoder rejects malformed occurrence indexes");
    }

    private static void SnapshotFreshnessChecks()
    {
        var tracker = new SnapshotFreshnessTracker();
        Assert(!tracker.IsFresh(2, 1f, 0.5f), "unseen player snapshot is not fresh");
        tracker.Record(2, 1f);
        Assert(tracker.IsFresh(2, 1.5f, 0.5f), "snapshot remains fresh at the configured age boundary");
        Assert(!tracker.IsFresh(2, 1.5001f, 0.5f), "stale player snapshot expires");
        Assert(!tracker.IsFresh(2, 0.9f, 0.5f), "snapshot freshness rejects time moving backwards");
        tracker.Record(0, 2f);
        Assert(!tracker.IsFresh(0, 2f, 1f), "invalid player identity cannot become fresh");
        tracker.Remove(2);
        Assert(!tracker.IsFresh(2, 1.1f, 1f), "removed player snapshot is no longer fresh");
        tracker.Record(2, float.NaN);
        Assert(!tracker.IsFresh(2, 1f, 1f), "invalid snapshot receive time is ignored");
    }

    private static void SnapshotTickChecks()
    {
        Assert(SnapshotTickPolicy.IsNewer(11, 10), "snapshot accepts the next tick");
        Assert(!SnapshotTickPolicy.IsNewer(10, 10), "snapshot rejects a duplicate tick");
        Assert(!SnapshotTickPolicy.IsNewer(9, 10), "snapshot rejects an older tick");
        Assert(SnapshotTickPolicy.IsNewer(0, uint.MaxValue), "snapshot tick ordering supports uint wraparound");
        Assert(!SnapshotTickPolicy.IsNewer(uint.MaxValue, 0), "snapshot tick ordering rejects pre-wrap replay");
    }

    private static void EnemyHealthPolicyChecks()
    {
        Assert(EnemyHealthSyncPolicy.IsValid(0f), "enemy health synchronization accepts zero health");
        Assert(EnemyHealthSyncPolicy.IsValid(100000f), "enemy health synchronization accepts its maximum bound");
        Assert(!EnemyHealthSyncPolicy.IsValid(-0.01f), "enemy health synchronization rejects negative health");
        Assert(!EnemyHealthSyncPolicy.IsValid(100000.1f), "enemy health synchronization rejects out-of-range health");
        Assert(!EnemyHealthSyncPolicy.IsValid(float.NaN), "enemy health synchronization rejects non-finite health");
    }

    private static void EnemyMatchPolicyChecks()
    {
        Assert(!EnemyMatchPolicy.ShouldAllowNearestFallback(1, 0.49f), "enemy spatial fallback waits for stable hierarchy matching");
        Assert(EnemyMatchPolicy.ShouldAllowNearestFallback(1, 0.5f), "enemy spatial fallback becomes available after the grace period");
        Assert(EnemyMatchPolicy.ShouldAllowNearestFallback(0, 0f), "missing stable key permits immediate spatial fallback");
        Assert(EnemyMatchPolicy.IsUnambiguousNearest(9f, float.PositiveInfinity), "single nearby enemy is a valid fallback match");
        Assert(EnemyMatchPolicy.IsUnambiguousNearest(4f, 12f), "clearly nearer enemy is selected");
        Assert(!EnemyMatchPolicy.IsUnambiguousNearest(4f, 7f), "near-tied enemy candidates are not guessed");
        Assert(!EnemyMatchPolicy.IsUnambiguousNearest(626f, float.PositiveInfinity), "distant enemy candidate is not claimed");
        Assert(!EnemyMatchPolicy.IsUnambiguousNearest(float.NaN, 10f), "invalid candidate distance is rejected");
    }

    private static void AppearanceColorChecks()
    {
        var color = new Color(0.8f, 0.2f, 1f, 1f);
        var packed = PlayerAppearanceColor.Pack(color);
        var unpacked = PlayerAppearanceColor.Unpack(packed);
        Assert(packed == 0xCC33FF, "appearance color packs to compact RGB");
        Assert(Math.Abs(unpacked.r - 0.8f) < 0.01f && Math.Abs(unpacked.g - 0.2f) < 0.01f && unpacked.b == 1f, "appearance color unpacks within 8-bit precision");
        var rejected = false;
        try { PlayerAppearanceColor.Unpack(0x1000000); }
        catch (ArgumentOutOfRangeException) { checks++; rejected = true; }
        if (!rejected) throw new Exception("Expected invalid RGB value rejection.");
    }

    private static void DamageRequestRateLimitChecks()
    {
        var limiter = new DamageRequestRateLimiter(4f, 2f);
        for (var i = 0; i < 4; i++) Assert(limiter.TryAccept(2, 3f), "damage limiter permits its configured burst");
        Assert(!limiter.TryAccept(2, 3f), "damage limiter rejects traffic above burst capacity");
        Assert(limiter.TryAccept(2, 3.5f), "damage limiter refills at its configured rate");
        Assert(limiter.TryAccept(3, 3f), "damage limiter tracks players independently");
        Assert(!limiter.TryAccept(0, 4f), "damage limiter rejects an invalid player identity");
        Assert(!limiter.TryAccept(2, float.PositiveInfinity), "damage limiter rejects invalid time");
    }

    private static void ReliableEventSequenceChecks()
    {
        var tracker = new ReliableEventSequenceTracker();
        Assert(tracker.TryAccept(2, 1), "first reliable event sequence is accepted");
        Assert(!tracker.TryAccept(2, 1), "duplicate reliable event sequence is rejected");
        Assert(!tracker.TryAccept(2, 0), "zero reliable event sequence is rejected");
        Assert(!tracker.TryAccept(2, uint.MaxValue), "out-of-order reliable event sequence is rejected");
        Assert(tracker.TryAccept(2, 2), "next reliable event sequence is accepted");
        Assert(tracker.TryAccept(3, 1), "reliable event sequences are independent per player");
        Assert(!tracker.TryAccept(0, 1), "reliable event without a player identity is rejected");
        tracker.Remove(2);
        Assert(tracker.TryAccept(2, 1), "new player session can restart its event sequence");
        var wrapTracker = new ReliableEventSequenceTracker();
        Assert(wrapTracker.TryAccept(4, uint.MaxValue - 1), "event sequence near wrap is accepted");
        Assert(wrapTracker.TryAccept(4, uint.MaxValue), "event sequence maximum is accepted");
        Assert(wrapTracker.TryAccept(4, 1), "event sequence wraps without rejecting the next event");
    }

    private static void ClientReconnectsAfterHostRestart()
    {
        int port;
        using (var probe = new UdpClient(0)) port = ((IPEndPoint)probe.Client.LocalEndPoint).Port;
        using (var host = new NetworkService())
        using (var client = new NetworkService())
        {
            host.StartHost(port, 4, "ReconnectHost");
            client.StartClient("127.0.0.1", port, "ReconnectGuest");
            for (var i = 0; i < 300 && !client.IsConnected; i++)
            {
                host.Update(0.02f, Vector3.zero, Quaternion.identity, Vector3.zero);
                client.Update(0.02f, Vector3.zero, Quaternion.identity, Vector3.zero);
                Thread.Sleep(10);
            }
            Assert(client.IsConnected, "client connects before simulated host restart");

            host.Stop();
            for (var i = 0; i < 650 && !client.IsReconnecting; i++)
            {
                client.Update(0.02f, Vector3.zero, Quaternion.identity, Vector3.zero);
                Thread.Sleep(10);
            }
            Assert(client.IsReconnecting, "client enters reconnect state after host disappears");

            host.StartHost(port, 4, "ReconnectHost");
            for (var i = 0; i < 350 && !client.IsConnected; i++)
            {
                host.Update(0.02f, Vector3.zero, Quaternion.identity, Vector3.zero);
                client.Update(0.02f, Vector3.zero, Quaternion.identity, Vector3.zero);
                Thread.Sleep(10);
            }
            Assert(client.IsConnected && host.Players.ContainsKey(2), "client automatically rejoins restarted host");
        }
    }

    private static void ClientReconnectsWithSameSessionIdentity()
    {
        int port;
        using (var probe = new UdpClient(0)) port = ((IPEndPoint)probe.Client.LocalEndPoint).Port;
        using (var host = new NetworkService())
        using (var client = new NetworkService())
        {
            host.StartHost(port, 2, "ResumeHost");
            client.StartClient("127.0.0.1", port, "ResumeGuest");
            var oldPosition = new Vector3(17f, 4f, -8f);
            var newPosition = new Vector3(95f, 12f, 41f);
            var inventory = WeaponInventoryPolicy.Add(0, 5, 2);
            for (var i = 0; i < 240 && (!client.IsConnected || !host.Players.ContainsKey(2)); i++)
            {
                host.Update(0.02f, Vector3.zero, Quaternion.identity, Vector3.zero);
                client.Update(0.02f, oldPosition, Quaternion.identity, new Vector3(2f, 0f, 1f),
                    2, 5, 2, 64, Quaternion.identity, "RocketLauncher", inventory);
                Thread.Sleep(5);
            }
            Assert(client.IsConnected && client.LocalPlayerId == 2 && host.Players.ContainsKey(2), "session established before reconnect recovery");
            for (var i = 0; i < 40; i++)
            {
                host.Update(0.02f, Vector3.zero, Quaternion.identity, Vector3.zero);
                client.Update(0.02f, oldPosition, Quaternion.identity, new Vector3(2f, 0f, 1f),
                    2, 5, 2, 64, Quaternion.identity, "RocketLauncher", inventory);
                Thread.Sleep(5);
            }
            Assert(host.SetAuthoritativePlayerVitals(2, 64, false), "host commits the guest's current health before reconnect");
            for (var i = 0; i < 20; i++)
            {
                host.Update(0.02f, Vector3.zero, Quaternion.identity, Vector3.zero);
                client.Update(0.02f, oldPosition, Quaternion.identity, new Vector3(2f, 0f, 1f),
                    2, 5, 2, 64, Quaternion.identity, "RocketLauncher", inventory);
                Thread.Sleep(5);
            }
            var oldId = client.LocalPlayerId;
            Assert(host.Players[oldId].Health == 64 && host.Players[oldId].WeaponInventoryMask == inventory,
                "host has the player's live state before a temporary disconnect");

            PlayerSnapshot restoredSnapshot = null;
            host.PlayerJoined += player => { if (player.PlayerId == oldId) restoredSnapshot = player.Clone(); };
            Assert(client.RequestReconnect(), "client can intentionally trigger automatic session recovery");
            for (var i = 0; i < 80 && host.Players.ContainsKey(oldId); i++)
            {
                host.Update(0.02f, Vector3.zero, Quaternion.identity, Vector3.zero);
                client.Update(0.02f, newPosition, Quaternion.identity, Vector3.zero, 0, 1, 0, 21);
                Thread.Sleep(5);
            }
            Assert(!host.Players.ContainsKey(oldId) && client.IsReconnecting, "host removes the disconnected player while retaining a recovery slot");
            for (var i = 0; i < 360 && (!client.IsConnected || restoredSnapshot == null); i++)
            {
                host.Update(0.02f, Vector3.zero, Quaternion.identity, Vector3.zero);
                client.Update(0.02f, newPosition, Quaternion.identity, Vector3.zero, 0, 1, 0, 21);
                Thread.Sleep(5);
            }
            Assert(client.IsConnected && client.LocalPlayerId == oldId && host.Players.ContainsKey(oldId),
                "automatic reconnect restores the original session player ID");
            Assert(restoredSnapshot != null && restoredSnapshot.Health == 64 && restoredSnapshot.Position == oldPosition &&
                restoredSnapshot.WeaponInventoryMask == inventory && restoredSnapshot.WeaponName == "RocketLauncher",
                "host restores the last authoritative roster snapshot before accepting fresh client state");
        }
    }

    private static void ReconnectBackoffChecks()
    {
        Assert(ReconnectBackoff.DelaySeconds(0) == 1f, "reconnect starts with a one-second delay");
        Assert(ReconnectBackoff.DelaySeconds(1) == 2f, "reconnect backoff doubles after a failed attempt");
        Assert(ReconnectBackoff.DelaySeconds(4) == 16f, "reconnect backoff grows exponentially");
        Assert(ReconnectBackoff.DelaySeconds(8) == ReconnectBackoff.MaximumDelaySeconds, "reconnect backoff stays bounded");
    }

    private static void JoinSessionStateChecks()
    {
        const string scene = "1-2 The Burning World";
        Assert(JoinSessionStateCodec.Decode(JoinSessionStateCodec.Encode(scene)) == scene, "late join active-scene round-trip");
        var rejected = false;
        try { JoinSessionStateCodec.Decode(new byte[] { 2, 0xC3, 0x28 }); }
        catch (System.IO.InvalidDataException) { checks++; rejected = true; }
        if (!rejected) throw new Exception("Expected invalid UTF-8 join-state rejection.");
    }

    private static void EnemyIdentityKeyChecks()
    {
        var key = EnemyIdentityKey.HashPath("0:Level/3:Enemies/2:Drone");
        Assert(key != 0 && key == EnemyIdentityKey.HashPath("0:Level/3:Enemies/2:Drone"), "enemy hierarchy identity is stable");
        Assert(key != EnemyIdentityKey.HashPath("0:Level/3:Enemies/3:Drone"), "enemy sibling path distinguishes same-type actors");
        Assert(EnemyIdentityKey.HashPath(null) == 0, "empty enemy identity does not produce a key");
    }

    private static void WeaponFireRoundTrip()
    {
        var halfYaw = 75d * Math.PI / 360d;
        var aim = new Quaternion(0f, (float)Math.Sin(halfYaw), 0f, (float)Math.Cos(halfYaw));
        var decoded = WeaponFireCodec.Decode(WeaponFireCodec.Encode(3, 1, 7, aim));
        Assert(decoded.WeaponSlot == 3 && decoded.WeaponVariation == 1 && decoded.FireKind == 7, "weapon-fire event round-trip");
        Assert(Math.Abs(decoded.AimRotation.y - aim.y) < 0.0001f && Math.Abs(decoded.AimRotation.w - aim.w) < 0.0001f,
            "weapon-fire event preserves the camera aim rotation");
        Assert(WeaponFireCodec.IsPlausibleAimRotation(decoded.AimRotation, aim), "weapon aim is accepted when it matches the recent snapshot");
        Assert(!WeaponFireCodec.IsPlausibleAimRotation(new Quaternion(0f, 1f, 0f, 0f), new Quaternion(0f, 0f, 0f, 1f)),
            "weapon aim far from the recent snapshot is rejected");
        Assert(WeaponFireCodec.IsValidWeaponFire(1, 0) && WeaponFireCodec.IsValidWeaponFire(1, 1), "Revolver accepts its primary and alternate fire kinds");
        Assert(WeaponFireCodec.IsValidWeaponFire(2, 2) && WeaponFireCodec.IsValidWeaponFire(2, 3) && WeaponFireCodec.IsValidWeaponFire(2, 4), "Shotgun accepts shot, Sinks and Saw fire kinds");
        Assert(WeaponFireCodec.IsValidWeaponFire(3, 6) && WeaponFireCodec.IsValidWeaponFire(3, 7), "Nailgun accepts primary and special fire kinds");
        Assert(WeaponFireCodec.IsValidWeaponFire(4, 5) && WeaponFireCodec.IsValidWeaponFire(5, 7) &&
            WeaponFireCodec.IsValidWeaponFire(5, 8) && WeaponFireCodec.IsValidWeaponFire(5, 9), "Railcannon and all Rocket Launcher fire kinds are accepted");
        Assert(WeaponFireCodec.Decode(WeaponFireCodec.Encode(5, 0, 7)).FireKind == 7 &&
            WeaponFireCodec.Decode(WeaponFireCodec.Encode(5, 0, 8)).FireKind == 8 &&
            WeaponFireCodec.Decode(WeaponFireCodec.Encode(5, 0, 9)).FireKind == 9, "Rocket Launcher rocket, Cannonball and Napalm event kinds survive the wire codec");
        Assert(!WeaponFireCodec.IsValidWeaponFire(1, 7) && !WeaponFireCodec.IsValidWeaponFire(2, 0) &&
            !WeaponFireCodec.IsValidWeaponFire(3, 8) && !WeaponFireCodec.IsValidWeaponFire(3, 9) &&
            !WeaponFireCodec.IsValidWeaponFire(4, 7) && !WeaponFireCodec.IsValidWeaponFire(6, 7), "fire kinds cannot be spoofed across weapon slots");
        var rejected = false;
        try { WeaponFireCodec.Decode(new byte[] { 3, 1, 8 }); }
        catch (System.IO.InvalidDataException) { checks++; rejected = true; }
        if (!rejected) throw new Exception("Expected invalid weapon-fire kind rejection.");
        rejected = false;
        try { WeaponFireCodec.Decode(new byte[] { 1, 0, 7 }); }
        catch (System.IO.InvalidDataException) { checks++; rejected = true; }
        if (!rejected) throw new Exception("Expected weapon/fire-kind mismatch rejection.");
        rejected = false;
        try { WeaponFireCodec.Encode(5, 0, 10); }
        catch (ArgumentOutOfRangeException) { checks++; rejected = true; }
        if (!rejected) throw new Exception("Expected out-of-range Rocket Launcher fire-kind rejection.");
        rejected = false;
        try { WeaponFireCodec.Encode(1, 0, 7); }
        catch (ArgumentException) { checks++; rejected = true; }
        if (!rejected) throw new Exception("Expected invalid local weapon-fire combination rejection.");
        rejected = false;
        try { WeaponFireCodec.Encode(1, 0, 0, new Quaternion(float.NaN, 0f, 0f, 1f)); }
        catch (ArgumentException) { checks++; rejected = true; }
        if (!rejected) throw new Exception("Expected invalid weapon-fire aim rotation rejection.");
    }

    private static void WeaponFireRateLimitChecks()
    {
        var limiter = new WeaponFireRateLimiter(12f, 60f);
        for (var i = 0; i < 12; i++) Assert(limiter.TryAccept(2, 1f), "weapon-fire limiter permits bounded initial burst");
        Assert(!limiter.TryAccept(2, 1f), "weapon-fire limiter blocks events above burst capacity");
        Assert(limiter.TryAccept(2, 1.02f), "weapon-fire limiter refills at the configured rate");
        Assert(limiter.TryAccept(3, 1f), "weapon-fire limit is tracked independently per player");
        Assert(!limiter.TryAccept(2, float.NaN), "weapon-fire limiter rejects invalid timestamps");
    }

    private static void PlayerAnimationCodecChecks()
    {
        var punch = PlayerAnimationCodec.Decode(PlayerAnimationCodec.Encode(PlayerAnimationAction.Punch, 0.5f));
        Assert(punch.Action == PlayerAnimationAction.Punch && Math.Abs(punch.Speed - 0.5f) < 0.001f,
            "punch animation preserves its speed multiplier");
        var coin = PlayerAnimationCodec.Decode(PlayerAnimationCodec.Encode(PlayerAnimationAction.CoinToss));
        Assert(coin.Action == PlayerAnimationAction.CoinToss && Math.Abs(coin.Speed - 1f) < 0.001f,
            "coin-toss animation round-trips");
        var parry = PlayerAnimationCodec.Decode(PlayerAnimationCodec.Encode(PlayerAnimationAction.Parry));
        Assert(parry.Action == PlayerAnimationAction.Parry && Math.Abs(parry.Speed - 1f) < 0.001f,
            "parry animation round-trip");
        Assert(PlayerAnimationCodec.RequiresReliableDelivery(PlayerAnimationAction.Parry) &&
            !PlayerAnimationCodec.RequiresReliableDelivery(PlayerAnimationAction.Punch) &&
            !PlayerAnimationCodec.RequiresReliableDelivery(PlayerAnimationAction.CoinToss),
            "parry delivery is reliable while punch and coin cosmetics remain unreliable");
        var rejected = false;
        try { PlayerAnimationCodec.Decode(new byte[] { 6, 100 }); }
        catch (System.IO.InvalidDataException) { checks++; rejected = true; }
        if (!rejected) throw new Exception("Expected unsupported player-animation rejection.");
        rejected = false;
        try { PlayerAnimationCodec.Decode(new byte[] { (byte)PlayerAnimationAction.Punch, 24 }); }
        catch (System.IO.InvalidDataException) { checks++; rejected = true; }
        if (!rejected) throw new Exception("Expected out-of-range animation speed rejection.");
        rejected = false;
        try { PlayerAnimationCodec.Encode(PlayerAnimationAction.Punch, float.NaN); }
        catch (ArgumentOutOfRangeException) { checks++; rejected = true; }
        if (!rejected) throw new Exception("Expected non-finite animation speed rejection.");
    }

    private static void CoinProjectileChecks()
    {
        var launch = new CoinProjectileEvent
        {
            WeaponVariation = 1,
            Position = new Vector3(10f, 2f, -4f),
            Rotation = Quaternion.identity,
            Velocity = new Vector3(18f, 26f, 9f),
            Gravity = new Vector3(0f, -9.81f, 0f),
            AngularVelocity = new Vector3(0f, 12f, 0f)
        };
        var request = CoinProjectileCodec.DecodeLaunchRequest(CoinProjectileCodec.EncodeLaunchRequest(launch));
        Assert(request.WeaponVariation == 1 && request.Position == launch.Position && request.Velocity == launch.Velocity,
            "coin launch request round-trips weapon variant and transform/velocity");
        Assert(request.Gravity == launch.Gravity && request.AngularVelocity == launch.AngularVelocity,
            "coin launch request preserves custom gravity and spin");
        var relay = CoinProjectileCodec.DecodeRelay(CoinProjectileCodec.EncodeRelay(4, launch));
        Assert(relay.OwnerPlayerId == 4 && relay.Rotation == launch.Rotation, "coin relay carries the authoritative owner and orientation");
        Assert(CoinProjectileCodec.IsLaunchNearPlayer(launch, new Vector3(11f, 2f, -4f)) &&
            !CoinProjectileCodec.IsLaunchNearPlayer(launch, new Vector3(20f, 2f, -4f)),
            "coin launch origin must be close to the shooter's recent position");
        var player = new PlayerSnapshot
        {
            PlayerId = 2, Position = launch.Position, Rotation = Quaternion.identity, ViewRotation = Quaternion.identity,
            WeaponSlot = 1, WeaponVariation = 1, WeaponName = "Revolver(Clone)",
            WeaponInventoryMask = WeaponInventoryPolicy.Add(0, 1, 1), MovementFlags = 0
        };
        Assert(CoinProjectilePolicy.IsAuthorizedLaunch(player, launch), "host accepts a nearby coin launch from a live player with the selected unlocked Revolver");
        var invalidPlayer = player.Clone();
        invalidPlayer.WeaponVariation = 0;
        Assert(!CoinProjectilePolicy.IsAuthorizedLaunch(invalidPlayer, launch), "host rejects coin launches from an unselected Revolver variant");
        invalidPlayer = player.Clone();
        invalidPlayer.MovementFlags = 4;
        Assert(!CoinProjectilePolicy.IsAuthorizedLaunch(invalidPlayer, launch), "host rejects coin launches from a dead player");
        invalidPlayer = player.Clone();
        invalidPlayer.WeaponName = "Shotgun";
        Assert(!CoinProjectilePolicy.IsAuthorizedLaunch(invalidPlayer, launch), "host rejects coin launches when the selected weapon is not the Revolver");
        var invalidLaunch = new CoinProjectileEvent
        {
            WeaponVariation = 1, Position = new Vector3(100f, 2f, -4f), Rotation = Quaternion.identity,
            Velocity = launch.Velocity, Gravity = launch.Gravity, AngularVelocity = launch.AngularVelocity
        };
        Assert(!CoinProjectilePolicy.IsAuthorizedLaunch(player, invalidLaunch), "host rejects a coin launch whose origin is far from the recent player position");
        var rejected = false;
        try { CoinProjectileCodec.DecodeLaunchRequest(new byte[] { 1, 2, 3 }); }
        catch (System.IO.InvalidDataException) { checks++; rejected = true; }
        if (!rejected) throw new Exception("Expected malformed coin projectile rejection.");
        rejected = false;
        try { CoinProjectileCodec.EncodeLaunchRequest(new CoinProjectileEvent { Position = Vector3.zero, Rotation = Quaternion.identity, Gravity = Vector3.down * 9.81f, AngularVelocity = Vector3.zero, Velocity = Vector3.one * 3000f }); }
        catch (ArgumentOutOfRangeException) { checks++; rejected = true; }
        if (!rejected) throw new Exception("Expected out-of-range coin launch speed rejection.");
    }

    private static void WeaponInventoryChecks()
    {
        Assert(WeaponInventoryPolicy.ToNetworkSlot(1) == 1 && WeaponInventoryPolicy.ToNetworkSlot(5) == 5,
            "one-based game weapon selection preserves the network slot convention");
        Assert(WeaponInventoryPolicy.ToNetworkSlot(0) == 0 && WeaponInventoryPolicy.ToNetworkSlot(7) == 0,
            "invalid game weapon slot converts to unarmed instead of a mismatched weapon");
        var guestInventory = WeaponInventoryPolicy.Add(0, 1, 0);
        guestInventory = WeaponInventoryPolicy.Add(guestInventory, 2, 1);
        guestInventory = WeaponInventoryPolicy.Add(guestInventory, 5, 2);
        Assert(WeaponInventoryPolicy.IsUnlocked(guestInventory, 1, 0), "player inventory tracks first slot weapon independently");
        Assert(WeaponInventoryPolicy.IsUnlocked(guestInventory, 2, 1), "player inventory tracks alternate weapon variant independently");
        Assert(WeaponInventoryPolicy.IsUnlocked(guestInventory, 5, 2), "player inventory tracks late slot weapon independently");
        Assert(!WeaponInventoryPolicy.IsUnlocked(0, 1, 0), "empty host inventory does not grant its weapons to a guest");
        Assert(!WeaponInventoryPolicy.IsUnlocked(guestInventory, 2, 0), "locked variant is rejected while another variant is unlocked");
        Assert(!WeaponInventoryPolicy.IsUnlocked(guestInventory, 7, 0), "out-of-range weapon slot is rejected");
        Assert(!WeaponInventoryPolicy.IsUnlocked(guestInventory, 1, 8), "out-of-range weapon variation is rejected");
        Assert(WeaponInventoryPolicy.IsValidMask(guestInventory), "weapon inventory mask stays within the six-slot layout");
        Assert(!WeaponInventoryPolicy.IsValidMask(1UL << 48), "weapon inventory mask rejects bits outside the slot layout");
    }

    private static void WorldMutationCodecChecks()
    {
        var wood = new WorldMutation
        {
            Kind = WorldMutationKind.Breakable,
            Scene = "1-2 The Burning World",
            Path = "Level/Geometry/BreakableWood (2)",
            ComponentIndex = 1,
            Strength = 42.5f
        };
        var decodedWood = WorldMutationCodec.Decode(WorldMutationCodec.Encode(wood));
        Assert(decodedWood.Kind == WorldMutationKind.Breakable && decodedWood.Scene == wood.Scene &&
            decodedWood.Path == wood.Path && decodedWood.ComponentIndex == wood.ComponentIndex &&
            Math.Abs(decodedWood.Strength - wood.Strength) < 0.001f,
            "breakable wood mutation preserves stable map identity and damage strength");

        var glass = new WorldMutation
        {
            Kind = WorldMutationKind.Glass,
            Scene = "1-2 The Burning World",
            Path = "Level/Windows/Glass (4)",
            Strength = 0f
        };
        Assert(WorldMutationCodec.Decode(WorldMutationCodec.Encode(glass)).Kind == WorldMutationKind.Glass,
            "glass shatter mutation round-trips through the world event codec");

        var rejected = false;
        try { WorldMutationCodec.Encode(new WorldMutation { Kind = WorldMutationKind.Breakable, Scene = "1-2", Path = "wood", Strength = float.NaN }); }
        catch (System.IO.InvalidDataException) { checks++; rejected = true; }
        if (!rejected) throw new Exception("Expected invalid world mutation rejection.");
    }

    private static void WorldAudioCodecChecks()
    {
        var sound = new WorldAudioEvent
        {
            OwnerPlayerId = 2,
            ClipName = "Enemy_Hurt_Heavy",
            Position = new Vector3(12.5f, 4f, -8f),
            Volume = 0.65f,
            Pitch = 0.9f,
            SpatialBlend = 1f
        };
        var decoded = WorldAudioCodec.Decode(WorldAudioCodec.Encode(sound));
        Assert(decoded.OwnerPlayerId == sound.OwnerPlayerId && decoded.ClipName == sound.ClipName &&
            decoded.Position == sound.Position && Math.Abs(decoded.Volume - sound.Volume) < 0.001f &&
            Math.Abs(decoded.Pitch - sound.Pitch) < 0.001f && decoded.SpatialBlend == 1f,
            "world sound event preserves owner, clip, position and spatial mix parameters");

        var rejected = false;
        try { WorldAudioCodec.Encode(new WorldAudioEvent { OwnerPlayerId = 0, ClipName = "bad", Pitch = 1f }); }
        catch (System.IO.InvalidDataException) { checks++; rejected = true; }
        if (!rejected) throw new Exception("Expected invalid world audio event rejection.");
    }

    private static void RelayThreadHelperBootstrapChecks()
    {
        var previousContext = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
            UnityRelayTransport.InitializeRelayThreadHelper();
            var helper = typeof(RelayService).Assembly.GetType("Unity.Services.Relay.Scheduler.ThreadHelper", true);
            var scheduler = helper.GetProperty("TaskScheduler", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            Assert(scheduler != null && scheduler.GetValue(null) != null,
                "late-loaded Relay SDK captures a main-thread scheduler before HTTP allocation calls");
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
    }

    private static void ProtocolRoundTrip()
    {
        var payload = new byte[] { 0, 1, 2, 127, 255 };
        var original = new NetworkMessage(MessageType.PlayerSnapshot, 0x12345678, payload);
        var decoded = NetworkMessage.Decode(original.Encode());
        Assert(decoded.ProtocolVersion == NetworkMessage.CurrentProtocolVersion, "protocol version round-trip");
        Assert(NetworkService.DefaultConnectionKey.EndsWith(NetworkMessage.CurrentProtocolVersion.ToString(), StringComparison.Ordinal), "connection key tracks the wire protocol version");
        IPAddress bindAddress;
        Assert(NetworkService.TryParseIPv4BindAddress("0.0.0.0", out bindAddress) && bindAddress.Equals(IPAddress.Any), "all-adapter IPv4 bind address is accepted");
        Assert(NetworkService.TryParseIPv4BindAddress("26.42.11.8", out bindAddress), "explicit IPv4 VPN bind address is accepted");
        Assert(!NetworkService.TryParseIPv4BindAddress("::1", out bindAddress), "IPv6 is rejected by the IPv4-only bind field");
        Assert(!NetworkService.TryParseIPv4BindAddress("radmin", out bindAddress), "non-address bind value is rejected");
        Assert(decoded.Type == MessageType.PlayerSnapshot, "message type round-trip");
        Assert(decoded.Sequence == 0x12345678, "sequence round-trip");
        Assert(Equal(decoded.Payload, payload), "payload round-trip");
        Assert(NetworkMessage.Decode(new NetworkMessage(MessageType.Ping, 4, Array.Empty<byte>()).Encode()).Payload.Length == 0, "empty payload round-trip");
        Assert(NetworkMessage.Decode(new NetworkMessage(MessageType.PlayerAnimation, 5,
            PlayerAnimationCodec.Encode(PlayerAnimationAction.Punch, 0.5f)).Encode()).Type == MessageType.PlayerAnimation,
            "player-animation message type round-trip");
    }

    private static void ProtocolRejectsMalformedFrames()
    {
        ExpectInvalid(new byte[3], "short frame");
        var valid = new NetworkMessage(MessageType.Ping, 1, new byte[] { 7 }).Encode();
        var badMagic = (byte[])valid.Clone(); badMagic[0] ^= 0xFF;
        ExpectInvalid(badMagic, "bad magic");
        var badVersion = (byte[])valid.Clone(); badVersion[4]++;
        ExpectInvalid(badVersion, "unsupported protocol version");
        var badType = (byte[])valid.Clone(); badType[5] = 255;
        ExpectInvalid(badType, "unknown message type");
        var badLength = (byte[])valid.Clone(); badLength[12]++;
        ExpectInvalid(badLength, "payload length mismatch");
    }

    private static void LevelTransitionRoundTrip()
    {
        var encoded = LevelTransitionCodec.Encode(42, LevelTransitionStage.Prepare, "1-2 The Burning World", true, 1, 3, new uint[] { 1 });
        var decoded = LevelTransitionCodec.Decode(encoded);
        Assert(decoded.TransitionId == 42, "level transition ID round-trip");
        Assert(decoded.Stage == LevelTransitionStage.Prepare, "level transition stage round-trip");
        Assert(decoded.ForceReload, "level transition preserves explicit same-scene restart intent");
        Assert(decoded.SceneName == "1-2 The Burning World", "level transition scene round-trip");
        Assert(decoded.ReadyPlayerCount == 1 && decoded.TotalPlayerCount == 3, "level prepare includes the current ready-player count");
        Assert(decoded.ReadyPlayerIds.Length == 1 && decoded.ReadyPlayerIds[0] == 1, "level prepare includes the ready player identity");
        var ready = LevelTransitionCodec.Decode(LevelTransitionCodec.Encode(42, LevelTransitionStage.Ready, "1-2 The Burning World"));
        Assert(ready.Stage == LevelTransitionStage.Ready, "level readiness acknowledgment round-trip");
        var progress = LevelTransitionCodec.Decode(LevelTransitionCodec.Encode(42, LevelTransitionStage.Progress, "1-2 The Burning World", false, 2, 3, new uint[] { 1, 2 }));
        Assert(progress.Stage == LevelTransitionStage.Progress && progress.ReadyPlayerCount == 2 && progress.TotalPlayerCount == 3,
            "host readiness progress round-trips for the loading overlay");
        Assert(progress.ReadyPlayerIds.Length == 2 && progress.ReadyPlayerIds[1] == 2, "loading progress preserves the ready-player list");
        var invalidCountsRejected = false;
        try { LevelTransitionCodec.Encode(42, LevelTransitionStage.Progress, "1-2 The Burning World", false, 4, 3); }
        catch (ArgumentOutOfRangeException) { checks++; invalidCountsRejected = true; }
        if (!invalidCountsRejected) throw new Exception("Expected invalid transition player counts to be rejected.");
        invalidCountsRejected = false;
        try { LevelTransitionCodec.Encode(42, LevelTransitionStage.Progress, "1-2 The Burning World"); }
        catch (ArgumentOutOfRangeException) { checks++; invalidCountsRejected = true; }
        if (!invalidCountsRejected) throw new Exception("Expected empty host transition progress to be rejected.");
        var invalidReadyIdsRejected = false;
        try { LevelTransitionCodec.Encode(42, LevelTransitionStage.Progress, "1-2 The Burning World", false, 2, 3, new uint[] { 1, 1 }); }
        catch (ArgumentException) { checks++; invalidReadyIdsRejected = true; }
        if (!invalidReadyIdsRejected) throw new Exception("Expected duplicate ready-player IDs to be rejected.");
        invalidReadyIdsRejected = false;
        try { LevelTransitionCodec.Encode(42, LevelTransitionStage.Progress, "1-2 The Burning World", false, 1, 3, new uint[] { 0 }); }
        catch (ArgumentException) { checks++; invalidReadyIdsRejected = true; }
        if (!invalidReadyIdsRejected) throw new Exception("Expected zero ready-player IDs to be rejected.");
        var badLength = (byte[])encoded.Clone(); badLength[6]++;
        var malformedRejected = false;
        try { LevelTransitionCodec.Decode(badLength); }
        catch (System.IO.InvalidDataException) { checks++; malformedRejected = true; }
        if (!malformedRejected) throw new Exception("Expected malformed transition rejection.");
        var badStage = (byte[])encoded.Clone(); badStage[4] = 255;
        var invalidStageRejected = false;
        try { LevelTransitionCodec.Decode(badStage); }
        catch (System.IO.InvalidDataException) { checks++; invalidStageRejected = true; }
        if (!invalidStageRejected) throw new Exception("Expected invalid transition stage rejection.");
        try { LevelTransitionCodec.Encode(43, LevelTransitionStage.Prepare, "invalid\nscene"); }
        catch (ArgumentException) { checks++; return; }
        throw new Exception("Expected unsafe scene name rejection.");
    }

    private static void LevelReadinessBarrierChecks()
    {
        var barrier = new LevelReadinessBarrier();
        barrier.Begin(8, "1-2 The Burning World", new uint[] { 1, 2, 3 }, 1);
        Assert(!barrier.CanStart(true), "level barrier waits for host scene load even after timeout");
        Assert(barrier.MarkHostReady(8, "1-2 The Burning World"), "host scene readiness is recorded");
        Assert(!barrier.CanStart(false), "level barrier waits for connected players");
        Assert(!barrier.MarkPlayerReady(2, 7, "1-2 The Burning World"), "readiness from another transition is rejected");
        Assert(!barrier.MarkPlayerReady(99, 8, "1-2 The Burning World"), "readiness from an unknown player is rejected");
        Assert(barrier.MarkPlayerReady(2, 8, "1-2 The Burning World"), "known player readiness is recorded");
        Assert(barrier.IsPlayerReady(2) && !barrier.IsPlayerReady(3), "barrier exposes per-player readiness for initial state sync");
        var readyPlayerIds = barrier.GetReadyPlayerIds();
        Assert(readyPlayerIds.Length == 2 && readyPlayerIds[0] == 1 && readyPlayerIds[1] == 2,
            "barrier reports sorted ready IDs including the host");
        Assert(barrier.MarkPlayerReady(3, 8, "1-2 The Burning World"), "last player readiness is recorded");
        Assert(barrier.IsPlayerReady(3), "barrier records readiness independently for every participant");
        Assert(barrier.CanStart(false), "level barrier opens when every participant is ready");
        barrier.MarkStarted();
        Assert(!barrier.CanStart(true), "started level barrier cannot be started twice");

        barrier.Begin(9, "1-3 Halls of Sacred Remains", new uint[] { 1, 2, 3 }, 1);
        barrier.MarkHostReady(9, "1-3 Halls of Sacred Remains");
        barrier.MarkPlayerReady(2, 9, "1-3 Halls of Sacred Remains");
        barrier.RemovePlayer(3);
        Assert(barrier.CanStart(false), "disconnecting a not-ready player releases the barrier");
        barrier.MarkStarted();

        barrier.Begin(10, "1-4 Clair de Lune", new uint[] { 1, 2 }, 1);
        barrier.MarkHostReady(10, "1-4 Clair de Lune");
        Assert(!barrier.CanStart(true), "readiness timeout cannot bypass a client's manual confirmation");
        barrier.MarkPlayerReady(2, 10, "1-4 Clair de Lune");
        Assert(barrier.CanStart(), "manual confirmation from the last player opens the barrier");
        barrier.MarkStarted();
        barrier.AddPlayer(3);
        Assert(barrier.IsStarted && !barrier.CanStart(false), "late join does not rewind a started transition");
        Assert(!barrier.MarkPlayerReady(2, 10, "wrong scene"), "readiness for a different scene is rejected");
    }

    private static void LevelProgressionEventChecks()
    {
        var eventData = LevelProgressionEventCodec.Decode(LevelProgressionEventCodec.Encode(
            LevelProgressionEventKind.WaveAdvanced, "1-2 The Burning World", "0:World/0:Arena/0:Encounter/0:Trigger", 2));
        Assert(eventData.Kind == LevelProgressionEventKind.WaveAdvanced, "level progression event kind round-trip");
        Assert(eventData.SceneName == "1-2 The Burning World" && eventData.ObjectPath == "0:World/0:Arena/0:Encounter/0:Trigger", "level progression event identifies its scene object");
        Assert(eventData.ComponentIndex == 2, "level progression event distinguishes components on one object");
        var invalidKindRejected = false;
        try { LevelProgressionEventCodec.Encode((LevelProgressionEventKind)99, "1-1", "0/1", 0); }
        catch (ArgumentOutOfRangeException) { checks++; invalidKindRejected = true; }
        if (!invalidKindRejected) throw new Exception("Expected invalid progression event rejection.");
        var invalidPathRejected = false;
        try { LevelProgressionEventCodec.Encode(LevelProgressionEventKind.ArenaActivated, "1-1", "../door", 0); }
        catch (ArgumentException) { checks++; invalidPathRejected = true; }
        if (!invalidPathRejected) throw new Exception("Expected unsafe progression path rejection.");
    }

    private static void PlayerLifecycleRoundTrip()
    {
        var encoded = PlayerLifecycleCodec.Encode(4, 0, true, 77);
        var decoded = PlayerLifecycleCodec.Decode(encoded);
        Assert(decoded.PlayerId == 4, "lifecycle player ID round-trip");
        Assert(decoded.Health == 0 && decoded.IsDead, "lifecycle death state round-trip");
        Assert(decoded.SnapshotTick == 77, "lifecycle update carries its source snapshot tick");
        var malformedRejected = false;
        try { PlayerLifecycleCodec.Decode(new byte[6]); }
        catch (System.IO.InvalidDataException) { checks++; malformedRejected = true; }
        if (!malformedRejected) throw new Exception("Expected malformed lifecycle event rejection.");
    }

    private static void PlayerLifecycleSnapshotOrderingChecks()
    {
        var lifecycle = PlayerLifecycleCodec.Decode(PlayerLifecycleCodec.Encode(2, 0, true, 100));
        var delayedSnapshot = new PlayerSnapshot { PlayerId = 2, Tick = 100, Health = 100, MovementFlags = 0 };
        PlayerLifecycleStatePolicy.ApplyToSnapshot(lifecycle, delayedSnapshot);
        Assert(delayedSnapshot.Health == 0 && (delayedSnapshot.MovementFlags & 4) != 0,
            "same-tick snapshot cannot undo reliable death state");

        var newerSnapshot = new PlayerSnapshot { PlayerId = 2, Tick = 101, Health = 100, MovementFlags = 0 };
        PlayerLifecycleStatePolicy.ApplyToSnapshot(lifecycle, newerSnapshot);
        Assert(newerSnapshot.Health == 0 && (newerSnapshot.MovementFlags & 4) != 0,
            "newer sequenced snapshot cannot roll back reliable lifecycle state");

        var wrappedLifecycle = PlayerLifecycleCodec.Decode(PlayerLifecycleCodec.Encode(2, 0, true, 1));
        var preWrapSnapshot = new PlayerSnapshot { PlayerId = 2, Tick = uint.MaxValue, Health = 100, MovementFlags = 0 };
        PlayerLifecycleStatePolicy.ApplyToSnapshot(wrappedLifecycle, preWrapSnapshot);
        Assert(preWrapSnapshot.Health == 0 && (preWrapSnapshot.MovementFlags & 4) != 0,
            "reliable lifecycle state remains authoritative across snapshot tick wraparound");
    }

    private static void SessionHandshakeCodecChecks()
    {
        var token = new string('A', SessionHandshakeCodec.SessionTokenHexLength);
        var decoded = SessionHandshakeCodec.DecodeHello(SessionHandshakeCodec.EncodeHello("Jogador Órbita", token));
        Assert(decoded.PlayerName == "Jogador Órbita", "session handshake preserves UTF-8 player names");
        Assert(decoded.SessionToken == token.ToLowerInvariant(), "session handshake normalizes the bearer token");
        var badTokenRejected = false;
        try { SessionHandshakeCodec.EncodeHello("Player", "not-a-token"); }
        catch (ArgumentException) { checks++; badTokenRejected = true; }
        if (!badTokenRejected) throw new Exception("Expected invalid session token rejection.");
    }

    private static void ReconnectReservationStoreChecks()
    {
        var store = new ReconnectReservationStore();
        var token = new string('b', SessionHandshakeCodec.SessionTokenHexLength);
        var snapshot = new PlayerSnapshot
        {
            PlayerId = 4, PlayerName = "Returning", Position = new Vector3(13f, 2f, -7f),
            Rotation = Quaternion.identity, ViewRotation = Quaternion.identity,
            Velocity = new Vector3(3f, 0f, 1f), Health = 64, Tick = 907,
            MovementFlags = 2, WeaponSlot = 5, WeaponVariation = 1, WeaponName = "RocketLauncher",
            WeaponInventoryMask = WeaponInventoryPolicy.Add(0, 5, 1)
        };
        store.Store(token, 4, "Returning", snapshot, 30f);
        snapshot.Position = Vector3.zero;
        ReconnectReservation reservation;
        Assert(store.TryTake(token.ToUpperInvariant(), 29f, out reservation), "valid reconnect token claims its reservation");
        Assert(reservation.PlayerId == 4 && reservation.PlayerName == "Returning", "reconnect reservation preserves player identity");
        Assert(reservation.Snapshot.Position == new Vector3(13f, 2f, -7f) && reservation.Snapshot.Health == 64 &&
            reservation.Snapshot.Tick == 907 && reservation.Snapshot.WeaponInventoryMask == WeaponInventoryPolicy.Add(0, 5, 1),
            "reconnect reservation keeps a detached full player snapshot");
        Assert(!store.TryTake(token, 30f, out reservation) && store.Count == 0, "expired reconnect reservations cannot be claimed");
    }

    private static void CheckpointEventRoundTrip()
    {
        var request = CheckpointEventCodec.Decode(CheckpointEventCodec.Encode(CheckpointEventKind.Request, "0/3/12"));
        Assert(request.Kind == CheckpointEventKind.Request, "checkpoint request kind round-trip");
        Assert(request.Path == "0/3/12", "checkpoint scene path round-trip");
        var commit = CheckpointEventCodec.Decode(CheckpointEventCodec.Encode(CheckpointEventKind.Commit, "1/0/7"));
        Assert(commit.Kind == CheckpointEventKind.Commit && commit.Path == "1/0/7", "checkpoint commit round-trip");
        var unsafePathRejected = false;
        try { CheckpointEventCodec.Encode(CheckpointEventKind.Request, "../../checkpoint"); }
        catch (ArgumentException) { checks++; unsafePathRejected = true; }
        if (!unsafePathRejected) throw new Exception("Expected invalid checkpoint path rejection.");
    }

    private static void CheckpointSessionStateChecks()
    {
        var state = new CheckpointSessionState();
        string path;
        Assert(!state.TryGetForScene("1-1 The Mourning", out path), "checkpoint state starts empty");
        state.BeginScene("1-1 The Mourning");
        state.RecordCommit("1-1 The Mourning", "0/4/2");
        Assert(state.TryGetForScene("1-1 The Mourning", out path) && path == "0/4/2", "active checkpoint is retained for late join sync");
        Assert(!state.TryGetForScene("1-2 The Burning World", out path), "checkpoint state is not replayed into a different phase");
        state.BeginScene("1-1 The Mourning");
        Assert(!state.TryGetForScene("1-1 The Mourning", out path), "restarting a phase clears its prior checkpoint commit");
    }

    private static void DamageRequestPolicyChecks()
    {
        Assert(DamageRequestPolicy.IsValid(1, "Revolver", 4f, 1f, false, 10000f, 0f, 0f, 10f, 20f, 30f), "valid host damage request accepted");
        Assert(!DamageRequestPolicy.IsValid(1, "", 1f, 0f, false, 0f, 0f, 0f, 0f, 0f, 0f), "damage without a weapon is rejected");
        Assert(!DamageRequestPolicy.IsValid(1, "Revolver", 51f, 0f, false, 0f, 0f, 0f, 0f, 0f, 0f), "excessive damage multiplier rejected");
        Assert(!DamageRequestPolicy.IsValid(1, "Revolver", 1f, 11f, false, 0f, 0f, 0f, 0f, 0f, 0f), "excessive critical multiplier rejected");
        Assert(DamageRequestPolicy.IsValid(1, "Revolver", 1f, 0f, true, 0f, 0f, 0f, 0f, 0f, 0f), "legitimate hit that preserves the game's ignore-scale flag is relayable");
        Assert(!DamageRequestPolicy.IsValid(1, "Revolver", 1f, 0f, false, 30000f, 0f, 0f, 0f, 0f, 0f), "excessive knockback rejected");
        Assert(!DamageRequestPolicy.IsValid(1, "Revolver", float.NaN, 0f, false, 0f, 0f, 0f, 0f, 0f, 0f), "non-finite damage multiplier rejected");
        Assert(DamageRequestPolicy.IsSelectedWeapon("Shotgun(Clone)", "Shotgun"), "selected weapon check accepts a runtime clone suffix");
        Assert(!DamageRequestPolicy.IsSelectedWeapon("Railcannon", "Revolver"), "selected weapon check rejects another weapon");
        Assert(DamageRequestPolicy.IsImpactNearTarget(0f, 0f, 0f, 1f, 0f, 0f, 1f), "impact at collider tolerance is accepted");
        Assert(!DamageRequestPolicy.IsImpactNearTarget(0f, 0f, 0f, 1.01f, 0f, 0f, 1f), "impact outside collider tolerance is rejected");
        Assert(!DamageRequestPolicy.IsImpactNearTarget(float.NaN, 0f, 0f, 0f, 0f, 0f, 1f), "non-finite impact is rejected");
        Assert(!DamageRequestPolicy.IsImpactNearTarget(0f, 0f, 0f, 0f, 0f, 0f, -1f), "negative impact tolerance is rejected");
        Assert(DamageRequestPolicy.IsValidHitterForWeapon("Revolver(Clone)", "revolver"), "revolver damage preserves its validated hitter type");
        Assert(DamageRequestPolicy.IsValidHitterForWeapon("Shotgun", "shotgunzone"), "shotgun damage preserves its validated hitter type");
        Assert(DamageRequestPolicy.IsValidHitterForWeapon("RocketLauncher", "explosion"), "weaponless rocket explosion can be attributed to the shooter's selected weapon");
        Assert(DamageRequestPolicy.IsValidHitterForWeapon("Nailgun", "sawblade"), "nailgun damage preserves its validated hitter type");
        Assert(!DamageRequestPolicy.IsValidHitterForWeapon("Revolver", "cannonball"), "damage hitter from another weapon is rejected");
        Assert(!DamageRequestPolicy.IsValidHitterForWeapon("Unknown", "revolver"), "unknown damage weapon hitter is rejected");
        Assert(DamageRequestPolicy.IsSupportedWeaponName("RocketLauncher(Clone)"), "remote rocket launcher weapon name is supported");
        Assert(!DamageRequestPolicy.IsSupportedWeaponName("UnknownWeapon"), "unknown remote weapon name is rejected");
        Assert(DamageRequestPolicy.IsSupportedWeaponName("Fist"), "weaponless fist damage uses the explicit fist identity");
        Assert(DamageRequestPolicy.IsValidHitterForWeapon("Fist", "punch"), "standard punch is accepted for fist damage");
        Assert(DamageRequestPolicy.IsValidHitterForWeapon("Fist", "heavypunch"), "heavy punch is accepted for fist damage");
        Assert(!DamageRequestPolicy.IsValidHitterForWeapon("Fist", "explosion"), "non-fist hitter is rejected for fist damage");
        Assert(DamageRequestPolicy.CanRelayWeaponlessDamage("punch") && DamageRequestPolicy.CanRelayWeaponlessDamage("heavypunch") &&
            !DamageRequestPolicy.CanRelayWeaponlessDamage("explosion") && !DamageRequestPolicy.CanRelayWeaponlessDamage(null),
            "weaponless enemy damage is relayable only for explicit fist hitters");
        Assert(DamageRequestPolicy.IsValid(1, "Fist", 1f, 0f, false, 25000f, 0f, 0f, 0f, 0f, 0f, "punch"), "standard punch damage and force limits are accepted");
        Assert(DamageRequestPolicy.IsValid(1, "Fist", 2.5f, 0f, false, 100000f, 0f, 0f, 0f, 0f, 0f, "heavypunch"), "heavy punch damage and force limits are accepted");
        Assert(!DamageRequestPolicy.IsValid(1, "Fist", 3f, 0f, false, 0f, 0f, 0f, 0f, 0f, 0f, "heavypunch"), "excessive heavy punch damage is rejected");
    }

    private static void EnemyDamageStateCodecChecks()
    {
        var state = new EnemyDamageState
        {
            NetworkId = 19,
            Frame = 42,
            StableKey = 0xAABBCCDD,
            EnemyType = 7,
            Position = new Vector3(1.5f, -2f, 3.25f),
            Rotation = Quaternion.identity,
            Health = 37.5f,
            Dead = false
        };
        var decoded = EnemyDamageStateCodec.Decode(EnemyDamageStateCodec.Encode(state));
        Assert(decoded.NetworkId == state.NetworkId && decoded.Frame == state.Frame, "authoritative enemy damage state carries identity and revision");
        Assert(decoded.StableKey == state.StableKey && decoded.EnemyType == state.EnemyType, "authoritative enemy damage state carries match metadata");
        Assert(decoded.Position == state.Position && decoded.Rotation == state.Rotation, "authoritative enemy damage state carries transform");
        Assert(Math.Abs(decoded.Health - state.Health) < 0.001f && decoded.Dead == state.Dead, "authoritative enemy damage state carries exact health and death state");
        state.Health = 0f;
        state.Dead = true;
        var deadBoss = EnemyDamageStateCodec.Decode(EnemyDamageStateCodec.Encode(state));
        Assert(deadBoss.Health == 0f && deadBoss.Dead, "boss or enemy lethal state is represented as an authoritative death event");
        var malformedRejected = false;
        try { EnemyDamageStateCodec.Decode(new byte[48]); }
        catch (System.IO.InvalidDataException) { checks++; malformedRejected = true; }
        if (!malformedRejected) throw new Exception("Expected invalid enemy damage state length rejection.");
        var invalidRejected = false;
        state.Health = float.NaN;
        try { EnemyDamageStateCodec.Encode(state); }
        catch (System.IO.InvalidDataException) { checks++; invalidRejected = true; }
        if (!invalidRejected) throw new Exception("Expected invalid enemy damage state value rejection.");
    }

    private static void PlayerSnapshotRoundTrip()
    {
        var snapshot = new PlayerSnapshot
        {
            PlayerId = 17,
            PlayerName = "Bloodlink",
            Position = new Vector3(1.25f, -8f, 100f),
            Rotation = new Quaternion(0.1f, 0.2f, 0.3f, 0.9f),
            ViewRotation = new Quaternion(0f, 0.2f, 0f, 0.98f),
            Velocity = new Vector3(2f, 3f, 4f),
            MovementFlags = 11,
            WeaponSlot = 5,
            WeaponVariation = 1,
            WeaponName = "RocketLauncher(Clone)",
            WeaponInventoryMask = WeaponInventoryPolicy.Add(0, 5, 1),
            Health = 147,
            Tick = 9001,
            PrimaryColor = 0xCC33FF,
            SecondaryColor = 0x123456,
            LightColor = 0xABCDEF,
            GlowIntensity = 201
        };
        var decoded = PlayerSnapshot.Decode(snapshot.Encode());
        Assert(decoded.PlayerId == snapshot.PlayerId, "player id round-trip");
        Assert(decoded.PlayerName == snapshot.PlayerName, "player name round-trip");
        Assert((decoded.Position - snapshot.Position).sqrMagnitude < 0.0001f, "player position round-trip");
        Assert(Math.Abs(decoded.Rotation.x - snapshot.Rotation.x) < 0.0001f && Math.Abs(decoded.Rotation.y - snapshot.Rotation.y) < 0.0001f && Math.Abs(decoded.Rotation.z - snapshot.Rotation.z) < 0.0001f && Math.Abs(decoded.Rotation.w - snapshot.Rotation.w) < 0.0001f, "player rotation round-trip");
        Assert(Math.Abs(decoded.ViewRotation.y - snapshot.ViewRotation.y) < 0.0001f && Math.Abs(decoded.ViewRotation.w - snapshot.ViewRotation.w) < 0.0001f, "player view rotation round-trip");
        Assert((decoded.Velocity - snapshot.Velocity).sqrMagnitude < 0.0001f, "player velocity round-trip");
        Assert(decoded.MovementFlags == snapshot.MovementFlags, "movement flags round-trip");
        Assert(decoded.WeaponSlot == snapshot.WeaponSlot && decoded.WeaponVariation == snapshot.WeaponVariation, "weapon selection round-trip");
        Assert(decoded.WeaponName == snapshot.WeaponName && decoded.WeaponInventoryMask == snapshot.WeaponInventoryMask, "independent weapon inventory round-trip");
        Assert(decoded.Health == snapshot.Health, "player health round-trip");
        Assert(decoded.Tick == snapshot.Tick, "player tick round-trip");
        Assert(decoded.PrimaryColor == snapshot.PrimaryColor && decoded.SecondaryColor == snapshot.SecondaryColor, "player primary and secondary colors round-trip");
        Assert(decoded.LightColor == snapshot.LightColor && decoded.GlowIntensity == snapshot.GlowIntensity, "player light color and intensity round-trip");
        var invalidColor = snapshot.Encode();
        Array.Copy(BitConverter.GetBytes(0x01000000u), 0, invalidColor, invalidColor.Length - 14, 4);
        var invalidColorRejected = false;
        try { PlayerSnapshot.Decode(invalidColor); }
        catch (System.IO.InvalidDataException) { checks++; invalidColorRejected = true; }
        if (!invalidColorRejected) throw new Exception("Expected invalid player color rejection.");
        var invalidInventory = new PlayerSnapshot
        {
            PlayerId = 19, PlayerName = "BadMask", Position = Vector3.zero, Rotation = Quaternion.identity,
            ViewRotation = Quaternion.identity, WeaponInventoryMask = 1UL << 48, Health = 100
        };
        var invalidInventoryRejected = false;
        try { PlayerSnapshot.Decode(invalidInventory.Encode()); }
        catch (System.IO.InvalidDataException) { checks++; invalidInventoryRejected = true; }
        if (!invalidInventoryRejected) throw new Exception("Expected invalid weapon inventory mask rejection.");
        var invalid = new PlayerSnapshot { PlayerId = 18, PlayerName = "Bad", Position = new Vector3(float.NaN, 0f, 0f), Rotation = Quaternion.identity, Health = 100 };
        try { PlayerSnapshot.Decode(invalid.Encode()); }
        catch (System.IO.InvalidDataException) { checks++; return; }
        throw new Exception("Expected invalid player coordinate rejection.");
    }

    private static void TwoPlayerHostClientSession()
    {
        int port;
        using (var probe = new UdpClient(0)) port = ((System.Net.IPEndPoint)probe.Client.LocalEndPoint).Port;
        using (var host = new NetworkService())
        using (var client = new NetworkService())
        {
            host.StartHost(port, 4, "Host", bindAddress: "127.0.0.1");
            client.StartClient("127.0.0.1", port, "Guest");
            host.SetLocalAppearance(0xCC2244, 0x222222, 0x77DDFF, 180);
            client.SetLocalAppearance(0x123456, 0x654321, 0xABCDEF, 72);
            var hostPos = new Vector3(1f, 2f, 3f);
            var clientPos = new Vector3(7f, 8f, 9f);
            for (var i = 0; i < 250; i++)
            {
                host.Update(0.01f, hostPos, Quaternion.identity, Vector3.zero);
                client.Update(0.01f, clientPos, Quaternion.identity, new Vector3(1f, 0f, 0f), 0, 2, 0, 73);
                Thread.Sleep(10);
                if (host.Players.ContainsKey(2) && client.IsConnected && client.Players.ContainsKey(1)) break;
            }
            Assert(client.IsConnected, "client receives reliable welcome (client: " + client.LastError + "; host: " + host.LastError + ")");
            Assert(host.Players.ContainsKey(2), "host assigns a remote player");
            Assert(client.Players.ContainsKey(1), "client receives host snapshot");
            Assert(host.SetAuthoritativePlayerVitals(2, 73, false), "host commits the guest's authoritative health state");
            for (var i = 0; i < 40 && client.Players[2].Health != 73; i++)
            {
                host.Update(0.02f, hostPos, Quaternion.identity, Vector3.zero);
                client.Update(0.02f, clientPos, Quaternion.identity, Vector3.zero);
                Thread.Sleep(5);
            }
            Assert(host.Players[2].Health == 73, "host receives remote player's health state");
            Assert(host.Players[2].PrimaryColor == 0x123456 && host.Players[2].GlowIntensity == 72, "host receives the guest's independent appearance");
            Assert(client.Players[1].PrimaryColor == 0xCC2244 && client.Players[1].GlowIntensity == 180, "client receives the host's independent appearance");
            byte[] joinState = null;
            client.MessageReceived += (sender, type, data) => { if (type == MessageType.JoinSessionState && sender == 1) joinState = data; };
            host.SendToPlayer(2, MessageType.JoinSessionState, JoinSessionStateCodec.Encode("1-2 The Burning World"));
            for (var i = 0; i < 20 && joinState == null; i++)
            {
                host.Update(0.02f, hostPos, Quaternion.identity, Vector3.zero);
                client.Update(0.02f, clientPos, Quaternion.identity, Vector3.zero, 0, 2, 0, 73);
                Thread.Sleep(5);
            }
            Assert(joinState != null && JoinSessionStateCodec.Decode(joinState) == "1-2 The Burning World", "host sends active scene state to a joining client");
            byte[] receivedEnemyDamageState = null;
            uint enemyDamageStateSender = 0;
            client.MessageReceived += (sender, type, data) => { if (type == MessageType.EnemyDamageState) { enemyDamageStateSender = sender; receivedEnemyDamageState = data; } };
            var enemyDamageStatePayload = EnemyDamageStateCodec.Encode(new EnemyDamageState
            {
                NetworkId = 5, Frame = 8, StableKey = 9, EnemyType = 2,
                Position = new Vector3(4f, 5f, 6f), Rotation = Quaternion.identity, Health = 12f, Dead = false
            });
            host.BroadcastReliable(MessageType.EnemyDamageState, enemyDamageStatePayload);
            for (var i = 0; i < 20 && receivedEnemyDamageState == null; i++)
            {
                host.Update(0.02f, hostPos, Quaternion.identity, Vector3.zero);
                client.Update(0.02f, clientPos, Quaternion.identity, Vector3.zero);
                Thread.Sleep(5);
            }
            Assert(enemyDamageStateSender == 1 && Equal(receivedEnemyDamageState, enemyDamageStatePayload), "client receives reliable authoritative enemy damage state");
            uint firedBy = 0;
            byte[] receivedFire = null;
            host.MessageReceived += (sender, type, data) => { if (type == MessageType.WeaponFire) { firedBy = sender; receivedFire = data; } };
            var firePayload = WeaponFireCodec.Encode(2, 0, 2);
            Assert(client.SendUnreliableToHost(MessageType.WeaponFire, firePayload), "client queues cosmetic weapon-fire event");
            for (var i = 0; i < 20 && receivedFire == null; i++)
            {
                host.Update(0.02f, hostPos, Quaternion.identity, Vector3.zero);
                client.Update(0.02f, clientPos, Quaternion.identity, Vector3.zero, 0, 2, 0, 73);
                Thread.Sleep(5);
            }
            Assert(firedBy == 2 && Equal(receivedFire, firePayload), "host receives cosmetic shot event with sender identity");
            uint relayedFrom = 0;
            byte[] relayedFire = null;
            client.MessageReceived += (sender, type, data) => { if (type == MessageType.WeaponFire) { relayedFrom = sender; relayedFire = data; } };
            host.BroadcastUnreliable(MessageType.WeaponFire, firePayload);
            for (var i = 0; i < 20 && relayedFire == null; i++)
            {
                host.Update(0.02f, hostPos, Quaternion.identity, Vector3.zero);
                client.Update(0.02f, clientPos, Quaternion.identity, Vector3.zero, 0, 2, 0, 73);
                Thread.Sleep(5);
            }
            Assert(relayedFrom == 1 && Equal(relayedFire, firePayload), "client receives cosmetic shot broadcast from host");
            uint receivedSender = 0;
            byte[] receivedPayload = null;
            host.MessageReceived += (sender, type, data) => { if (type == MessageType.DamageRequest) { receivedSender = sender; receivedPayload = data; } };
            var requestPayload = new byte[] { 9, 8, 7, 6 };
            Assert(client.SendToHost(MessageType.DamageRequest, requestPayload), "client queues reliable game event");
            for (var i = 0; i < 20 && receivedPayload == null; i++)
            {
                host.Update(0.02f, hostPos, Quaternion.identity, Vector3.zero);
                client.Update(0.02f, clientPos, Quaternion.identity, Vector3.zero);
                Thread.Sleep(5);
            }
            Assert(receivedSender == 2 && Equal(receivedPayload, requestPayload), "host receives reliable event with sender identity");

            uint worldMutationSender = 0;
            byte[] receivedWorldMutation = null;
            host.MessageReceived += (sender, type, data) =>
            {
                if (type == MessageType.WorldMutationRequest) { worldMutationSender = sender; receivedWorldMutation = data; }
            };
            var woodBreakRequest = WorldMutationCodec.Encode(new WorldMutation
            {
                Kind = WorldMutationKind.Breakable,
                Scene = "1-2 The Burning World",
                Path = "Level/Geometry/BreakableWood (2)",
                ComponentIndex = 1,
                Strength = 42.5f
            });
            Assert(client.SendToHost(MessageType.WorldMutationRequest, woodBreakRequest), "guest sends a reliable wood-break request to the local host");
            for (var i = 0; i < 20 && receivedWorldMutation == null; i++)
            {
                host.Update(0.02f, hostPos, Quaternion.identity, Vector3.zero);
                client.Update(0.02f, clientPos, Quaternion.identity, Vector3.zero);
                Thread.Sleep(5);
            }
            Assert(worldMutationSender == 2 && receivedWorldMutation != null &&
                WorldMutationCodec.Decode(receivedWorldMutation).Path == "Level/Geometry/BreakableWood (2)",
                "local UDP server delivers a stable-identity breakable mutation from guest to host");

            uint worldAudioSender = 0;
            byte[] receivedWorldAudio = null;
            host.MessageReceived += (sender, type, data) =>
            {
                if (type == MessageType.WorldAudio) { worldAudioSender = sender; receivedWorldAudio = data; }
            };
            var soundEvent = WorldAudioCodec.Encode(new WorldAudioEvent
            {
                OwnerPlayerId = 2,
                ClipName = "Enemy_Hurt_Heavy",
                Position = new Vector3(7f, 8f, 9f),
                Volume = 0.8f,
                Pitch = 1f,
                SpatialBlend = 1f
            });
            Assert(client.SendUnreliableToHost(MessageType.WorldAudio, soundEvent), "guest sends a spatial sound event to the local host");
            for (var i = 0; i < 20 && receivedWorldAudio == null; i++)
            {
                host.Update(0.02f, hostPos, Quaternion.identity, Vector3.zero);
                client.Update(0.02f, clientPos, Quaternion.identity, Vector3.zero);
                Thread.Sleep(5);
            }
            Assert(worldAudioSender == 2 && receivedWorldAudio != null &&
                WorldAudioCodec.Decode(receivedWorldAudio).ClipName == "Enemy_Hurt_Heavy",
                "local UDP server delivers a spatial game-sound event with sender identity");

            uint parrySender = 0;
            byte[] receivedParry = null;
            host.MessageReceived += (sender, type, data) => { if (type == MessageType.PlayerAnimation) { parrySender = sender; receivedParry = data; } };
            var parryPayload = PlayerAnimationCodec.Encode(PlayerAnimationAction.Parry);
            Assert(client.SendToHost(MessageType.PlayerAnimation, parryPayload), "client queues parry event through reliable transport");
            for (var i = 0; i < 20 && receivedParry == null; i++)
            {
                host.Update(0.02f, hostPos, Quaternion.identity, Vector3.zero);
                client.Update(0.02f, clientPos, Quaternion.identity, Vector3.zero);
                Thread.Sleep(5);
            }
            Assert(parrySender == 2 && Equal(receivedParry, parryPayload), "host receives reliable parry event with sender identity");
            uint relayedParrySender = 0;
            byte[] relayedParry = null;
            client.MessageReceived += (sender, type, data) => { if (type == MessageType.PlayerAnimation) { relayedParrySender = sender; relayedParry = data; } };
            host.BroadcastReliable(MessageType.PlayerAnimation, parryPayload);
            for (var i = 0; i < 20 && relayedParry == null; i++)
            {
                host.Update(0.02f, hostPos, Quaternion.identity, Vector3.zero);
                client.Update(0.02f, clientPos, Quaternion.identity, Vector3.zero);
                Thread.Sleep(5);
            }
            Assert(relayedParrySender == 1 && Equal(relayedParry, parryPayload), "client receives parry event through reliable host broadcast");
            PlayerLifecycleUpdate lastLifecycle = null;
            client.PlayerLifecycleChanged += update => lastLifecycle = update;
            Assert(host.SetAuthoritativePlayerVitals(2, 0, true), "host commits the guest death to authoritative session state");
            for (var i = 0; i < 40 && (lastLifecycle == null || !lastLifecycle.IsDead); i++)
            {
                host.Update(0.02f, hostPos, Quaternion.identity, Vector3.zero);
                client.Update(0.02f, clientPos, Quaternion.identity, Vector3.zero);
                Thread.Sleep(5);
            }
            Assert(lastLifecycle != null && lastLifecycle.PlayerId == 2 && lastLifecycle.IsDead && lastLifecycle.Health == 0, "host reliably relays player death state");
            Assert((client.Players[2].MovementFlags & 4) != 0, "client applies reliable death state to player roster");
            lastLifecycle = null;
            Assert(host.SetAuthoritativePlayerVitals(2, 100, false), "host commits the guest respawn to authoritative session state");
            for (var i = 0; i < 40 && (lastLifecycle == null || lastLifecycle.IsDead); i++)
            {
                host.Update(0.02f, hostPos, Quaternion.identity, Vector3.zero);
                client.Update(0.02f, clientPos, Quaternion.identity, Vector3.zero);
                Thread.Sleep(5);
            }
            Assert(lastLifecycle != null && !lastLifecycle.IsDead && lastLifecycle.Health == 100, "host reliably relays player respawn state");
            for (var i = 0; i < 70; i++)
            {
                host.Update(0.02f, hostPos, Quaternion.identity, Vector3.zero);
                client.Update(0.02f, clientPos, Quaternion.identity, Vector3.zero);
                Thread.Sleep(2);
            }
            Assert(host.TotalBytesSent > 0 && client.TotalBytesReceived > 0, "network traffic counters record transmitted and received frames");
            Assert(host.SnapshotSendRate > 0f && client.SnapshotReceiveRate > 0f, "diagnostics report measured snapshot rates");
            Assert(host.PacketSendRate > 0f && client.PacketReceiveRate > 0f, "diagnostics report measured packet rates");
            Assert(host.PacketLossPercent >= 0f && host.PacketLossPercent <= 100f, "snapshot loss estimate stays within valid range");
            Pump(host, new[] { client }, hostPos, new[] { clientPos }, 20);
            Assert((host.Players[2].Position - clientPos).sqrMagnitude < 0.001f, "host receives sequenced client snapshot");
            Assert((client.Players[1].Position - hostPos).sqrMagnitude < 0.001f, "client receives host snapshot");
            client.Stop();
            for (var i = 0; i < 20 && host.Players.Count > 1; i++)
            {
                host.Update(0.04f, hostPos, Quaternion.identity, Vector3.zero);
                Thread.Sleep(5);
            }
            Assert(host.Players.Count == 1, "host removes a disconnected client");
        }
    }

    private static void FourPlayerCapacityAndSnapshots()
    {
        int port;
        using (var probe = new UdpClient(0)) port = ((System.Net.IPEndPoint)probe.Client.LocalEndPoint).Port;
        using (var host = new NetworkService())
        {
            var clients = new List<NetworkService>();
            var positions = new List<Vector3>();
            try
            {
                host.StartHost(port, 4, "Host");
                for (var i = 0; i < 3; i++)
                {
                    var client = new NetworkService();
                    clients.Add(client);
                    positions.Add(new Vector3(i + 10, i + 20, i + 30));
                    client.StartClient("127.0.0.1", port, "Guest" + (i + 1));
                }
                for (var i = 0; i < 400; i++)
                {
                    host.Update(0.01f, new Vector3(1, 2, 3), Quaternion.identity, Vector3.zero);
                    for (var j = 0; j < clients.Count; j++)
                        clients[j].Update(0.01f, positions[j], Quaternion.identity, Vector3.zero);
                    Thread.Sleep(10);
                    if (host.Players.Count == 4 && clients.TrueForAll(c => c.IsConnected && c.Players.Count >= 4)) break;
                }
                Assert(host.Players.Count == 4, "host tracks four total players");
                for (var i = 0; i < 3; i++)
                {
                    Assert(clients[i].IsConnected, "client " + (i + 1) + " connects");
                    Assert(clients[i].LocalPlayerId == (uint)(i + 2), "client receives stable join-order id");
                    Assert(clients[i].Players.Count == 4, "client receives all four player snapshots");
                }
            }
            finally
            {
                foreach (var client in clients) client.Dispose();
            }
        }
    }

    private static void LossAndLatencyResilience()
    {
        int serverPort;
        using (var probe = new UdpClient(0)) serverPort = ((IPEndPoint)probe.Client.LocalEndPoint).Port;
        using (var host = new NetworkService())
        {
            host.StartHost(serverPort, 2, "ProxyHost");
            using (var proxy = new UdpFaultProxy(new IPEndPoint(IPAddress.Loopback, serverPort), 0.15, 8, 24))
            using (var client = new NetworkService())
            {
                client.StartClient("127.0.0.1", proxy.ListenPort, "ProxyGuest");
                var hostPosition = new Vector3(1f, 1f, 1f);
                var clientPosition = new Vector3(8f, 2f, 3f);
                for (var i = 0; i < 900; i++)
                {
                    host.Update(0.01f, hostPosition, Quaternion.identity, Vector3.zero);
                    client.Update(0.01f, clientPosition, Quaternion.identity, Vector3.zero);
                    Thread.Sleep(3);
                }
                Assert(client.IsConnected && host.Players.ContainsKey(2), "session survives 15 percent packet loss with simulated latency");
                Assert(host.PacketLossPercent > 0f || client.PacketLossPercent > 0f, "snapshot sequence diagnostics detect simulated loss");
                uint receivedSender = 0;
                host.MessageReceived += (sender, type, payload) => { if (type == MessageType.DamageRequest) receivedSender = sender; };
                Assert(client.SendToHost(MessageType.DamageRequest, new byte[] { 1, 3, 5, 7 }), "reliable event queues through impaired-network proxy");
                for (var i = 0; i < 1800 && receivedSender == 0; i++)
                {
                    host.Update(0.01f, hostPosition, Quaternion.identity, Vector3.zero);
                    client.Update(0.01f, clientPosition, Quaternion.identity, Vector3.zero);
                    Thread.Sleep(3);
                }
                Assert(receivedSender == 2, "reliable event is retransmitted through impaired-network proxy");
                uint parrySender = 0;
                host.MessageReceived += (sender, type, payload) =>
                {
                    if (sender == 2 && type == MessageType.PlayerAnimation &&
                        PlayerAnimationCodec.Decode(payload).Action == PlayerAnimationAction.Parry) parrySender = sender;
                };
                var parryPayload = PlayerAnimationCodec.Encode(PlayerAnimationAction.Parry);
                Assert(client.SendToHost(MessageType.PlayerAnimation, parryPayload), "parry queues reliably over impaired network");
                for (var i = 0; i < 1800 && parrySender == 0; i++)
                {
                    host.Update(0.01f, hostPosition, Quaternion.identity, Vector3.zero);
                    client.Update(0.01f, clientPosition, Quaternion.identity, Vector3.zero);
                    Thread.Sleep(3);
                }
                Assert(parrySender == 2, "parry survives packet loss and latency on the client-to-host route");
                byte[] receivedCoinRequest = null;
                host.MessageReceived += (sender, type, payload) =>
                {
                    if (sender == 2 && type == MessageType.CoinProjectile) receivedCoinRequest = payload;
                };
                var coinLaunch = new CoinProjectileEvent
                {
                    WeaponVariation = 0,
                    Position = clientPosition,
                    Rotation = Quaternion.identity,
                    Velocity = new Vector3(20f, 15f, 0f),
                    Gravity = new Vector3(0f, -9.81f, 0f),
                    AngularVelocity = new Vector3(0f, 8f, 0f)
                };
                var coinRequest = CoinProjectileCodec.EncodeLaunchRequest(coinLaunch);
                Assert(client.SendToHost(MessageType.CoinProjectile, coinRequest), "coin launch request queues reliably through impaired-network proxy");
                for (var i = 0; i < 1800 && receivedCoinRequest == null; i++)
                {
                    host.Update(0.01f, hostPosition, Quaternion.identity, Vector3.zero);
                    client.Update(0.01f, clientPosition, Quaternion.identity, Vector3.zero);
                    Thread.Sleep(3);
                }
                Assert(receivedCoinRequest != null && CoinProjectileCodec.DecodeLaunchRequest(receivedCoinRequest).Position == clientPosition,
                    "coin launch request survives packet loss and latency to the host");
                LevelProgressionEventData receivedProgression = null;
                host.MessageReceived += (sender, type, payload) =>
                {
                    if (sender == 2 && type == MessageType.LevelProgression)
                        receivedProgression = LevelProgressionEventCodec.Decode(payload);
                };
                var progressionPayload = LevelProgressionEventCodec.Encode(
                    LevelProgressionEventKind.ArenaActivationRequest, "1-2 The Burning World", "0:World/0:Encounter/0:Trigger", 0);
                Assert(client.SendToHost(MessageType.LevelProgression, progressionPayload), "arena request queues as a reliable progression event");
                for (var i = 0; i < 1800 && receivedProgression == null; i++)
                {
                    host.Update(0.01f, hostPosition, Quaternion.identity, Vector3.zero);
                    client.Update(0.01f, clientPosition, Quaternion.identity, Vector3.zero);
                    Thread.Sleep(3);
                }
                Assert(receivedProgression != null && receivedProgression.Kind == LevelProgressionEventKind.ArenaActivationRequest &&
                    receivedProgression.SceneName == "1-2 The Burning World" && receivedProgression.ObjectPath == "0:World/0:Encounter/0:Trigger",
                    "reliable progression request survives impaired-network retransmission");
                byte[] receivedEnemyState = null;
                client.MessageReceived += (sender, type, payload) =>
                {
                    if (sender == 1 && type == MessageType.EnemyDamageState) receivedEnemyState = payload;
                };
                var authoritativeState = EnemyDamageStateCodec.Encode(new EnemyDamageState
                {
                    NetworkId = 23, Frame = 91, StableKey = 0x12345678, EnemyType = 4,
                    Position = new Vector3(-4f, 12f, 3f), Rotation = Quaternion.identity, Health = 2.75f, Dead = false
                });
                host.BroadcastReliable(MessageType.EnemyDamageState, authoritativeState);
                for (var i = 0; i < 1800 && receivedEnemyState == null; i++)
                {
                    host.Update(0.01f, hostPosition, Quaternion.identity, Vector3.zero);
                    client.Update(0.01f, clientPosition, Quaternion.identity, Vector3.zero);
                    Thread.Sleep(3);
                }
                Assert(receivedEnemyState != null && Equal(receivedEnemyState, authoritativeState), "authoritative enemy damage state is retransmitted host-to-client through impaired-network proxy");
                var appliedState = EnemyDamageStateCodec.Decode(receivedEnemyState);
                Assert(appliedState.NetworkId == 23 && Math.Abs(appliedState.Health - 2.75f) < 0.001f, "loss-recovered enemy state preserves exact health");
                byte[] relayedParry = null;
                client.MessageReceived += (sender, type, payload) =>
                {
                    if (sender == 1 && type == MessageType.PlayerAnimation &&
                        PlayerAnimationCodec.Decode(payload).Action == PlayerAnimationAction.Parry) relayedParry = payload;
                };
                host.BroadcastReliable(MessageType.PlayerAnimation, parryPayload);
                for (var i = 0; i < 1800 && relayedParry == null; i++)
                {
                    host.Update(0.01f, hostPosition, Quaternion.identity, Vector3.zero);
                    client.Update(0.01f, clientPosition, Quaternion.identity, Vector3.zero);
                    Thread.Sleep(3);
                }
                Assert(relayedParry != null && Equal(relayedParry, parryPayload), "parry survives packet loss and latency on the host-to-client route");
                byte[] relayedCoin = null;
                client.MessageReceived += (sender, type, payload) =>
                {
                    if (sender == 1 && type == MessageType.CoinProjectile) relayedCoin = payload;
                };
                var coinRelay = CoinProjectileCodec.EncodeRelay(2, coinLaunch);
                host.BroadcastReliable(MessageType.CoinProjectile, coinRelay);
                for (var i = 0; i < 1800 && relayedCoin == null; i++)
                {
                    host.Update(0.01f, hostPosition, Quaternion.identity, Vector3.zero);
                    client.Update(0.01f, clientPosition, Quaternion.identity, Vector3.zero);
                    Thread.Sleep(3);
                }
                Assert(relayedCoin != null && CoinProjectileCodec.DecodeRelay(relayedCoin).OwnerPlayerId == 2,
                    "coin relay survives packet loss and latency to connected clients");
            }
        }
    }


    private static void IndependentInventoryNetworkSession()
    {
        int port;
        using (var probe = new UdpClient(0)) port = ((IPEndPoint)probe.Client.LocalEndPoint).Port;
        using (var host = new NetworkService())
        using (var guest = new NetworkService())
        {
            var hostInventory = WeaponInventoryPolicy.Add(0, 1, 0);
            var guestInventory = WeaponInventoryPolicy.Add(0, 5, 2);
            var hostPosition = new Vector3(1f, 1f, 1f);
            var guestPosition = new Vector3(5f, 2f, 3f);
            host.StartHost(port, 2, "InventoryHost");
            guest.StartClient("127.0.0.1", port, "InventoryGuest");
            for (var i = 0; i < 100; i++)
            {
                host.Update(0.02f, hostPosition, Quaternion.identity, Vector3.zero, 0, 1, 0, 100,
                    Quaternion.identity, "Revolver(Clone)", hostInventory);
                guest.Update(0.02f, guestPosition, Quaternion.identity, Vector3.zero, 0, 5, 2, 100,
                    Quaternion.identity, "RocketLauncher(Clone)", guestInventory);
                if (host.Players.ContainsKey(2) && host.Players[2].WeaponInventoryMask == guestInventory &&
                    guest.Players.ContainsKey(1) && guest.Players[1].WeaponInventoryMask == hostInventory) break;
                Thread.Sleep(5);
            }
            Assert(host.IsHost && guest.IsConnected, "independent inventory session connects");
            Assert(host.Players[2].WeaponInventoryMask == guestInventory && host.Players[2].WeaponName == "RocketLauncher(Clone)", "host receives guest loadout without replacing host inventory");
            Assert(guest.Players[1].WeaponInventoryMask == hostInventory && guest.Players[1].WeaponName == "Revolver(Clone)", "guest receives host inventory as a separate state");
            Assert(!WeaponInventoryPolicy.IsUnlocked(host.Players[1].WeaponInventoryMask, 5, 2) &&
                WeaponInventoryPolicy.IsUnlocked(host.Players[2].WeaponInventoryMask, 5, 2), "host and guest weapon unlocks remain independent over the network");
        }
    }

    private static void LanDiscoveryAndJoin()
    {
        int port;
        using (var probe = new UdpClient(0)) port = ((System.Net.IPEndPoint)probe.Client.LocalEndPoint).Port;
        using (var host = new NetworkService())
        using (var client = new NetworkService())
        {
            host.LobbyName = "Discovery Test";
            host.StartHost(port, 4, "Host");
            client.DiscoverLan(port);
            for (var i = 0; i < 200 && client.DiscoveredLobbies.Count == 0; i++)
            {
                host.Update(0.01f, Vector3.zero, Quaternion.identity, Vector3.zero);
                client.Update(0.01f, Vector3.zero, Quaternion.identity, Vector3.zero);
                Thread.Sleep(10);
            }
            Assert(client.DiscoveredLobbies.Count > 0, "LAN broadcast finds the host");
            Assert(client.DiscoveredLobbies[0].Name == "Discovery Test", "LAN lobby metadata is decoded");
            client.JoinDiscovered(client.DiscoveredLobbies[0], "FoundGuest");
            for (var i = 0; i < 200 && !client.IsConnected; i++)
            {
                host.Update(0.01f, Vector3.zero, Quaternion.identity, Vector3.zero);
                client.Update(0.01f, Vector3.zero, Quaternion.identity, Vector3.zero);
                Thread.Sleep(10);
            }
            Assert(client.IsConnected, "joining a discovered lobby connects");
        }
    }

    private static void PasswordRejectsWrongClient()
    {
        int port;
        using (var probe = new UdpClient(0)) port = ((System.Net.IPEndPoint)probe.Client.LocalEndPoint).Port;
        using (var host = new NetworkService())
        using (var wrong = new NetworkService())
        using (var valid = new NetworkService())
        {
            host.StartHost(port, 2, "Host", "secret", false);
            wrong.DiscoverLan(port);
            for (var i = 0; i < 50; i++)
            {
                host.Update(0.01f, Vector3.zero, Quaternion.identity, Vector3.zero);
                wrong.Update(0.01f, Vector3.zero, Quaternion.identity, Vector3.zero);
                Thread.Sleep(10);
            }
            Assert(wrong.DiscoveredLobbies.Count == 0, "private room is omitted from LAN discovery");
            wrong.StartClient("127.0.0.1", port, "Wrong", "incorrect");
            for (var i = 0; i < 100; i++)
            {
                host.Update(0.01f, Vector3.zero, Quaternion.identity, Vector3.zero);
                wrong.Update(0.01f, Vector3.zero, Quaternion.identity, Vector3.zero);
                Thread.Sleep(10);
            }
            Assert(!wrong.IsConnected, "incorrect room password is rejected");
            Assert(host.Players.Count == 1, "rejected client does not enter the room");
            valid.StartClient("127.0.0.1", port, "Valid", "secret");
            for (var i = 0; i < 200 && !valid.IsConnected; i++)
            {
                host.Update(0.01f, Vector3.zero, Quaternion.identity, Vector3.zero);
                wrong.Update(0.01f, Vector3.zero, Quaternion.identity, Vector3.zero);
                valid.Update(0.01f, Vector3.zero, Quaternion.identity, Vector3.zero);
                Thread.Sleep(10);
            }
            Assert(valid.IsConnected, "correct password joins a private room by direct IP");
        }
    }
    private static void Pump(NetworkService host, NetworkService[] clients, Vector3 hostPos, Vector3[] clientPos, int count)
    {
        for (var i = 0; i < count; i++)
        {
            host.Update(0.04f, hostPos, Quaternion.identity, Vector3.zero);
            for (var j = 0; j < clients.Length; j++) clients[j].Update(0.04f, clientPos[j], Quaternion.identity, Vector3.zero);
            Thread.Sleep(5);
        }
    }

    private sealed class UdpFaultProxy : IDisposable
    {
        private readonly UdpClient clientSocket;
        private readonly UdpClient hostSocket;
        private readonly IPEndPoint serverEndpoint;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly object endpointLock = new object();
        private readonly double dropChance;
        private readonly int minimumDelay;
        private readonly int maximumDelay;
        private IPEndPoint clientEndpoint;
        private readonly Task clientToHost;
        private readonly Task hostToClient;

        public int ListenPort { get { return ((IPEndPoint)clientSocket.Client.LocalEndPoint).Port; } }

        public UdpFaultProxy(IPEndPoint serverEndpoint, double dropChance, int minimumDelay, int maximumDelay)
        {
            this.serverEndpoint = serverEndpoint;
            this.dropChance = dropChance;
            this.minimumDelay = minimumDelay;
            this.maximumDelay = maximumDelay;
            clientSocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            hostSocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            clientToHost = Task.Run(ForwardClientToHost);
            hostToClient = Task.Run(ForwardHostToClient);
        }

        private async Task ForwardClientToHost()
        {
            var random = new System.Random(7183);
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    var packet = await clientSocket.ReceiveAsync(cancellation.Token).ConfigureAwait(false);
                    lock (endpointLock) clientEndpoint = packet.RemoteEndPoint;
                    if (random.NextDouble() < dropChance) continue;
                    await Task.Delay(random.Next(minimumDelay, maximumDelay + 1), cancellation.Token).ConfigureAwait(false);
                    await hostSocket.SendAsync(packet.Buffer, serverEndpoint).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (SocketException) when (cancellation.IsCancellationRequested) { }
        }

        private async Task ForwardHostToClient()
        {
            var random = new System.Random(9041);
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    var packet = await hostSocket.ReceiveAsync(cancellation.Token).ConfigureAwait(false);
                    if (random.NextDouble() < dropChance) continue;
                    await Task.Delay(random.Next(minimumDelay, maximumDelay + 1), cancellation.Token).ConfigureAwait(false);
                    IPEndPoint endpoint;
                    lock (endpointLock) endpoint = clientEndpoint;
                    if (endpoint != null) await clientSocket.SendAsync(packet.Buffer, endpoint).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (SocketException) when (cancellation.IsCancellationRequested) { }
        }

        public void Dispose()
        {
            cancellation.Cancel();
            clientSocket.Dispose();
            hostSocket.Dispose();
            try { Task.WhenAll(clientToHost, hostToClient).Wait(1000); }
            catch { }
            cancellation.Dispose();
        }
    }

    private static void ExpectInvalid(byte[] data, string label)
    {
        try { NetworkMessage.Decode(data); }
        catch (System.IO.InvalidDataException) { checks++; return; }
        throw new Exception("Expected invalid data rejection: " + label);
    }

    private static bool Equal(byte[] left, byte[] right)
    {
        if (left.Length != right.Length) return false;
        for (var i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
        return true;
    }

    private static void Assert(bool condition, string label)
    {
        if (!condition) throw new Exception("Assertion failed: " + label);
        checks++;
    }
}




