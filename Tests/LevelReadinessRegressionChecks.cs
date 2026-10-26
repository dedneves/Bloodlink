using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using ULTRAKILL.Bloodlink.Levels;
using ULTRAKILL.Bloodlink.Networking;
using UnityEngine;

internal static partial class Program
{
    private static void ReadinessSceneIdentityChecks()
    {
        Assert(LevelSceneReadinessPolicy.IsFullyLoaded("Main Menu", "Main Menu", null, true, true),
            "Addressables menu key is ready even when the loaded Unity scene uses an asset GUID");
        Assert(LevelSceneReadinessPolicy.IsFullyLoaded("Level 0-1", "Level 0-1", null, true, true),
            "completed Addressables campaign scene releases READY without comparing Unity's internal scene name");
        Assert(!LevelSceneReadinessPolicy.IsFullyLoaded("Level 0-1", "Level 0-1", "Level 0-1", true, true),
            "same-map reload remains blocked until the pending Addressables operation finishes");
        Assert(!LevelSceneReadinessPolicy.IsFullyLoaded("Level 0-1", "Main Menu", null, true, true),
            "a loaded previous scene cannot release the new level barrier");
        Assert(!LevelSceneReadinessPolicy.IsFullyLoaded("Level 0-1", "Level 0-1", null, false, true),
            "an invalid Unity scene cannot release READY");
        Assert(!LevelSceneReadinessPolicy.IsFullyLoaded("Level 0-1", "Level 0-1", null, true, false),
            "a scene that has not activated cannot release READY");
        Assert(!LevelSceneReadinessPolicy.IsFullyLoaded(null, null, null, true, true),
            "an absent target never releases READY");
    }

    private static void ReadinessTransportChecks()
    {
        int port;
        using (var probe = new UdpClient(0)) port = ((IPEndPoint)probe.Client.LocalEndPoint).Port;
        using (var host = new NetworkService())
        using (var client = new NetworkService())
        {
            Action<Func<bool>> until = condition =>
            {
                for (var i = 0; i < 400 && !condition(); i++)
                {
                    host.Update(0.01f, Vector3.zero, Quaternion.identity, Vector3.zero);
                    client.Update(0.01f, Vector3.zero, Quaternion.identity, Vector3.zero);
                    Thread.Sleep(5);
                }
                Assert(condition(), "readiness network operation completes within its deadline");
            };
            host.StartHost(port, 2, "ReadyHost", bindAddress: "127.0.0.1");
            client.StartClient("127.0.0.1", port, "ReadyClient");
            until(() => client.IsConnected && host.Players.Count == 2);
            host.SetSceneSnapshotSyncReady(false);
            client.SetSceneSnapshotSyncReady(false);
            var ready = false;
            var start = false;
            var beacon = false;
            host.MessageReceived += (sender, type, payload) =>
            {
                if (sender == client.LocalPlayerId && type == MessageType.LevelTransition)
                {
                    var transition = LevelTransitionCodec.Decode(payload);
                    ready = transition.Stage == LevelTransitionStage.Ready && transition.TransitionId == 2 && transition.SceneName == "Level 0-1";
                }
            };
            client.MessageReceived += (sender, type, payload) =>
            {
                if (sender != 1) return;
                if (type == MessageType.LevelTransition)
                    start = LevelTransitionCodec.Decode(payload).Stage == LevelTransitionStage.Start;
                if (type == MessageType.JoinSessionState)
                    beacon = JoinSessionStateCodec.DecodeState(payload).SceneName == "Level 0-1";
            };
            Assert(client.SendToHost(MessageType.LevelTransition,
                LevelTransitionCodec.Encode(2, LevelTransitionStage.Ready, "Level 0-1", false)),
                "client sends READY while gameplay snapshots are paused");
            until(() => ready);
            host.SendToPlayer(client.LocalPlayerId, MessageType.LevelTransition,
                LevelTransitionCodec.Encode(2, LevelTransitionStage.Start, "Level 0-1", false,
                    2, 2, new[] { 1u, client.LocalPlayerId }));
            until(() => start);
            Assert(!host.SceneSnapshotSyncReady && !client.SceneSnapshotSyncReady,
                "READY and START reach their recipients independently of scene snapshot suspension");
            host.BroadcastReliable(MessageType.JoinSessionState, JoinSessionStateCodec.Encode("Level 0-1", 0));
            until(() => beacon);
            Assert(beacon, "the authoritative running-scene beacon reaches a waiting client for START recovery");
        }
    }
}
