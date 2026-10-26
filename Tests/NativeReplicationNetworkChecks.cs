using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using ULTRAKILL.Bloodlink.Combat;
using ULTRAKILL.Bloodlink.Networking;
using UnityEngine;

internal static partial class Program
{
    private static void NativeReplicationNetworkSession()
    {
        int port;
        using (var probe = new UdpClient(0)) port = ((IPEndPoint)probe.Client.LocalEndPoint).Port;
        using (var host = new NetworkService())
        using (var first = new NetworkService())
        using (var second = new NetworkService())
        {
            host.StartHost(port, 4, "Host", bindAddress: "127.0.0.1");
            first.StartClient("127.0.0.1", port, "First");
            Action pump = () =>
            {
                host.Update(0.01f, Vector3.zero, Quaternion.identity, Vector3.zero);
                first.Update(0.01f, Vector3.one, Quaternion.identity, Vector3.zero);
                if (second.IsRunning) second.Update(0.01f, Vector3.one * 2, Quaternion.identity, Vector3.zero);
                Thread.Sleep(5);
            };
            Action<Func<bool>> until = predicate => { for (var i = 0; i < 600 && !predicate(); i++) pump(); Assert(predicate(), "native replication session reaches its expected transport state"); };
            until(() => first.IsConnected && host.Players.ContainsKey(2));
            second.StartClient("127.0.0.1", port, "Second");
            until(() => second.IsConnected && host.Players.ContainsKey(3));

            var receipts = new RequestReceiptJournal();
            var applied = 0;
            var firstRevisions = new HashSet<uint>();
            var secondRevisions = new HashSet<uint>();
            ItemState firstItem = null, secondItem = null;
            EnemyDamageState firstHit = null, secondHit = null;
            SceneObjectState firstObject = null, secondObject = null;
            host.MessageReceived += (sender, type, payload) =>
            {
                if (type == MessageType.WorldMutationRequest)
                {
                    var request = WorldMutationCodec.Decode(payload); byte[] response;
                    if (!receipts.TryGet(sender, request.RequestId, out response))
                    {
                        applied++; request.Revision = (uint)applied; request.OwnerId = sender; request.Broken = true;
                        response = WorldMutationCodec.Encode(request); receipts.Record(sender, request.RequestId, response);
                        host.BroadcastReliable(MessageType.WorldMutation, response);
                    }
                    else host.SendToPlayer(sender, MessageType.WorldMutation, response);
                }
                else if (type == MessageType.SceneObjectRequest)
                {
                    var state = SceneObjectStateCodec.Decode(payload);
                    state.OwnerId = sender; state.Revision = state.Sequence;
                    host.BroadcastReliable(MessageType.SceneObjectState, SceneObjectStateCodec.Encode(state));
                }
                else if (type == MessageType.ItemRequest)
                {
                    var item = ItemStateCodec.Decode(payload); item.ActorId = sender; item.OwnerId = item.Action == ItemAction.PickUp ? sender : 0;
                    item.Revision++;
                    host.BroadcastReliable(MessageType.ItemState, ItemStateCodec.Encode(item));
                }
                else if (type == MessageType.DamageRequest)
                {
                    var native = DamageRequestCodec.Decode(payload);
                    host.BroadcastReliable(MessageType.EnemyDamageState, EnemyDamageStateCodec.Encode(new EnemyDamageState {
                        NetworkId = 7, StableKey = native.StableKey, EnemyType = native.EnemyType, Frame = native.RequestSequence,
                        Rotation = Quaternion.identity, HealthBefore = 100, Health = 80, ImpactPayload = payload }));
                }
            };
            first.MessageReceived += (sender, type, payload) =>
            {
                if (sender != 1) return;
                if (type == MessageType.WorldMutation) firstRevisions.Add(WorldMutationCodec.Decode(payload).Revision);
                if (type == MessageType.ItemState) firstItem = ItemStateCodec.Decode(payload);
                if (type == MessageType.EnemyDamageState) firstHit = EnemyDamageStateCodec.Decode(payload);
                if (type == MessageType.SceneObjectState) firstObject = SceneObjectStateCodec.Decode(payload);
            };
            second.MessageReceived += (sender, type, payload) =>
            {
                if (sender != 1) return;
                if (type == MessageType.WorldMutation) secondRevisions.Add(WorldMutationCodec.Decode(payload).Revision);
                if (type == MessageType.ItemState) secondItem = ItemStateCodec.Decode(payload);
                if (type == MessageType.EnemyDamageState) secondHit = EnemyDamageStateCodec.Decode(payload);
                if (type == MessageType.SceneObjectState) secondObject = SceneObjectStateCodec.Decode(payload);
            };
            var wood = new WorldMutation { Kind = WorldMutationKind.Breakable, Scene = "Level 1-1", Path = "0:World/0:Wood", RequestId = 1, Strength = 99999 };
            var packet = WorldMutationCodec.Encode(wood);
            Assert(first.SendToHost(MessageType.WorldMutationRequest, packet), "first client sends a native wood break request");
            Assert(second.SendToHost(MessageType.WorldMutationRequest, packet), "second client has an independent request sequence");
            until(() => firstRevisions.Count == 2 && secondRevisions.Count == 2);
            Assert(applied == 2, "both authenticated clients can commit world changes visible to every peer");
            first.SendToHost(MessageType.WorldMutationRequest, packet);
            for (var i = 0; i < 35; i++) pump();
            Assert(applied == 2, "retrying the same client request replays its receipt without applying world damage twice");

            var glass = new WorldMutation { Kind = WorldMutationKind.Glass,
                Scene = "b3e7f2f8052488a45b35549efb98d902", Path = "0:World/0:Glass", RequestId = 2 };
            second.SendToHost(MessageType.WorldMutationRequest, WorldMutationCodec.Encode(glass));
            until(() => firstRevisions.Contains(3) && secondRevisions.Contains(3));
            Assert(applied == 3, "a glass shatter from the second client is committed once and delivered to both clients");
            var objectState = new SceneObjectState { Sequence = 1, Scene = glass.Scene, Path = "0:World/0:Prop",
                Operation = SceneObjectOperation.SetActive, Rotation = Quaternion.identity, Active = false };
            first.SendToHost(MessageType.SceneObjectRequest, SceneObjectStateCodec.Encode(objectState));
            until(() => firstObject != null && secondObject != null);
            Assert(!firstObject.Active && !secondObject.Active && firstObject.OwnerId == 2 && secondObject.Revision == 1,
                "an environmental object toggled by one client reaches every peer with the same host revision");
            objectState.Sequence = 2; objectState.Operation = SceneObjectOperation.Destroy;
            second.SendToHost(MessageType.SceneObjectRequest, SceneObjectStateCodec.Encode(objectState));
            until(() => firstObject.Operation == SceneObjectOperation.Destroy && secondObject.Operation == SceneObjectOperation.Destroy);
            Assert(firstObject.OwnerId == 3 && secondObject.OwnerId == 3,
                "environmental object destruction is delivered from either client to the entire session");

            var item = new ItemState { Sequence = 1, Scene = "Level 1-1", Path = "0:World/0:Skull", Action = ItemAction.PickUp };
            first.SendToHost(MessageType.ItemRequest, ItemStateCodec.Encode(item));
            until(() => firstItem != null && secondItem != null && firstItem.OwnerId == 2 && secondItem.OwnerId == 2);
            item.Sequence = 2; item.Action = ItemAction.PutDown; item.ParentPath = "0:World/0:Altar";
            first.SendToHost(MessageType.ItemRequest, ItemStateCodec.Encode(item));
            until(() => firstItem.Action == ItemAction.PutDown && secondItem.Action == ItemAction.PutDown);
            Assert(firstItem.ParentPath == secondItem.ParentPath && secondItem.ActorId == 2 && secondItem.OwnerId == 0,
                "placing the skull distributes the same altar and acting client to both players");

            var hit = new DamageRequestData { RequestSequence = 1, StableKey = 77, EnemyType = 2, WeaponName = "SupportItem", Hitter = "support",
                ShotDirection = Vector3.forward, Multiplier = 20, IgnoreDamageMultiplier = true };
            first.SendToHost(MessageType.DamageRequest, DamageRequestCodec.Encode(hit));
            until(() => firstHit != null && secondHit != null);
            Assert(DamageRequestCodec.Decode(secondHit.ImpactPayload).WeaponName == "SupportItem" && firstHit.Health == 80 && secondHit.Health == 80,
                "a client's support item hit reaches host authority and the same native impact and health outcome reach every client");
        }
    }
}
