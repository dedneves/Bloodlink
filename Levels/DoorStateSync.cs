using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ULTRAKILL.Bloodlink.Networking;
using UnityEngine;

namespace ULTRAKILL.Bloodlink.Levels
{
    internal enum DoorMutation : byte
    {
        Open = 1,
        Close = 2,
        Lock = 3,
        Unlock = 4,
        LockOpen = 5,
        LockClose = 6,
        Snapshot = 7
    }

    internal sealed class DoorMutationEvent
    {
        public uint Sequence;
        public uint DoorKey;
        public DoorMutation Mutation;
        public bool Enemy;
        public bool Skull;
        public bool Force;
        public bool Open;
        public bool Locked;
        public bool StartOpen;
        public int Requests;
        public int OpenLocks;
    }

    internal static class DoorStateCodec
    {
        private const ushort ValidFlags = 0x3f;

        public static byte[] Encode(DoorMutationEvent value)
        {
            Validate(value);
            ushort flags = 0;
            if (value.Enemy) flags |= 1;
            if (value.Skull) flags |= 2;
            if (value.Force) flags |= 4;
            if (value.Open) flags |= 8;
            if (value.Locked) flags |= 16;
            if (value.StartOpen) flags |= 32;
            using (var stream = new MemoryStream(19))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(value.Sequence);
                writer.Write(value.DoorKey);
                writer.Write((byte)value.Mutation);
                writer.Write(flags);
                writer.Write(value.Requests);
                writer.Write(value.OpenLocks);
                return stream.ToArray();
            }
        }

        public static DoorMutationEvent Decode(byte[] payload)
        {
            if (payload == null || payload.Length != 19) throw new InvalidDataException("Invalid door state payload length.");
            using (var stream = new MemoryStream(payload, false))
            using (var reader = new BinaryReader(stream))
            {
                var state = new DoorMutationEvent
                {
                    Sequence = reader.ReadUInt32(),
                    DoorKey = reader.ReadUInt32(),
                    Mutation = (DoorMutation)reader.ReadByte()
                };
                var flags = reader.ReadUInt16();
                state.Requests = reader.ReadInt32();
                state.OpenLocks = reader.ReadInt32();
                state.Enemy = (flags & 1) != 0;
                state.Skull = (flags & 2) != 0;
                state.Force = (flags & 4) != 0;
                state.Open = (flags & 8) != 0;
                state.Locked = (flags & 16) != 0;
                state.StartOpen = (flags & 32) != 0;
                if ((flags & ~ValidFlags) != 0) throw new InvalidDataException("Invalid door state flags.");
                Validate(state);
                return state;
            }
        }

        public static byte[] EncodeOpenRequest(uint sequence, uint doorKey, bool enemy, bool skull)
        {
            if (sequence == 0 || doorKey == 0) throw new ArgumentOutOfRangeException();
            using (var stream = new MemoryStream(10))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(sequence);
                writer.Write(doorKey);
                writer.Write((byte)DoorMutation.Open);
                writer.Write((byte)((enemy ? 1 : 0) | (skull ? 2 : 0)));
                return stream.ToArray();
            }
        }

        public static DoorMutationEvent DecodeOpenRequest(byte[] payload)
        {
            if (payload == null || payload.Length != 10) throw new InvalidDataException("Invalid door open request length.");
            using (var stream = new MemoryStream(payload, false))
            using (var reader = new BinaryReader(stream))
            {
                var sequence = reader.ReadUInt32();
                var key = reader.ReadUInt32();
                var mutation = (DoorMutation)reader.ReadByte();
                var flags = reader.ReadByte();
                if (sequence == 0 || key == 0 || mutation != DoorMutation.Open || (flags & ~3) != 0)
                    throw new InvalidDataException("Invalid door open request values.");
                return new DoorMutationEvent
                {
                    Sequence = sequence,
                    DoorKey = key,
                    Mutation = mutation,
                    Enemy = (flags & 1) != 0,
                    Skull = (flags & 2) != 0
                };
            }
        }

        private static void Validate(DoorMutationEvent value)
        {
            if (value == null || value.Sequence == 0 || value.DoorKey == 0 || !Enum.IsDefined(typeof(DoorMutation), value.Mutation) ||
                value.Requests < 0 || value.Requests > 10000 || value.OpenLocks < 0 || value.OpenLocks > 10000)
                throw new InvalidDataException("Invalid door state values.");
        }
    }

    public sealed class DoorStateSync
    {
        private sealed class PendingState
        {
            public DoorMutationEvent State;
            public float Age;
        }

        private static readonly HashSet<Door> compoundMutations = new HashSet<Door>();
        private readonly Dictionary<uint, PendingState> pendingByDoor = new Dictionary<uint, PendingState>();
        private readonly ReliableEventSequenceTracker requestSequences = new ReliableEventSequenceTracker();
        private readonly Dictionary<uint, float> requestTimes = new Dictionary<uint, float>();
        private uint nextSequence;
        private uint nextRequestSequence;
        private uint lastReceivedSequence;
        private float resolveTimer;
        private sealed class PredictedDoor { public Door Door; public DoorMutationEvent Original; public float ExpiresAt; }
        private readonly Dictionary<uint, PredictedDoor> predictions = new Dictionary<uint, PredictedDoor>();

        public static bool IsApplyingRemote { get; private set; }

        public void Reset()
        {
            predictions.Clear();
            pendingByDoor.Clear();
            requestSequences.Clear();
            requestTimes.Clear();
            nextSequence = 0;
            nextRequestSequence = 0;
            lastReceivedSequence = 0;
            resolveTimer = 0f;
            compoundMutations.Clear();
            IsApplyingRemote = false;
        }

        public void ResetForScene()
        {
            predictions.Clear();
            pendingByDoor.Clear();
            compoundMutations.Clear();
            resolveTimer = 0f;
        }

        public void Update(NetworkService network, float deltaTime)
        {
            if (network == null || network.IsHost) return;
            if (predictions.Count > 0)
                foreach (var key in new List<uint>(predictions.Keys))
                {
                    var pending = predictions[key];
                    if (Time.unscaledTime < pending.ExpiresAt) continue;
                    if (pending.Door != null) ApplyMutation(pending.Door, pending.Original);
                    predictions.Remove(key);
                }
            if (pendingByDoor.Count == 0) return;
            resolveTimer += Mathf.Max(0f, deltaTime);
            if (resolveTimer < 0.25f) return;
            var elapsed = resolveTimer;
            resolveTimer = 0f;

            var applied = new List<uint>();
            foreach (var pair in pendingByDoor)
            {
                pair.Value.Age += elapsed;
                var door = FindDoor(pair.Key);
                if (door != null)
                {
                    ApplyMutation(door, pair.Value.State);
                    applied.Add(pair.Key);
                }
                else if (pair.Value.Age > 60f) applied.Add(pair.Key);
            }
            for (var i = 0; i < applied.Count; i++) pendingByDoor.Remove(applied[i]);
        }

        public void RequestOpen(NetworkService network, Door door, bool enemy, bool skull)
        {
            if (network == null || !network.IsConnected || network.IsHost || door == null) return;
            unchecked { nextRequestSequence++; }
            if (nextRequestSequence == 0) nextRequestSequence = 1;
            var key = GetStableKey(door);
            if (key == 0) return;
            if (!door.locked && !predictions.ContainsKey(key))
                predictions[key] = new PredictedDoor { Door = door, Original = CreateState(door, key, DoorMutation.Snapshot),
                    ExpiresAt = Time.unscaledTime + Mathf.Max(1.25f, network.PingMilliseconds * 0.004f) };
            network.SendToHost(MessageType.DoorRequest,
                DoorStateCodec.EncodeOpenRequest(nextRequestSequence, key, enemy, skull));
        }

        public bool TryGetOpenRequest(uint senderId, byte[] payload, NetworkService network, out Door door, out bool enemy, out bool skull)
        {
            door = null;
            enemy = false;
            skull = false;
            if (network == null || !network.IsHost || senderId == 0 || !network.HasFreshPlayerSnapshot(senderId, 0.5f)) return false;
            var request = DoorStateCodec.DecodeOpenRequest(payload);
            if (!requestSequences.TryAccept(senderId, request.Sequence)) return false;
            var now = Time.unscaledTime;
            float last;
            if (requestTimes.TryGetValue(senderId, out last) && now - last < 0.08f) return false;
            requestTimes[senderId] = now;

            PlayerSnapshot player;
            if (!network.Players.TryGetValue(senderId, out player) || player == null || (player.MovementFlags & 4) != 0) return false;
            door = FindDoor(request.DoorKey);
            if (door == null || door.locked) return false;
            var collider = door.GetComponentInChildren<Collider>();
            var closest = collider != null ? collider.ClosestPoint(player.Position) : door.transform.position;
            if ((closest - player.Position).sqrMagnitude > 8f * 8f) return false;
            enemy = request.Enemy;
            skull = request.Skull;
            return true;
        }

        internal void BroadcastMutation(NetworkService network, Door door, DoorMutation mutation, bool enemy = false, bool skull = false, bool force = false)
        {
            if (network == null || !network.IsHost || !network.IsConnected || door == null || IsApplyingRemote || compoundMutations.Contains(door)) return;
            BroadcastState(network, door, mutation, enemy, skull, force);
        }

        public void SendCurrentToPlayer(NetworkService network, uint playerId)
        {
            if (network == null || !network.IsHost || !network.IsConnected || playerId == 0) return;
            var doors = Resources.FindObjectsOfTypeAll<Door>();
            for (var i = 0; i < doors.Length; i++)
            {
                var door = doors[i];
                if (door == null || !door.gameObject.scene.IsValid()) continue;
                var key = GetStableKey(door);
                if (key == 0) continue;
                var state = CreateState(door, key, DoorMutation.Snapshot);
                unchecked { nextSequence++; }
                if (nextSequence == 0) nextSequence = 1;
                state.Sequence = nextSequence;
                network.SendToPlayer(playerId, MessageType.DoorState, DoorStateCodec.Encode(state));
            }
        }

        public void ReceiveState(byte[] payload)
        {
            var state = DoorStateCodec.Decode(payload);
            if (lastReceivedSequence != 0 && !IsNewer(state.Sequence, lastReceivedSequence)) return;
            predictions.Remove(state.DoorKey);
            lastReceivedSequence = state.Sequence;
            var door = FindDoor(state.DoorKey);
            if (door == null)
            {
                if (pendingByDoor.Count < 512 || pendingByDoor.ContainsKey(state.DoorKey))
                    pendingByDoor[state.DoorKey] = new PendingState { State = state };
                return;
            }
            ApplyMutation(door, state);
        }

        public void RemovePlayer(uint playerId)
        {
            requestSequences.Remove(playerId);
            requestTimes.Remove(playerId);
        }

        public void BeginCompoundMutation(Door door)
        {
            if (door != null) compoundMutations.Add(door);
        }

        public void EndCompoundMutation(Door door)
        {
            if (door != null) compoundMutations.Remove(door);
        }

        public bool IsCompoundMutation(Door door)
        {
            return door != null && compoundMutations.Contains(door);
        }

        public static uint GetStableKey(Door door)
        {
            return SceneObjectIdentity.GetStableKey(door);
        }

        private void BroadcastState(NetworkService network, Door door, DoorMutation mutation, bool enemy, bool skull, bool force)
        {
            unchecked { nextSequence++; }
            if (nextSequence == 0) nextSequence = 1;
            var state = CreateState(door, GetStableKey(door), mutation);
            state.Sequence = nextSequence;
            state.Enemy = enemy;
            state.Skull = skull;
            state.Force = force;
            network.BroadcastReliable(MessageType.DoorState, DoorStateCodec.Encode(state));
        }

        private static DoorMutationEvent CreateState(Door door, uint key, DoorMutation mutation)
        {
            return new DoorMutationEvent
            {
                DoorKey = key,
                Mutation = mutation,
                Open = door.open,
                Locked = door.locked,
                StartOpen = door.startOpen,
                Requests = Mathf.Clamp(door.requests, 0, 10000),
                OpenLocks = Mathf.Clamp(door.openLocks, 0, 10000)
            };
        }

        private static void ApplyMutation(Door door, DoorMutationEvent state)
        {
            if (door == null) return;
            IsApplyingRemote = true;
            try
            {
                switch (state.Mutation)
                {
                    case DoorMutation.Open:
                        // An older local lock bit can make Door.Open silently do
                        // nothing even though the host's authoritative mutation
                        // already unlocked this gate.
                        if (!state.Locked && door.locked) door.Unlock();
                        door.Open(state.Enemy, state.Skull);
                        break;
                    case DoorMutation.Close: door.Close(state.Force); break;
                    case DoorMutation.Lock: door.Lock(); break;
                    case DoorMutation.Unlock: door.Unlock(); break;
                    case DoorMutation.LockOpen: door.LockOpen(); break;
                    case DoorMutation.LockClose: door.LockClose(); break;
                    case DoorMutation.Snapshot:
                        if (state.Locked && !door.locked) door.Lock();
                        else if (!state.Locked && door.locked) door.Unlock();
                        if (state.Open && !door.open) door.Open(enemy: false, skull: true);
                        else if (!state.Open && door.open) door.Close(force: true);
                        door.open = state.Open;
                        door.locked = state.Locked;
                        door.startOpen = state.StartOpen;
                        door.requests = state.Requests;
                        door.openLocks = state.OpenLocks;
                        break;
                }
                if (state.Mutation != DoorMutation.Snapshot)
                {
                    door.open = state.Open;
                    door.locked = state.Locked;
                    door.startOpen = state.StartOpen;
                    door.requests = state.Requests;
                    door.openLocks = state.OpenLocks;
                }
            }
            finally { IsApplyingRemote = false; }
        }

        private static Door FindDoor(uint key)
        {
            var doors = Resources.FindObjectsOfTypeAll<Door>();
            for (var i = 0; i < doors.Length; i++)
            {
                var door = doors[i];
                if (door != null && door.gameObject.scene.IsValid() && GetStableKey(door) == key) return door;
            }
            return null;
        }

        private static bool IsNewer(uint candidate, uint previous)
        {
            return unchecked((int)(candidate - previous)) > 0;
        }
    }
}
