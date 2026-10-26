using System;
using System.Collections.Generic;
using HarmonyLib;
using ULTRAKILL.Bloodlink.Core;
using ULTRAKILL.Bloodlink.Networking;
using UnityEngine;

namespace ULTRAKILL.Bloodlink.Levels
{
    internal sealed class ItemSync
    {
        private readonly Dictionary<string, ItemState> states = new Dictionary<string, ItemState>();
        private readonly Dictionary<string, string> copySources = new Dictionary<string, string>();
        private readonly Dictionary<string, ItemState> pending = new Dictionary<string, ItemState>();
        private readonly Dictionary<string, uint> applied = new Dictionary<string, uint>();
        private readonly Dictionary<string, string> appliedEvents = new Dictionary<string, string>();
        private readonly ReliableEventSequenceTracker requests = new ReliableEventSequenceTracker();
        private uint sequence, revision, copies;
        private float nextPoseAt, nextPendingAt;
        private static int applyDepth;
        private static int scenarioDepth;
        internal static bool IsApplyingRemote => applyDepth > 0;
        internal static bool IsApplyingScenario => applyDepth > 0 || scenarioDepth > 0;
        internal static void BeginScenario() { scenarioDepth++; }
        internal static Exception EndScenario(Exception error) { scenarioDepth = Math.Max(0, scenarioDepth - 1); return error; }
        public string Diagnostics => "items: tracked=" + states.Count + ", deferred=" + pending.Count;
        private static string Key(ItemState state) => state.Scene + "|" + state.Path + "|" + state.ComponentIndex;
        public void Reset() { states.Clear(); copySources.Clear(); pending.Clear(); applied.Clear(); appliedEvents.Clear(); requests.Clear(); sequence = revision = copies = 0; }

        public void RegisterCopy(ItemIdentifier original, ItemIdentifier copy, NetworkService network)
        {
            if (IsApplyingRemote || original == null || copy == null || network == null || !network.IsConnected) return;
            string scene, path; byte index;
            if (!SceneObjectIdentity.TryBuild(original, out scene, out path, out index)) return;
            var copyPath = path + "/bloodlink-copy:" + network.LocalPlayerId + ":" + ++copies;
            SceneObjectIdentity.AssignPath(copy.transform, scene, copyPath);
            copySources[scene + "|" + copyPath + "|0"] = path;
        }

        public void Notify(ItemIdentifier item, ItemAction action, NetworkService network)
        {
            if (IsApplyingRemote || SceneObjectStateSync.IsApplyingRemote || item == null || network == null || !network.IsConnected) return;
            string scene, path; byte index;
            if (!SceneObjectIdentity.TryBuild(item, out scene, out path, out index)) return;
            string parent = "";
            if (action == ItemAction.PutDown && item.transform.parent != null) SceneObjectIdentity.TryBuildPath(item.transform.parent, out parent);
            var body = item.GetComponent<Rigidbody>();
            var state = new ItemState { Sequence = ++sequence, ActorId = network.LocalPlayerId, OwnerId = action == ItemAction.PickUp ? network.LocalPlayerId : 0,
                Scene = scene, Path = path, ComponentIndex = index, Action = action, ParentPath = parent,
                Position = item.transform.position, Rotation = item.transform.rotation, Velocity = body != null ? body.velocity : Vector3.zero };
            string source; if (copySources.TryGetValue(Key(state), out source)) state.SourcePath = source;
            if (network.IsHost) Commit(state, network, false);
            else network.SendToHost(MessageType.ItemRequest, ItemStateCodec.Encode(state));
        }

        public void ReceiveRequest(uint sender, byte[] payload, NetworkService network)
        {
            if (!network.IsHost || !network.Players.ContainsKey(sender)) return;
            var state = ItemStateCodec.Decode(payload);
            if (!SceneObjectIdentity.IsCurrentScene(state.Scene) || state.Action == ItemAction.Pose) return;
            if (!requests.TryAccept(sender, state.Sequence))
            { ItemState current; if (states.TryGetValue(Key(state), out current)) network.SendToPlayer(sender, MessageType.ItemState, ItemStateCodec.Encode(current)); return; }
            ItemState previous;
            if (states.TryGetValue(Key(state), out previous) && previous.OwnerId != 0 && previous.OwnerId != sender)
            { network.SendToPlayer(sender, MessageType.ItemState, ItemStateCodec.Encode(previous)); return; }
            state.OwnerId = state.Action == ItemAction.PickUp ? sender : 0;
            state.ActorId = sender;
            var item = Resolve(state);
            if (item == null) return;
            if (state.Action == ItemAction.PickUp && (network.Players[sender].Position - item.transform.position).sqrMagnitude > 30f * 30f && string.IsNullOrEmpty(state.SourcePath)) return;
            Commit(state, network, true);
        }

        private void Commit(ItemState state, NetworkService network, bool apply)
        {
            state.Revision = ++revision;
            states[Key(state)] = state;
            if (apply) ApplyState(state, network);
            network.BroadcastReliable(MessageType.ItemState, ItemStateCodec.Encode(state));
        }

        public void Receive(byte[] payload, NetworkService network)
        {
            var state = ItemStateCodec.Decode(payload);
            if (!SceneObjectIdentity.IsCurrentScene(state.Scene)) return;
            var key = Key(state); uint previous;
            if (applied.TryGetValue(key, out previous) && unchecked((int)(state.Revision - previous)) <= 0) return;
            states[key] = state;
            if (ApplyState(state, network)) { applied[key] = state.Revision; pending.Remove(key); }
            else pending[key] = state;
        }

        private ItemIdentifier Resolve(ItemState state)
        {
            var item = SceneObjectIdentity.Resolve<ItemIdentifier>(state.Scene, state.Path, state.ComponentIndex);
            if (item == null && !string.IsNullOrEmpty(state.SourcePath))
            {
                var source = SceneObjectIdentity.Resolve<ItemIdentifier>(state.Scene, state.SourcePath, 0);
                if (source != null)
                {
                    applyDepth++;
                    try { item = source.CreateCopy(); SceneObjectIdentity.AssignPath(item.transform, state.Scene, state.Path); }
                    finally { applyDepth--; }
                    copySources[Key(state)] = state.SourcePath;
                }
            }
            return item;
        }

        private bool ApplyState(ItemState state, NetworkService network)
        {
            var item = Resolve(state); if (item == null) return false;
            var eventToken = state.ActorId + ":" + state.Sequence + ":" + (byte)state.Action;
            string previousToken;
            var invokeEvents = state.Action != ItemAction.Pose && (!appliedEvents.TryGetValue(Key(state), out previousToken) || previousToken != eventToken);
            var heldLocally = MonoSingleton<FistControl>.Instance != null && MonoSingleton<FistControl>.Instance.heldObject == item;
            // Native hand attachment is predicted, while puzzle events wait for the
            // host. Acknowledgement runs those events without detaching the local item.
            if (state.Action == ItemAction.PickUp && state.OwnerId == network.LocalPlayerId && heldLocally)
            {
                if (invokeEvents)
                {
                    applyDepth++;
                    try { item.SendMessage("PickUp", SendMessageOptions.DontRequireReceiver); if (item != null && item.ipz != null) item.ipz.CheckItem(); }
                    finally { applyDepth--; }
                    appliedEvents[Key(state)] = eventToken;
                }
                return true;
            }
            if (heldLocally && state.OwnerId != network.LocalPlayerId)
            {
                var punch = MonoSingleton<FistControl>.Instance.currentPunch;
                punch.ResetHeldState(); punch.heldItem = null; MonoSingleton<FistControl>.Instance.heldObject = null;
            }
            applyDepth++;
            try
            {
                var oldZones = item.GetComponentsInParent<ItemPlaceZone>(true);
                Transform parent = null;
                if (state.OwnerId == 0 && !string.IsNullOrEmpty(state.ParentPath)) parent = SceneObjectIdentity.Resolve<Transform>(state.Scene, state.ParentPath, 0);
                item.transform.SetParent(parent, true);
                item.transform.SetPositionAndRotation(state.Position, state.Rotation);
                item.pickedUp = state.OwnerId != 0;
                if (item.pickedUp) item.beenPickedUp = true;
                foreach (var collider in item.GetComponentsInChildren<Collider>(true)) collider.enabled = !item.pickedUp;
                foreach (var body in item.GetComponentsInChildren<Rigidbody>(true))
                { body.isKinematic = item.pickedUp || !network.IsHost; if (!body.isKinematic) body.velocity = state.Velocity; }
                if (invokeEvents)
                {
                    item.SendMessage(state.Action == ItemAction.PickUp ? "PickUp" : "PutDown", SendMessageOptions.DontRequireReceiver);
                    foreach (var zone in oldZones) if (zone != null) zone.CheckItem();
                    if (item != null) foreach (var zone in item.GetComponentsInParent<ItemPlaceZone>(true)) if (zone != null) zone.CheckItem();
                    appliedEvents[Key(state)] = eventToken;
                }
                return true;
            }
            finally { applyDepth--; }
        }

        public void Update(NetworkService network)
        {
            if (!network.IsConnected) return;
            if (Time.unscaledTime >= nextPendingAt)
            {
                nextPendingAt = Time.unscaledTime + 0.25f;
                foreach (var key in new List<string>(pending.Keys))
                    if (ApplyState(pending[key], network)) { applied[key] = pending[key].Revision; pending.Remove(key); }
            }
            var sendPose = network.IsHost && Time.unscaledTime >= nextPoseAt;
            if (sendPose) nextPoseAt = Time.unscaledTime + 0.1f;
            foreach (var pair in states)
            {
                var state = pair.Value; var item = Resolve(state); if (item == null) continue;
                PlayerSnapshot owner;
                if (state.OwnerId != 0 && state.OwnerId != network.LocalPlayerId && network.Players.TryGetValue(state.OwnerId, out owner))
                {
                    item.transform.SetPositionAndRotation(owner.Position + Vector3.up * 1.1f + owner.ViewRotation * Vector3.forward * 0.7f, owner.ViewRotation);
                }
                if (sendPose && state.OwnerId == 0)
                {
                    state.Position = item.transform.position; state.Rotation = item.transform.rotation;
                    state.Revision = ++revision;
                    var pose = new ItemState { Sequence = state.Sequence, Revision = state.Revision, ActorId = state.ActorId,
                        OwnerId = state.OwnerId, Action = ItemAction.Pose, Scene = state.Scene, Path = state.Path,
                        ParentPath = state.ParentPath, SourcePath = state.SourcePath, ComponentIndex = state.ComponentIndex,
                        Position = state.Position, Rotation = state.Rotation, Velocity = state.Velocity };
                    network.BroadcastUnreliable(MessageType.ItemState, ItemStateCodec.Encode(pose));
                }
            }
        }
        public void SendCurrent(NetworkService network, uint player)
        { foreach (var state in states.Values) network.SendToPlayer(player, MessageType.ItemState, ItemStateCodec.Encode(state)); }

        public void ForgetPlayer(NetworkService network, uint player)
        {
            requests.Remove(player);
            if (!network.IsHost) return;
            foreach (var state in new List<ItemState>(states.Values))
            {
                if (state.OwnerId != player) continue;
                var item = Resolve(state); if (item == null) continue;
                state.OwnerId = 0; state.ActorId = 0; state.Action = ItemAction.PutDown;
                state.ParentPath = ""; state.Position = item.transform.position; state.Rotation = item.transform.rotation;
                Commit(state, network, true);
            }
        }
    }

    [HarmonyPatch(typeof(ItemIdentifier), "PickUp")]
    internal static class ItemPickedUpSyncPatch
    {
        private static bool Prefix() { ItemSync.BeginScenario(); var plugin = BloodlinkPlugin.Instance; return plugin == null || !plugin.IsConnected || plugin.IsHost || ItemSync.IsApplyingRemote; }
        private static void Postfix(ItemIdentifier __instance) { var plugin = BloodlinkPlugin.Instance; if (plugin != null) plugin.ItemSync.Notify(__instance, ItemAction.PickUp, plugin.Network); }
        private static Exception Finalizer(Exception __exception) => ItemSync.EndScenario(__exception);
    }
    [HarmonyPatch(typeof(ItemIdentifier), "PutDown")]
    internal static class ItemPutDownSyncPatch
    {
        private static bool Prefix() { ItemSync.BeginScenario(); var plugin = BloodlinkPlugin.Instance; return plugin == null || !plugin.IsConnected || plugin.IsHost || ItemSync.IsApplyingRemote; }
        private static void Postfix(ItemIdentifier __instance) { var plugin = BloodlinkPlugin.Instance; if (plugin != null) plugin.ItemSync.Notify(__instance, ItemAction.PutDown, plugin.Network); }
        private static Exception Finalizer(Exception __exception) => ItemSync.EndScenario(__exception);
    }
    [HarmonyPatch(typeof(ItemIdentifier), nameof(ItemIdentifier.CreateCopy))]
    internal static class ItemCopySyncPatch
    { private static void Postfix(ItemIdentifier __instance, ItemIdentifier __result) { var plugin = BloodlinkPlugin.Instance; if (plugin != null) plugin.ItemSync.RegisterCopy(__instance, __result, plugin.Network); } }

    [HarmonyPatch(typeof(ItemPlaceZone), nameof(ItemPlaceZone.CheckItem))]
    internal static class ItemPlaceZoneAuthorityPatch
    {
        private static bool Prefix(bool prelim)
        { var plugin = BloodlinkPlugin.Instance; return prelim || plugin == null || !plugin.IsConnected || plugin.IsHost || ItemSync.IsApplyingRemote; }
    }
}
