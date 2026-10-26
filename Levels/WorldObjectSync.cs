using System;
using System.Collections.Generic;
using ULTRAKILL.Bloodlink.Networking;
using UnityEngine;

namespace ULTRAKILL.Bloodlink.Levels
{
    internal sealed class WorldObjectSync
    {
        private sealed class Pending { public WorldMutation Mutation; public float RetryAt; public float ExpiresAt; }
        private readonly Dictionary<uint, Pending> requests = new Dictionary<uint, Pending>();
        private readonly Dictionary<string, Pending> deferred = new Dictionary<string, Pending>();
        private readonly Dictionary<string, WorldMutation> outcomes = new Dictionary<string, WorldMutation>();
        private readonly RequestReceiptJournal receipts = new RequestReceiptJournal();
        private readonly Dictionary<string, uint> revisions = new Dictionary<string, uint>();
        private readonly HashSet<string> predictedDestructions = new HashSet<string>();
        private uint nextRequest, nextRevision;
        private static int applyDepth;
        private static int nativeDepth;
        public static bool IsApplyingRemote => applyDepth > 0;
        internal static bool IsRunningNativeMutation => nativeDepth > 0;
        internal static void BeginNativeMutation() { nativeDepth++; }
        internal static void EndNativeMutation() { nativeDepth = Math.Max(0, nativeDepth - 1); }
        public string Diagnostics => "world: states=" + outcomes.Count + ", requests=" + requests.Count + ", deferred=" + deferred.Count;

        public void Reset()
        {
            requests.Clear(); deferred.Clear(); outcomes.Clear(); receipts.Clear(); revisions.Clear(); predictedDestructions.Clear();
            nextRequest = nextRevision = 0;
        }

        public WorldMutation CaptureBreakable(Breakable target, float strength) => Capture(WorldMutationKind.Breakable, target, strength);
        public WorldMutation CaptureGlass(Glass target) => Capture(WorldMutationKind.Glass, target, 0f);
        private static WorldMutation Capture<T>(WorldMutationKind kind, T target, float strength) where T : Component
        {
            string scene, path; byte index;
            if (target == null || !SceneObjectIdentity.TryBuild(target, out scene, out path, out index)) return null;
            return new WorldMutation { Kind = kind, Scene = scene, Path = path, ComponentIndex = index,
                Position = target.transform.position, Strength = Mathf.Clamp(strength, 0f, 100000f), Target = target };
        }

        // Called after native gameplay on either peer. Clients predict the native
        // interaction, then the host commits and distributes the authoritative result.
        public void CommitHostMutation(WorldMutation mutation, NetworkService network)
        {
            if (mutation == null || network == null || !network.IsConnected || IsApplyingRemote) return;
            if (network.IsHost) Commit(mutation, network);
            else
            {
                unchecked { if (++nextRequest == 0) nextRequest = 1; }
                mutation.RequestId = nextRequest;
                mutation.OwnerId = network.LocalPlayerId;
                var localBreakable = mutation.Target as Breakable;
                if (mutation.Kind == WorldMutationKind.Glass || mutation.Target == null || (localBreakable != null && localBreakable.broken))
                    predictedDestructions.Add(Key(mutation));
                requests[mutation.RequestId] = new Pending { Mutation = mutation, RetryAt = Time.unscaledTime + 1f, ExpiresAt = Time.unscaledTime + 15f };
                network.SendToHost(MessageType.WorldMutationRequest, WorldMutationCodec.Encode(mutation));
            }
        }

        public void HandleRequest(uint senderId, byte[] payload, NetworkService network)
        {
            if (network == null || !network.IsHost || !network.Players.ContainsKey(senderId)) return;
            var mutation = WorldMutationCodec.Decode(payload);
            if (mutation.RequestId == 0 || !SceneObjectIdentity.IsCurrentScene(mutation.Scene)) return;
            byte[] previous;
            if (receipts.TryGet(senderId, mutation.RequestId, out previous))
            { network.SendToPlayer(senderId, MessageType.WorldMutation, previous); return; }
            mutation.OwnerId = senderId;
            Component target = mutation.Kind == WorldMutationKind.Breakable
                ? (Component)SceneObjectIdentity.ResolveNear<Breakable>(mutation.Scene, mutation.Path, mutation.ComponentIndex, mutation.Position)
                : SceneObjectIdentity.ResolveNear<Glass>(mutation.Scene, mutation.Path, mutation.ComponentIndex, mutation.Position);
            if (target == null)
            {
                WorldMutation known;
                if (outcomes.TryGetValue(Key(mutation), out known))
                {
                    mutation.Broken = known.Broken; mutation.Durability = known.Durability; mutation.Revision = known.Revision;
                    var response = WorldMutationCodec.Encode(mutation); receipts.Record(senderId, mutation.RequestId, response);
                    network.SendToPlayer(senderId, MessageType.WorldMutation, response);
                }
                return;
            }
            var player = network.Players[senderId];
            if (player == null || (player.MovementFlags & 4) != 0 || (player.Position - target.transform.position).sqrMagnitude > 600f * 600f) return;
            mutation.Target = target;
            applyDepth++;
            try
            {
                if (target is Breakable) ((Breakable)target).Break(mutation.Strength);
                else ((Glass)target).Shatter();
            }
            finally { applyDepth--; }
            var committed = Commit(mutation, network);
            receipts.Record(senderId, mutation.RequestId, committed);
        }

        private byte[] Commit(WorldMutation mutation, NetworkService network)
        {
            var target = mutation.Target as Breakable;
            mutation.Broken = mutation.Kind == WorldMutationKind.Glass || mutation.Target == null || (target != null && target.broken);
            mutation.Durability = target != null ? target.durability : 0f;
            unchecked { if (++nextRevision == 0) nextRevision = 1; }
            mutation.Revision = nextRevision;
            outcomes[Key(mutation)] = mutation;
            var payload = WorldMutationCodec.Encode(mutation);
            network.BroadcastReliable(MessageType.WorldMutation, payload);
            return payload;
        }

        public void SendCurrentToPlayer(NetworkService network, uint playerId)
        {
            if (network == null || !network.IsHost) return;
            foreach (var mutation in outcomes.Values) network.SendToPlayer(playerId, MessageType.WorldMutation, WorldMutationCodec.Encode(mutation));
        }

        public void Apply(byte[] payload, NetworkService network)
        {
            if (network == null || network.IsHost) return;
            var mutation = WorldMutationCodec.Decode(payload);
            if (!SceneObjectIdentity.IsCurrentScene(mutation.Scene)) return;
            if (mutation.OwnerId == network.LocalPlayerId) requests.Remove(mutation.RequestId);
            var key = Key(mutation); uint previous;
            if (revisions.TryGetValue(key, out previous) && unchecked((int)(mutation.Revision - previous)) <= 0) return;
            Pending waiting;
            if (deferred.TryGetValue(key, out waiting) && unchecked((int)(mutation.Revision - waiting.Mutation.Revision)) <= 0) return;
            if (ApplyOutcome(mutation)) { revisions[key] = mutation.Revision; deferred.Remove(key); }
            else deferred[key] = new Pending { Mutation = mutation, RetryAt = Time.unscaledTime + 0.2f, ExpiresAt = Time.unscaledTime + 30f };
        }

        private bool ApplyOutcome(WorldMutation mutation)
        {
            applyDepth++;
            try
            {
                if (mutation.Kind == WorldMutationKind.Breakable)
                {
                    var target = SceneObjectIdentity.ResolveNear<Breakable>(mutation.Scene, mutation.Path, mutation.ComponentIndex, mutation.Position);
                    if (target == null) return mutation.Broken && predictedDestructions.Contains(Key(mutation));
                    if (mutation.Broken && !target.broken) target.ForceBreak();
                    else if (!target.broken) target.durability = mutation.Durability;
                }
                else
                {
                    var target = SceneObjectIdentity.ResolveNear<Glass>(mutation.Scene, mutation.Path, mutation.ComponentIndex, mutation.Position);
                    if (predictedDestructions.Contains(Key(mutation))) return true;
                    if (target == null) return false;
                    target.Shatter();
                }
                return true;
            }
            finally { applyDepth--; }
        }

        public void Update(NetworkService network, float deltaTime)
        {
            if (network == null || !network.IsConnected || network.IsHost) return;
            var now = Time.unscaledTime;
            foreach (var id in new List<uint>(requests.Keys))
            {
                var pending = requests[id];
                if (now >= pending.ExpiresAt) { requests.Remove(id); continue; }
                if (now < pending.RetryAt) continue;
                pending.RetryAt = now + 1f;
                network.SendToHost(MessageType.WorldMutationRequest, WorldMutationCodec.Encode(pending.Mutation));
            }
            foreach (var key in new List<string>(deferred.Keys))
            {
                var pending = deferred[key];
                if (now >= pending.ExpiresAt || !SceneObjectIdentity.IsCurrentScene(pending.Mutation.Scene)) { deferred.Remove(key); continue; }
                if (now < pending.RetryAt) continue;
                pending.RetryAt = now + 0.2f;
                if (ApplyOutcome(pending.Mutation)) { revisions[key] = pending.Mutation.Revision; deferred.Remove(key); }
            }
        }
        private static string Key(WorldMutation mutation) => mutation.Scene + "|" + (byte)mutation.Kind + "|" + mutation.Path + "|" + mutation.ComponentIndex;
    }
}
