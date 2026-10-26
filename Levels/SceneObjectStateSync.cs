using System;
using System.Collections.Generic;
using ULTRAKILL.Bloodlink.Networking;
using UnityEngine;

namespace ULTRAKILL.Bloodlink.Levels
{
    // Host-authoritative state for native scene interactions and movable props.
    internal sealed class SceneObjectStateSync
    {
        private sealed class TrackedObject { public GameObject Target; public SceneObjectState Last; public int InstanceId; }
        private sealed class DeferredState { public SceneObjectState State; public float ExpiresAt; }
        private readonly Dictionary<string, SceneObjectState> states = new Dictionary<string, SceneObjectState>(StringComparer.Ordinal);
        private readonly Dictionary<string, uint> appliedRevisions = new Dictionary<string, uint>(StringComparer.Ordinal);
        private readonly Dictionary<string, SceneObjectState> lastLocalPoses = new Dictionary<string, SceneObjectState>(StringComparer.Ordinal);
        private readonly Dictionary<string, DeferredState> deferredStates = new Dictionary<string, DeferredState>(StringComparer.Ordinal);
        private readonly Dictionary<string, TrackedObject> lifecycleByKey = new Dictionary<string, TrackedObject>(StringComparer.Ordinal);
        private readonly List<TrackedObject> lifecycleObjects = new List<TrackedObject>();
        private readonly List<Rigidbody> physicsBodies = new List<Rigidbody>();
        private readonly HashSet<int> knownInstanceIds = new HashSet<int>();
        private readonly HashSet<string> destroyedObjects = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<uint, float> senderLastAction = new Dictionary<uint, float>();
        private readonly ReliableEventSequenceTracker requestSequences = new ReliableEventSequenceTracker();
        private readonly RequestReceiptJournal receipts = new RequestReceiptJournal();
        private uint sequence;
        private uint revision;
        private int poseCursor;
        private int lifecycleCursor = -1;
        private float nextLifecycleScan;
        private float nextPoseUpdate;
        private float nextDiscoveryAt;
        private float nextDeferredRetryAt;
        private static int applyDepth;

        internal static bool IsApplyingRemote => applyDepth > 0;
        public string Diagnostics => "scene objects: states=" + states.Count + ", deferred=" + deferredStates.Count +
            ", tracked=" + lifecycleObjects.Count + ", bodies=" + physicsBodies.Count;

        public void Reset()
        {
            states.Clear(); appliedRevisions.Clear(); lastLocalPoses.Clear(); deferredStates.Clear(); lifecycleByKey.Clear(); lifecycleObjects.Clear();
            physicsBodies.Clear(); knownInstanceIds.Clear(); destroyedObjects.Clear(); senderLastAction.Clear();
            requestSequences.Clear(); receipts.Clear(); sequence = revision = 0; poseCursor = 0;
            lifecycleCursor = -1;
            nextLifecycleScan = nextPoseUpdate = nextDiscoveryAt = nextDeferredRetryAt = 0f; applyDepth = 0;
            PrimeScene();
        }

        internal void OnSceneChanged() { Reset(); }

        internal void PrimeScene()
        {
            lifecycleByKey.Clear(); lifecycleObjects.Clear(); physicsBodies.Clear(); knownInstanceIds.Clear();
            DiscoverSceneObjects();
        }

        private static bool IsTrackedComponentName(string name)
        {
            return name == "Breakable" || name == "Glass" || name == "ItemIdentifier" || name == "WeaponPickUp" ||
                name == "AltPickUp" || name == "Bonus" || name == "Skull" || name == "Door" || name == "CheckPoint" ||
                name == "ActivateArena" || name == "ActivateNextWave" || name == "ItemPlaceZone" || name == "ItemTrigger" ||
                name == "BreakOnImpact" || name == "DualWieldPickup" || name == "ClashModePickup" || name == "BaitItem" || name == "TeleportItem";
        }

        internal static bool ShouldTrack(GameObject target)
        {
            if (target == null || !target.scene.IsValid() || target.scene.name == "DontDestroyOnLoad") return false;
            // A checkpoint keeps whole room templates far outside the level and
            // replaces their live copies on a solo restart. Never interpret those
            // room containers or gore-distance culling roots as shared props.
            if (target.GetComponentInChildren<GoreZone>(true) != null) return false;
            if (target.GetComponentInChildren<NewMovement>(true) != null ||
                target.GetComponentInChildren<ULTRAKILL.Bloodlink.Players.RemotePlayerVisual>(true) != null) return false;
            var hasPhysicsBody = target.GetComponent<Rigidbody>() != null && !target.GetComponent<Rigidbody>().isKinematic;
            var interactive = false;
            var current = target.transform;
            while (current != null)
            {
                if (current.name.EndsWith("(Clone)", StringComparison.Ordinal) && !SceneObjectIdentity.HasPrimedIdentity(current)) return false;
                foreach (var component in current.GetComponents<Component>())
                {
                    if (component == null) continue;
                    var name = component.GetType().Name;
                    if (name == "EnemyIdentifier" || name == "NewMovement" || name == "Canvas" || name == "RemotePlayerVisual" || name == "Coin" ||
                        name.IndexOf("Projectile", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("Bullet", StringComparison.OrdinalIgnoreCase) >= 0) return false;
                    if (IsTrackedComponentName(name)) interactive = true;
                }
                current = current.parent;
            }
            // Scene-authored props and their activation parents also participate.
            // Effects, players, UI and enemy replicas have dedicated authorities.
            return interactive || hasPhysicsBody || (SceneObjectIdentity.HasPrimedIdentity(target.transform) &&
                (target.GetComponentInChildren<Collider>(true) != null || target.GetComponentInChildren<Renderer>(true) != null));
        }

        internal SceneObjectState Capture(GameObject target, SceneObjectOperation operation, bool active, float delay = 0f)
        {
            if (IsApplyingRemote || !string.IsNullOrEmpty(SceneHelper.PendingScene) || !ShouldTrack(target)) return null;
            return CaptureKnown(target, operation, active, delay);
        }

        private static SceneObjectState CaptureKnown(GameObject target, SceneObjectOperation operation, bool active, float delay = 0f)
        {
            if (target == null || !target.scene.IsValid()) return null;
            string path;
            if (!SceneObjectIdentity.TryBuildPath(target.transform, out path)) return null;
            var body = target.GetComponent<Rigidbody>();
            return new SceneObjectState { Operation = operation, Scene = target.scene.name, Path = path,
                Position = target.transform.position, Rotation = target.transform.rotation,
                Velocity = body != null ? body.velocity : Vector3.zero, Active = active, Delay = delay };
        }

        internal SceneObjectState CaptureComponent(Component target, float delay = 0f)
        {
            if (target == null || IsApplyingRemote || !string.IsNullOrEmpty(SceneHelper.PendingScene) || !ShouldTrack(target.gameObject)) return null;
            var components = target.GetComponents(target.GetType());
            var index = Array.IndexOf(components, target);
            string path;
            if (index < 0 || index > byte.MaxValue || !SceneObjectIdentity.TryBuildPath(target.transform, out path)) return null;
            var body = target.GetComponent<Rigidbody>();
            return new SceneObjectState { Operation = SceneObjectOperation.Destroy, Scene = target.gameObject.scene.name, Path = path,
                ComponentType = target.GetType().FullName, ComponentIndex = (byte)index, Position = target.transform.position,
                Rotation = target.transform.rotation, Velocity = body != null ? body.velocity : Vector3.zero, Delay = delay };
        }

        internal void CommitLocal(SceneObjectState state, NetworkService network)
        {
            if (state == null || network == null || !network.IsConnected || IsApplyingRemote) return;
            if (state.Operation == SceneObjectOperation.Destroy) destroyedObjects.Add(Key(state));
            else if (state.Operation == SceneObjectOperation.SetActive) UpdateTrackedLifecycle(state);
            NextSequence(); state.Sequence = sequence; state.OwnerId = network.LocalPlayerId;
            if (network.IsHost) Commit(state, network);
            else network.SendToHost(MessageType.SceneObjectRequest, SceneObjectStateCodec.Encode(state));
        }

        internal void HandleRequest(uint sender, byte[] payload, NetworkService network)
        {
            if (network == null || !network.IsHost || !network.Players.ContainsKey(sender)) return;
            var request = SceneObjectStateCodec.Decode(payload);
            if (!SceneObjectIdentity.IsCurrentScene(request.Scene) || request.Revision != 0) return;
            if (request.Operation == SceneObjectOperation.Pose) { HandlePoseRequest(sender, request, network); return; }
            byte[] previous;
            if (receipts.TryGet(sender, request.Sequence, out previous))
            { network.SendToPlayer(sender, MessageType.SceneObjectState, previous); return; }
            if (!requestSequences.TryAccept(sender, request.Sequence)) return;
            var now = Time.unscaledTime; float last;
            if (senderLastAction.TryGetValue(sender, out last) && now - last < 0.015f) return;
            senderLastAction[sender] = now;
            var player = network.Players[sender];
            var target = SceneObjectIdentity.Resolve<Transform>(request.Scene, request.Path, 0);
            if (player == null || target == null || !ShouldTrack(target.gameObject) || (player.MovementFlags & 4) != 0 ||
                (player.Position - target.position).sqrMagnitude > 600f * 600f) return;
            request.OwnerId = sender;
            ApplyNative(target.gameObject, request);
            receipts.Record(sender, request.Sequence, Commit(request, network));
        }

        private void HandlePoseRequest(uint sender, SceneObjectState request, NetworkService network)
        {
            if (!requestSequences.TryAccept(sender, request.Sequence)) return;
            var now = Time.unscaledTime; float last;
            if (senderLastAction.TryGetValue(sender | 0x80000000u, out last) && now - last < 0.01f) return;
            senderLastAction[sender | 0x80000000u] = now;
            var player = network.Players[sender];
            var target = SceneObjectIdentity.Resolve<Transform>(request.Scene, request.Path, 0);
            if (player == null || target == null || (player.MovementFlags & 4) != 0 || (player.Position - target.position).sqrMagnitude > 35f * 35f) return;
            var body = target.GetComponent<Rigidbody>();
            if (body == null || !knownInstanceIds.Contains(body.GetInstanceID())) return;
            request.OwnerId = sender; ApplyNative(target.gameObject, request); CommitPose(request, network);
        }

        private byte[] Commit(SceneObjectState state, NetworkService network)
        {
            unchecked { if (++revision == 0) revision = 1; }
            state.Revision = revision; states[Key(state)] = state;
            if (state.Operation == SceneObjectOperation.Destroy) destroyedObjects.Add(Key(state));
            else if (state.Operation == SceneObjectOperation.SetActive) UpdateTrackedLifecycle(state);
            var payload = SceneObjectStateCodec.Encode(state);
            network.BroadcastReliable(MessageType.SceneObjectState, payload);
            return payload;
        }

        internal void Receive(byte[] payload, NetworkService network)
        {
            if (network == null || network.IsHost) return;
            var state = SceneObjectStateCodec.Decode(payload);
            if (!SceneObjectIdentity.IsCurrentScene(state.Scene) || state.Revision == 0) return;
            var key = Key(state); uint previous;
            if (appliedRevisions.TryGetValue(key, out previous) && unchecked((int)(state.Revision - previous)) <= 0) return;
            DeferredState queued;
            if (deferredStates.TryGetValue(key, out queued) && unchecked((int)(state.Revision - queued.State.Revision)) < 0) return;
            states[key] = state;
            if (!network.SceneSnapshotSyncReady)
            {
                deferredStates[key] = new DeferredState { State = state, ExpiresAt = Time.unscaledTime + 30f };
                return;
            }
            var target = SceneObjectIdentity.Resolve<Transform>(state.Scene, state.Path, 0);
            if (target == null)
            {
                deferredStates[key] = new DeferredState { State = state, ExpiresAt = Time.unscaledTime + 30f };
                return;
            }
            try
            {
                ApplyNative(target.gameObject, state);
                appliedRevisions[key] = state.Revision;
                deferredStates.Remove(key);
            }
            catch (Exception ex)
            {
                deferredStates[key] = new DeferredState { State = state, ExpiresAt = Time.unscaledTime + 30f };
                UnityEngine.Debug.LogWarning("Bloodlink scene-state apply deferred for " + state.Path + ": " + ex.Message);
            }
        }

        private void CommitPose(SceneObjectState state, NetworkService network)
        {
            unchecked { if (++revision == 0) revision = 1; }
            state.Revision = revision; states[Key(state)] = state;
            network.BroadcastUnreliable(MessageType.SceneObjectState, SceneObjectStateCodec.Encode(state));
        }

        internal void Update(NetworkService network)
        {
            if (network == null || !network.IsConnected) return;
            var now = Time.unscaledTime;
            if (network.SceneSnapshotSyncReady && now >= nextDeferredRetryAt && deferredStates.Count > 0)
            {
                nextDeferredRetryAt = now + 0.2f;
                foreach (var key in new List<string>(deferredStates.Keys))
                {
                    DeferredState pending;
                    if (!deferredStates.TryGetValue(key, out pending)) continue;
                    if (now >= pending.ExpiresAt || !SceneObjectIdentity.IsCurrentScene(pending.State.Scene))
                    { deferredStates.Remove(key); continue; }
                    var target = SceneObjectIdentity.Resolve<Transform>(pending.State.Scene, pending.State.Path, 0);
                    if (target == null) continue;
                    try
                    {
                        ApplyNative(target.gameObject, pending.State);
                        appliedRevisions[key] = pending.State.Revision;
                        deferredStates.Remove(key);
                    }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogWarning("Bloodlink deferred scene-state apply failed for " + pending.State.Path + ": " + ex.Message);
                    }
                }
            }
            if (!network.SceneSnapshotSyncReady) return;
            if (now >= nextDiscoveryAt) DiscoverSceneObjects();
            if (now >= nextLifecycleScan)
            {
                if (lifecycleCursor < 0) lifecycleCursor = lifecycleObjects.Count - 1;
                ScanLifecycleStates(network);
                nextLifecycleScan = lifecycleCursor < 0 ? now + 0.1f : now;
            }
            if (now < nextPoseUpdate || physicsBodies.Count == 0) return;
            nextPoseUpdate = now + 0.2f;
            var start = poseCursor % physicsBodies.Count;
            var inspected = 0; var sent = 0;
            while (inspected < physicsBodies.Count && sent < 8)
            {
                var body = physicsBodies[(start + inspected) % physicsBodies.Count]; inspected++;
                if (body == null || !body.gameObject.scene.IsValid() || !SceneObjectIdentity.IsCurrentScene(body.gameObject.scene.name) ||
                    !body.gameObject.activeInHierarchy || body.isKinematic) continue;
                var item = body.GetComponent<ItemIdentifier>();
                if (item != null && item.pickedUp) continue;
                string path;
                if (!SceneObjectIdentity.TryBuildPath(body.transform, out path)) continue;
                var key = body.gameObject.scene.name + "|" + path + "|pose";
                var state = new SceneObjectState { Operation = SceneObjectOperation.Pose, Scene = body.gameObject.scene.name,
                    Path = path, Position = body.position, Rotation = body.rotation, Velocity = body.velocity, Active = true };
                if (network.IsHost)
                {
                    if (network.Players.Count <= 1) continue;
                    SceneObjectState previous;
                    if (states.TryGetValue(key, out previous) && !PoseChanged(previous, state)) continue;
                    state.OwnerId = network.LocalPlayerId; NextSequence(); state.Sequence = sequence;
                    CommitPose(state, network); sent++;
                }
                else
                {
                    SceneObjectState previous;
                    if (lastLocalPoses.TryGetValue(key, out previous) && !PoseChanged(previous, state)) continue;
                    state.OwnerId = network.LocalPlayerId; NextSequence(); state.Sequence = sequence;
                    if (network.SendToHost(MessageType.SceneObjectRequest, SceneObjectStateCodec.Encode(state)))
                    { lastLocalPoses[key] = state; sent++; }
                }
            }
            poseCursor = (start + Math.Max(inspected, 1)) % physicsBodies.Count;
        }

        private static bool PoseChanged(SceneObjectState a, SceneObjectState b) =>
            (a.Position - b.Position).sqrMagnitude >= 0.04f || Quaternion.Angle(a.Rotation, b.Rotation) >= 7f ||
            (a.Velocity - b.Velocity).sqrMagnitude >= 1f;

        private void ScanLifecycleStates(NetworkService network)
        {
            // Spread large maps across frames instead of pausing rendering for a
            // complete hierarchy scan; direct glass/wood mutations remain immediate.
            for (var inspected = 0; lifecycleCursor >= 0 && inspected < 2048; inspected++)
            {
                var i = lifecycleCursor--;
                var tracked = lifecycleObjects[i];
                if (tracked.Target == null)
                {
                    if (!destroyedObjects.Contains(Key(tracked.Last)))
                    {
                        var prior = tracked.Last;
                        CommitLocal(new SceneObjectState { Operation = SceneObjectOperation.Destroy, Scene = prior.Scene, Path = prior.Path,
                            Position = prior.Position, Rotation = prior.Rotation, Active = false }, network);
                    }
                    lifecycleByKey.Remove(Key(tracked.Last)); knownInstanceIds.Remove(tracked.InstanceId);
                    lifecycleObjects.RemoveAt(i); continue;
                }
                var active = tracked.Target.activeSelf;
                if (tracked.Last.Active == active) continue;
                var state = CaptureKnown(tracked.Target, SceneObjectOperation.SetActive, active);
                if (state == null) continue;
                tracked.Last = state;
                CommitLocal(state, network);
            }
        }

        private void DiscoverSceneObjects()
        {
            nextDiscoveryAt = Time.unscaledTime + 30f;
            var candidates = new HashSet<GameObject>();
            AddTrackableComponents(Resources.FindObjectsOfTypeAll<Breakable>(), candidates);
            AddTrackableComponents(Resources.FindObjectsOfTypeAll<Glass>(), candidates);
            AddTrackableComponents(Resources.FindObjectsOfTypeAll<ItemIdentifier>(), candidates);
            AddTrackableComponents(Resources.FindObjectsOfTypeAll<WeaponPickUp>(), candidates);
            AddTrackableComponents(Resources.FindObjectsOfTypeAll<AltPickUp>(), candidates);
            AddTrackableComponents(Resources.FindObjectsOfTypeAll<Bonus>(), candidates);
            AddTrackableComponents(Resources.FindObjectsOfTypeAll<Skull>(), candidates);
            AddTrackableComponents(Resources.FindObjectsOfTypeAll<Door>(), candidates);
            AddTrackableComponents(Resources.FindObjectsOfTypeAll<CheckPoint>(), candidates);
            AddTrackableComponents(Resources.FindObjectsOfTypeAll<ActivateArena>(), candidates);
            AddTrackableComponents(Resources.FindObjectsOfTypeAll<ActivateNextWave>(), candidates);
            AddTrackableComponents(Resources.FindObjectsOfTypeAll<ItemPlaceZone>(), candidates);
            AddTrackableComponents(Resources.FindObjectsOfTypeAll<ItemTrigger>(), candidates);
            AddTrackableComponents(Resources.FindObjectsOfTypeAll<BreakOnImpact>(), candidates);
            AddTrackableComponents(Resources.FindObjectsOfTypeAll<DualWieldPickup>(), candidates);
            AddTrackableComponents(Resources.FindObjectsOfTypeAll<ClashModePickup>(), candidates);
            AddTrackableComponents(Resources.FindObjectsOfTypeAll<BaitItem>(), candidates);
            AddTrackableComponents(Resources.FindObjectsOfTypeAll<TeleportItem>(), candidates);
            AddTrackableComponents(Resources.FindObjectsOfTypeAll<Collider>(), candidates);
            AddTrackableComponents(Resources.FindObjectsOfTypeAll<Renderer>(), candidates);
            foreach (var target in candidates) AddTrackedObject(target);
            foreach (var body in Resources.FindObjectsOfTypeAll<Rigidbody>())
            {
                if (body == null || !body.gameObject.scene.IsValid() || !SceneObjectIdentity.IsCurrentScene(body.gameObject.scene.name) ||
                    !SceneObjectIdentity.HasPrimedIdentity(body.transform) || knownInstanceIds.Contains(body.GetInstanceID()) || !ShouldTrack(body.gameObject)) continue;
                if (knownInstanceIds.Add(body.GetInstanceID())) physicsBodies.Add(body);
            }
        }

        private static void AddTrackableComponents<T>(T[] components, HashSet<GameObject> targets) where T : Component
        {
            if (components == null) return;
            foreach (var component in components)
            {
                if (component == null || !component.gameObject.scene.IsValid() || !SceneObjectIdentity.IsCurrentScene(component.gameObject.scene.name)) continue;
                for (var transform = component.transform; transform != null; transform = transform.parent)
                    if (SceneObjectIdentity.HasPrimedIdentity(transform)) targets.Add(transform.gameObject);
            }
        }

        private void AddTrackedObject(GameObject target)
        {
            if (target == null || !SceneObjectIdentity.HasPrimedIdentity(target.transform)) return;
            var id = target.GetInstanceID();
            if (knownInstanceIds.Contains(id) || !ShouldTrack(target) || !knownInstanceIds.Add(id)) return;
            var state = CaptureKnown(target, SceneObjectOperation.SetActive, target.activeSelf);
            if (state == null) { knownInstanceIds.Remove(id); return; }
            var tracked = new TrackedObject { Target = target, Last = state, InstanceId = id };
            lifecycleObjects.Add(tracked); lifecycleByKey[Key(state)] = tracked;
        }

        private void UpdateTrackedLifecycle(SceneObjectState state)
        {
            TrackedObject tracked;
            if (lifecycleByKey.TryGetValue(Key(state), out tracked) && tracked.Target != null) tracked.Last = state;
        }

        private void ApplyNative(GameObject target, SceneObjectState state)
        {
            if (target == null || !ShouldTrack(target)) return;
            applyDepth++;
            try
            {
                switch (state.Operation)
                {
                    case SceneObjectOperation.SetActive:
                        if (target.activeSelf != state.Active) target.SetActive(state.Active);
                        UpdateTrackedLifecycle(state);
                        break;
                    case SceneObjectOperation.Destroy:
                        destroyedObjects.Add(Key(state));
                        if (!string.IsNullOrEmpty(state.ComponentType))
                        {
                            var found = 0;
                            foreach (var component in target.GetComponents<Component>())
                            {
                                if (component == null || component.GetType().FullName != state.ComponentType) continue;
                                if (found++ != state.ComponentIndex) continue;
                                UnityEngine.Object.Destroy(component, state.Delay); break;
                            }
                        }
                        else UnityEngine.Object.Destroy(target, state.Delay);
                        break;
                    case SceneObjectOperation.Pose:
                        target.transform.SetPositionAndRotation(state.Position, state.Rotation);
                        var body = target.GetComponent<Rigidbody>(); if (body != null) body.velocity = state.Velocity;
                        break;
                }
            }
            finally { applyDepth--; }
        }

        internal void SendCurrentToPlayer(NetworkService network, uint player)
        {
            if (network == null || !network.IsHost) return;
            foreach (var state in states.Values) network.SendToPlayer(player, MessageType.SceneObjectState, SceneObjectStateCodec.Encode(state));
        }

        internal void OnPlayerLeft(uint player) { senderLastAction.Remove(player); senderLastAction.Remove(player | 0x80000000u); requestSequences.Remove(player); }
        private void NextSequence() { unchecked { if (++sequence == 0) sequence = 1; } }
        private static string Key(SceneObjectState state) => state.Scene + "|" + state.Path +
            (state.Operation == SceneObjectOperation.Pose ? "|pose" : "|lifecycle") +
            (!string.IsNullOrEmpty(state.ComponentType) ? "|" + state.ComponentType + ":" + state.ComponentIndex : string.Empty);
    }
}
