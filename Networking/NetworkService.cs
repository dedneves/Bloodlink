using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using LiteNetLib;
using UnityEngine;

namespace ULTRAKILL.Bloodlink.Networking
{
    public sealed class PlayerSnapshot
    {
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public uint PlayerId;
        public string PlayerName;
        public Vector3 Position;
        public Quaternion Rotation = Quaternion.identity;
        public Quaternion ViewRotation = Quaternion.identity;
        public Vector3 Velocity;
        public byte MovementFlags;
        public byte WeaponSlot;
        public byte WeaponVariation;
        public bool WeaponAlternate;
        public string WeaponName;
        public ulong WeaponInventoryMask;
        public ushort Health = 100;
        public uint Tick = 1;
        public uint PrimaryColor = PlayerAppearanceColor.DefaultPrimary;
        public uint SecondaryColor = PlayerAppearanceColor.DefaultSecondary;
        public uint LightColor = PlayerAppearanceColor.DefaultLight;
        public byte GlowIntensity = PlayerAppearanceColor.DefaultGlow;
        public int AnimatorStateHash;
        public float AnimatorNormalizedTime;

        internal byte[] Encode()
        {
            var nameBytes = EncodeBoundedUtf8(PlayerName ?? "Player", 48);
            using (var stream = new MemoryStream(64 + nameBytes.Length))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(PlayerId);
                writer.Write((byte)nameBytes.Length);
                writer.Write(nameBytes);
                WriteVector(writer, Position);
                writer.Write(Rotation.x); writer.Write(Rotation.y); writer.Write(Rotation.z); writer.Write(Rotation.w);
                writer.Write(ViewRotation.x); writer.Write(ViewRotation.y); writer.Write(ViewRotation.z); writer.Write(ViewRotation.w);
                WriteVector(writer, Velocity);
                writer.Write(MovementFlags);
                writer.Write(WeaponSlot);
                writer.Write(WeaponVariation);
                writer.Write(Health);
                writer.Write(Tick);
                var weaponNameBytes = EncodeBoundedUtf8(WeaponName ?? string.Empty, 48);
                writer.Write((byte)weaponNameBytes.Length);
                writer.Write(weaponNameBytes);
                writer.Write(WeaponInventoryMask);
                writer.Write(PrimaryColor);
                writer.Write(SecondaryColor);
                writer.Write(LightColor);
                writer.Write(GlowIntensity);
                writer.Write(AnimatorStateHash);
                writer.Write(AnimatorNormalizedTime);
                writer.Write(WeaponAlternate);
                return stream.ToArray();
            }
        }

        internal PlayerSnapshot Clone()
        {
            return new PlayerSnapshot
            {
                PlayerId = PlayerId,
                PlayerName = PlayerName,
                Position = Position,
                Rotation = Rotation,
                ViewRotation = ViewRotation,
                Velocity = Velocity,
                MovementFlags = MovementFlags,
                WeaponSlot = WeaponSlot,
                WeaponVariation = WeaponVariation,
                WeaponAlternate = WeaponAlternate,
                WeaponName = WeaponName,
                WeaponInventoryMask = WeaponInventoryMask,
                Health = Health,
                Tick = Tick,
                PrimaryColor = PrimaryColor,
                SecondaryColor = SecondaryColor,
                LightColor = LightColor,
                GlowIntensity = GlowIntensity,
                AnimatorStateHash = AnimatorStateHash,
                AnimatorNormalizedTime = AnimatorNormalizedTime
            };
        }

        internal static PlayerSnapshot Decode(byte[] payload)
        {
            using (var stream = new MemoryStream(payload, false))
            using (var reader = new BinaryReader(stream))
            {
                var id = reader.ReadUInt32();
                var nameLength = reader.ReadByte();
                if (nameLength > 48 || stream.Length - stream.Position < nameLength + 12 + 16 + 16 + 12 + 3 + 1 + 2 + 8 + 4 + 21)
                    throw new InvalidDataException("Invalid player snapshot length.");
                string name;
                try { name = StrictUtf8.GetString(reader.ReadBytes(nameLength)); }
                catch (DecoderFallbackException ex) { throw new InvalidDataException("Player snapshot contains invalid UTF-8 player name.", ex); }
                var position = ReadVector(reader);
                var rotation = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                var viewRotation = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                var velocity = ReadVector(reader);
                var movementFlags = reader.ReadByte();
                var weaponSlot = reader.ReadByte();
                var weaponVariation = reader.ReadByte();
                var health = reader.ReadUInt16();
                var tick = reader.ReadUInt32();
                var weaponNameLength = reader.ReadByte();
                if (weaponNameLength > 48 || stream.Length - stream.Position < weaponNameLength + 8 + 13)
                    throw new InvalidDataException("Invalid player weapon inventory length.");
                string weaponName;
                try { weaponName = StrictUtf8.GetString(reader.ReadBytes(weaponNameLength)); }
                catch (DecoderFallbackException ex) { throw new InvalidDataException("Player snapshot contains invalid UTF-8 weapon name.", ex); }
                var weaponInventoryMask = reader.ReadUInt64();
                if (!WeaponInventoryPolicy.IsValidMask(weaponInventoryMask))
                    throw new InvalidDataException("Player snapshot contains weapon inventory bits outside its slot layout.");
                var primaryColor = reader.ReadUInt32();
                var secondaryColor = reader.ReadUInt32();
                var lightColor = reader.ReadUInt32();
                var glowIntensity = reader.ReadByte();
                var animatorStateHash = reader.ReadInt32();
                var animatorNormalizedTime = reader.ReadSingle();
                var weaponAlternate = reader.ReadBoolean();
                if (stream.Position != stream.Length) throw new InvalidDataException("Trailing bytes in player snapshot.");
                if (id == 0 || tick == 0 || (movementFlags & 0xF0) != 0 ||
                    !IsFinite(position.x) || !IsFinite(position.y) || !IsFinite(position.z) ||
                    !IsFinite(velocity.x) || !IsFinite(velocity.y) || !IsFinite(velocity.z) ||
                    !IsFinite(rotation.x) || !IsFinite(rotation.y) || !IsFinite(rotation.z) || !IsFinite(rotation.w) ||
                    !IsFinite(viewRotation.x) || !IsFinite(viewRotation.y) || !IsFinite(viewRotation.z) || !IsFinite(viewRotation.w) ||
                    position.sqrMagnitude > 100000f * 100000f || velocity.sqrMagnitude > 10000f * 10000f ||
                    rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w < 0.01f ||
                    rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w > 4f || health > 200 ||
                    primaryColor > 0xFFFFFF || secondaryColor > 0xFFFFFF || lightColor > 0xFFFFFF)
                    throw new InvalidDataException("Player snapshot contains invalid state values (tick=" + tick +
                        ", flags=" + movementFlags + ", health=" + health + ", position=" + position +
                        ", velocity=" + velocity + ", rotation=" + rotation + ", view=" + viewRotation +
                        ", inventory=0x" + weaponInventoryMask.ToString("X") + ", colors=" + primaryColor + "/" +
                        secondaryColor + "/" + lightColor + ").");
                var viewMagnitude = viewRotation.x * viewRotation.x + viewRotation.y * viewRotation.y + viewRotation.z * viewRotation.z + viewRotation.w * viewRotation.w;
                if (viewMagnitude < 0.01f || viewMagnitude > 4f) throw new InvalidDataException("Invalid player view rotation.");
                if (!IsFinite(animatorNormalizedTime) || animatorNormalizedTime < 0f || animatorNormalizedTime >= 1f)
                    throw new InvalidDataException("Invalid player animator normalized time.");
                return new PlayerSnapshot { PlayerId = id, PlayerName = name, Position = position, Rotation = rotation, ViewRotation = viewRotation, Velocity = velocity, MovementFlags = movementFlags, WeaponSlot = weaponSlot, WeaponVariation = weaponVariation, WeaponAlternate = weaponAlternate, WeaponName = weaponName, WeaponInventoryMask = weaponInventoryMask, Health = health, Tick = tick, PrimaryColor = primaryColor, SecondaryColor = secondaryColor, LightColor = lightColor, GlowIntensity = glowIntensity, AnimatorStateHash = animatorStateHash, AnimatorNormalizedTime = animatorNormalizedTime };
            }
        }

        private static bool IsFinite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }

        private static byte[] EncodeBoundedUtf8(string value, int maximumBytes)
        {
            var chars = value.ToCharArray();
            var bytes = new byte[maximumBytes];
            int charsUsed, bytesUsed;
            bool completed;
            Encoding.UTF8.GetEncoder().Convert(chars, 0, chars.Length, bytes, 0, bytes.Length, true,
                out charsUsed, out bytesUsed, out completed);
            if (bytesUsed != bytes.Length) Array.Resize(ref bytes, bytesUsed);
            return bytes;
        }

        private static void WriteVector(BinaryWriter writer, Vector3 value)
        {
            writer.Write(value.x); writer.Write(value.y); writer.Write(value.z);
        }

        private static Vector3 ReadVector(BinaryReader reader)
        {
            return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }
    }

    public sealed class DiscoveredLobby
    {
        public string Name { get; internal set; }
        public string HostName { get; internal set; }
        public int PlayerCount { get; internal set; }
        public int MaxPlayers { get; internal set; }
        public bool RequiresPassword { get; internal set; }
        public string Address { get; internal set; }
        public int Port { get; internal set; }
        public DateTime LastSeenUtc { get; internal set; }
    }
    public sealed class NetworkService : ILiteNetEventListener, IDisposable
    {
        private sealed class TransportPeer
        {
            public LiteNetPeer LitePeer;
            public int RelayPeerId;
            public string Address;
            public Action DisconnectAction;
            public int Ping { get { return LitePeer != null ? LitePeer.Ping : 0; } }
            public void Disconnect() { if (LitePeer != null) LitePeer.Disconnect(); else DisconnectAction?.Invoke(); }
            public override bool Equals(object obj)
            {
                var other = obj as TransportPeer;
                if (other == null) return false;
                if (LitePeer != null || other.LitePeer != null) return LitePeer != null && LitePeer.Equals(other.LitePeer);
                return RelayPeerId == other.RelayPeerId;
            }
            public override int GetHashCode()
            {
                return LitePeer != null ? LitePeer.GetHashCode() : RelayPeerId;
            }
        }

        private struct PendingNetworkEvent
        {
            public Action Callback;
            public int PayloadBytes;
        }

        private const int UnreliableBacklogLimit = 512;
        private const int MaximumPendingNetworkEvents = 4096;
        private const int MaximumPendingNetworkBytes = 8 * 1024 * 1024;
        private const float PlayerHandshakeTimeoutSeconds = 12f;
        internal const float MaximumLocalPositionCorrection = 8f;
        internal const float MaximumPlayerMovementSpeed = 260f;
        private const float PlayerMovementPositionTolerance = 0.5f;
        internal static string DefaultConnectionKey
        {
            get { return "BLOODLINK-PROTOCOL-" + NetworkMessage.CurrentProtocolVersion; }
        }
        private const float ReconnectReservationSeconds = 30f;
        private readonly Queue<PendingNetworkEvent> pendingNetworkEvents = new Queue<PendingNetworkEvent>();
        private readonly object pendingNetworkEventsLock = new object();
        private int pendingNetworkBytes;
        private readonly object snapshotPumpLock = new object();
        private readonly object predictionLock = new object();
        private readonly object sequenceLock = new object();
        private readonly object diagnosticsLock = new object();
        private volatile PlayerSnapshot latestLocalSnapshot;
        private volatile bool sceneSnapshotSyncReady = true;
        private volatile bool localSnapshotsDuringBarrier;
        private System.Threading.Timer snapshotPumpTimer;
        private bool snapshotPumpEnabled;
        private long snapshotPumpLastTimestamp;
        private double snapshotPumpHeartbeatElapsed;
        private readonly Dictionary<TransportPeer, uint> playerIds = new Dictionary<TransportPeer, uint>();
        private readonly Dictionary<TransportPeer, float> pendingPlayerHandshakes = new Dictionary<TransportPeer, float>();
        private readonly Dictionary<uint, string> playerNames = new Dictionary<uint, string>();
        private readonly Dictionary<uint, string> sessionTokenByPlayer = new Dictionary<uint, string>();
        private readonly Dictionary<uint, PlayerSnapshot> players = new Dictionary<uint, PlayerSnapshot>();
        private readonly Dictionary<uint, List<PlayerSnapshot>> playerSnapshotHistory = new Dictionary<uint, List<PlayerSnapshot>>();
        private readonly Dictionary<uint, ulong> authorizedWeaponUnlocks = new Dictionary<uint, ulong>();
        private readonly HashSet<uint> authoritativeWeaponInventoryInitialized = new HashSet<uint>();
        private readonly HashSet<uint> authoritativeVitalsInitialized = new HashSet<uint>();
        private readonly Dictionary<uint, uint> lastSnapshotTickByPlayer = new Dictionary<uint, uint>();
        private readonly Dictionary<uint, float> lastAcceptedMovementAt = new Dictionary<uint, float>();
        private readonly Dictionary<uint, float> respawnMovementGraceUntil = new Dictionary<uint, float>();
        private readonly Dictionary<uint, Vector3> predictedLocalPositionsByTick = new Dictionary<uint, Vector3>();
        private readonly Dictionary<uint, PlayerLifecycleUpdate> latestLifecycleByPlayer = new Dictionary<uint, PlayerLifecycleUpdate>();
        private readonly Dictionary<uint, uint> lifecycleEventTickByPlayer = new Dictionary<uint, uint>();
        private readonly List<uint> disconnectedRemotePlayerIds = new List<uint>(4);
        private readonly List<TransportPeer> expiredHandshakePeers = new List<TransportPeer>(4);
        private readonly ReconnectReservationStore reconnectReservations = new ReconnectReservationStore();
        private readonly List<uint> expiredReconnectPlayerIds = new List<uint>(4);
        private readonly SnapshotFreshnessTracker snapshotFreshness = new SnapshotFreshnessTracker();
        private readonly Dictionary<TransportPeer, int> lastLatencyByPeer = new Dictionary<TransportPeer, int>();
        private readonly LatencyWindow latencyWindow = new LatencyWindow(128);
        private readonly List<LiteNetPeer> peerScratch = new List<LiteNetPeer>(4);
        private readonly Dictionary<LiteNetPeer, TransportPeer> litePeerMap = new Dictionary<LiteNetPeer, TransportPeer>();
        private readonly Dictionary<int, TransportPeer> relayPeerMap = new Dictionary<int, TransportPeer>();
        private readonly List<DiscoveredLobby> discoveredLobbies = new List<DiscoveredLobby>();
        private UnityRelayTransport relayTransport;
        private LiteNetManager manager;
        private volatile TransportPeer hostPeer;
        private string relayJoinCode;
        private bool relayTransportActive;
        private volatile bool isHost;
        private bool isDiscovering;
        private int maxPlayers = 4;
        private uint nextPlayerId = 2;
        private uint sequence;
        private volatile uint snapshotTick;
        private float diagnosticsAccumulator;
        private float networkTimeSeconds;
        private long sentBytesWindow;
        private long receivedBytesWindow;
        private long sentPacketsWindow;
        private long receivedPacketsWindow;
        private long sentSnapshotsWindow;
        private long receivedSnapshotsWindow;
        private long lostSnapshots;
        private long observedSnapshots;
        private float jitterEwma;
        private string localPlayerName = "Player";
        private string connectionKey = DefaultConnectionKey;
        private string clientSessionToken;
        private bool requiresPassword;
        private string clientAddress;
        private int clientPort;
        private bool reconnectEnabled;
        private bool clientEverWelcomed;
        private bool connectAttemptPending;
        private int reconnectAttempts;
        private float reconnectDelayTimer;
        private uint localPlayerId = 1;
        private Vector3 currentPosition;
        private Quaternion currentRotation = Quaternion.identity;
        private Vector3 currentVelocity;
        private byte currentMovementFlags;
        private byte currentWeaponSlot = 1;
        private byte currentWeaponVariation;
        private string currentWeaponName = string.Empty;
        private ulong currentWeaponInventoryMask;
        private ushort currentHealth = 100;
        private Quaternion currentViewRotation = Quaternion.identity;
        private uint currentPrimaryColor = PlayerAppearanceColor.DefaultPrimary;
        private uint currentSecondaryColor = PlayerAppearanceColor.DefaultSecondary;
        private uint currentLightColor = PlayerAppearanceColor.DefaultLight;
        private byte currentGlowIntensity = PlayerAppearanceColor.DefaultGlow;

        public bool IsRunning { get; private set; }
        public bool IsRelaySession { get { return relayTransportActive; } }
        public bool IsDiscovering { get { return IsRunning && isDiscovering; } }
        public bool IsHost { get { return IsRunning && isHost; } }
        public bool IsConnected { get; private set; }
        public bool IsReconnecting { get { return !isHost && reconnectEnabled && clientEverWelcomed && !IsConnected; } }
        public bool CanRetryConnection
        {
            get { return IsRunning && !isHost && !isDiscovering && !IsConnected && !clientEverWelcomed &&
                !connectAttemptPending && hostPeer == null; }
        }
        public int ReconnectAttempts { get { return reconnectAttempts; } }
        public float NetworkTimeSeconds { get { return networkTimeSeconds; } }
        public uint LocalPlayerId { get { return localPlayerId; } }
        public int ListenPort { get; private set; }
        public ushort MaximumPlayerHealth { get; set; } = 100;
        public string LastError { get; private set; }
        public string RelayJoinCode { get { return relayJoinCode; } }
        public int PingMilliseconds
        {
            get
            {
                if (hostPeer != null) return hostPeer.Ping;
                if (!isHost || lastLatencyByPeer.Count == 0) return 0;
                var total = 0;
                foreach (var latency in lastLatencyByPeer.Values) total += latency;
                return total / lastLatencyByPeer.Count;
            }
        }
        private int ConnectedPeerCount { get { return relayTransportActive ? (relayTransport != null ? relayTransport.PeerCount : 0) : (manager != null ? manager.ConnectedPeersCount : 0); } }
        public int Rtt95Milliseconds { get { return latencyWindow.Percentile95; } }
        public float JitterMilliseconds { get { return jitterEwma; } }
        public float PacketLossPercent { get { return observedSnapshots + lostSnapshots == 0 ? 0f : 100f * lostSnapshots / (observedSnapshots + lostSnapshots); } }
        public float SnapshotSendRate { get; private set; }
        public float SnapshotReceiveRate { get; private set; }
        public float PacketSendRate { get; private set; }
        public float PacketReceiveRate { get; private set; }
        public float SentBytesPerSecond { get; private set; }
        public float ReceivedBytesPerSecond { get; private set; }
        public long TotalBytesSent { get; private set; }
        public long TotalBytesReceived { get; private set; }
        public ushort LocalHealth { get { return currentHealth; } }
        public byte CurrentWeaponSlot { get { return currentWeaponSlot; } }
        public byte CurrentWeaponVariation { get { return currentWeaponVariation; } }

        public void SetLocalAppearance(uint primaryColor, uint secondaryColor, uint lightColor, byte glowIntensity)
        {
            currentPrimaryColor = primaryColor & 0xFFFFFF;
            currentSecondaryColor = secondaryColor & 0xFFFFFF;
            currentLightColor = lightColor & 0xFFFFFF;
            currentGlowIntensity = glowIntensity;
        }
        public IReadOnlyList<DiscoveredLobby> DiscoveredLobbies { get { return discoveredLobbies; } }
        public string LobbyName { get; set; } = "Bloodlink Room";
        public bool IsPublic { get; set; } = true;
        public IReadOnlyDictionary<uint, PlayerSnapshot> Players { get { return players; } }
        public uint LocalSnapshotTick { get { return snapshotTick; } }
        public bool HasFreshPlayerSnapshot(uint playerId, float maximumAgeSeconds)
        {
            return snapshotFreshness.IsFresh(playerId, networkTimeSeconds, maximumAgeSeconds);
        }
        public void AuthorizeWeaponUnlock(uint playerId, byte slot, byte variation)
        {
            if (!isHost || playerId == 0 || slot == 0 || slot > WeaponInventoryPolicy.Slots ||
                variation >= WeaponInventoryPolicy.VariationsPerSlot) return;
            ulong mask;
            authorizedWeaponUnlocks.TryGetValue(playerId, out mask);
            authorizedWeaponUnlocks[playerId] = WeaponInventoryPolicy.Add(mask, slot, variation);
        }
        public bool TryGetPlayerSnapshotAtTick(uint playerId, uint tick, out PlayerSnapshot snapshot)
        {
            return TryGetPlayerSnapshotAtTick(playerId, tick, 4, out snapshot);
        }
        public bool TryGetPlayerSnapshotAtTick(uint playerId, uint tick, int maximumTickDistance, out PlayerSnapshot snapshot)
        {
            snapshot = null;
            if (maximumTickDistance < 0) return false;
            List<PlayerSnapshot> history;
            if (!playerSnapshotHistory.TryGetValue(playerId, out history) || history.Count == 0) return false;
            var bestDelta = int.MaxValue;
            foreach (var candidate in history)
            {
                var delta = Math.Abs(unchecked((int)(tick - candidate.Tick)));
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    snapshot = candidate;
                }
            }
            if (bestDelta > maximumTickDistance) { snapshot = null; return false; }
            snapshot = snapshot.Clone();
            return true;
        }
        public float EstimateOneWayLatencySeconds(uint playerId)
        {
            if (!isHost || playerId == 0) return 0f;
            foreach (var pair in playerIds)
                if (pair.Value == playerId) return Mathf.Clamp(pair.Key.Ping * 0.0005f, 0f, 0.25f);
            return 0f;
        }
        public event Action<PlayerSnapshot> PlayerJoined;
        public event Action<uint> PlayerLeft;
        public event Action<PlayerLifecycleUpdate> PlayerLifecycleChanged;
        public event Action<PlayerSnapshot, Vector3> LocalPositionReconciled;
        public bool SceneSnapshotSyncReady { get { return sceneSnapshotSyncReady; } }
        public event Action<string> StatusChanged;
        public event Action<uint, MessageType, byte[]> MessageReceived;

        internal static bool TryParseIPv4BindAddress(string requestedAddress, out IPAddress address)
        {
            address = null;
            IPAddress parsed;
            if (string.IsNullOrWhiteSpace(requestedAddress) || !IPAddress.TryParse(requestedAddress.Trim(), out parsed) ||
                parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
            address = parsed;
            return true;
        }

        public void StartHost(int port, int playerLimit, string playerName, string password = "", bool isPublic = true,
            string bindAddress = "0.0.0.0")
        {
            Stop();
            LastError = null;
            if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            IPAddress bindIPv4;
            if (!TryParseIPv4BindAddress(bindAddress, out bindIPv4))
                throw new ArgumentException("Enter a valid IPv4 bind address, or 0.0.0.0 to listen on all adapters.", nameof(bindAddress));
            maxPlayers = Math.Max(2, Math.Min(4, playerLimit));
            localPlayerName = SanitizeName(playerName, "Host");
            connectionKey = BuildConnectionKey(password);
            requiresPassword = !string.IsNullOrEmpty(password);
            IsPublic = isPublic;
            isHost = true;
            localPlayerId = 1;
            ListenPort = port;
            manager = new LiteNetManager(this) { BroadcastReceiveEnabled = true, UnconnectedMessagesEnabled = true, UpdateTime = 5 };
            if (!manager.Start(bindIPv4, IPAddress.IPv6Any, port))
            {
                manager = null;
                throw new InvalidOperationException("Could not bind UDP " + bindIPv4 + ":" + port + ". Check that this IPv4 address belongs to an active adapter, the port is free, and firewall rules allow it.");
            }
            players[localPlayerId] = new PlayerSnapshot { PlayerId = localPlayerId, PlayerName = localPlayerName };
            IsRunning = true;
            IsConnected = true;
            StartSnapshotPump();
            SetStatus("Hosting LiteNetLib UDP session on port " + port);
        }

        public void StartClient(string address, int port, string playerName, string password = "")
        {
            if (isHost) throw new InvalidOperationException("Stop hosting before connecting to another room.");
            if (IsRunning && !isDiscovering)
                throw new InvalidOperationException("A connection is already active or in progress. Disconnect before joining another room.");
            if (!isDiscovering) Stop();
            LastError = null;
            if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            if (string.IsNullOrWhiteSpace(address)) throw new ArgumentException("Enter the host IP address.", nameof(address));
            localPlayerName = SanitizeName(playerName, "Player");
            clientSessionToken = CreateSessionToken();
            connectionKey = BuildConnectionKey(password);
            requiresPassword = !string.IsNullOrEmpty(password);
            clientAddress = address.Trim();
            clientPort = port;
            reconnectEnabled = true;
            clientEverWelcomed = false;
            connectAttemptPending = true;
            reconnectAttempts = 0;
            reconnectDelayTimer = 0f;
            isHost = false;
            ListenPort = 0;
            isDiscovering = false;
            if (manager == null)
            {
                manager = new LiteNetManager(this) { BroadcastReceiveEnabled = true, UnconnectedMessagesEnabled = true, UpdateTime = 5 };
                if (!manager.Start(0))
                {
                    manager = null;
                    throw new InvalidOperationException("Could not open the local UDP socket.");
                }
            }
            IsRunning = true;
            try
            {
                manager.Connect(clientAddress, clientPort, connectionKey);
                StartSnapshotPump();
                SetStatus("Connecting to " + address + ":" + port);
            }
            catch
            {
                Stop();
                throw;
            }
        }

        public async Task<string> StartRelayHostAsync(string projectId, int playerLimit, string playerName)
        {
            Stop();
            LastError = null;
            maxPlayers = Math.Max(2, Math.Min(4, playerLimit));
            localPlayerName = SanitizeName(playerName, "Host");
            connectionKey = DefaultConnectionKey;
            requiresPassword = false;
            IsPublic = false;
            isHost = true;
            relayTransportActive = true;
            localPlayerId = 1;
            ListenPort = 0;
            relayTransport = new UnityRelayTransport();
            AttachRelayEvents(relayTransport);
            players[localPlayerId] = new PlayerSnapshot { PlayerId = localPlayerId, PlayerName = localPlayerName };
            IsRunning = true;
            IsConnected = true;
            StartSnapshotPump();
            SetStatus("Signing in and creating a Unity Relay allocation...");
            try
            {
                relayJoinCode = await relayTransport.StartHostAsync(projectId, maxPlayers);
                if (!IsRunning) throw new InvalidOperationException("Unity Relay hosting was cancelled before allocation completed.");
                SetStatus("Hosting Unity Relay room. Join code: " + relayJoinCode);
                return relayJoinCode;
            }
            catch (Exception ex)
            {
                LastError = "Unity Relay host setup failed: " + ex.Message;
                Stop();
                throw;
            }
        }

        public async Task StartRelayClientAsync(string projectId, string joinCode, string playerName)
        {
            if (isHost) throw new InvalidOperationException("Stop hosting before joining a Unity Relay room.");
            if (IsRunning && !isDiscovering)
                throw new InvalidOperationException("A connection is already active or in progress. Disconnect before joining another room.");
            Stop();
            LastError = null;
            if (string.IsNullOrWhiteSpace(joinCode)) throw new ArgumentException("Enter the Unity Relay join code.", nameof(joinCode));
            localPlayerName = SanitizeName(playerName, "Player");
            clientSessionToken = CreateSessionToken();
            connectionKey = DefaultConnectionKey;
            requiresPassword = false;
            clientEverWelcomed = false;
            connectAttemptPending = true;
            reconnectEnabled = false;
            isHost = false;
            relayTransportActive = true;
            ListenPort = 0;
            relayTransport = new UnityRelayTransport();
            AttachRelayEvents(relayTransport);
            IsRunning = true;
            StartSnapshotPump();
            SetStatus("Signing in and joining Unity Relay...");
            try
            {
                await relayTransport.StartClientAsync(projectId, joinCode);
                if (!IsRunning) throw new InvalidOperationException("Unity Relay join was cancelled before connection completed.");
            }
            catch (Exception ex)
            {
                LastError = "Unity Relay join failed: " + ex.Message;
                Stop();
                throw;
            }
        }

        private void AttachRelayEvents(UnityRelayTransport transport)
        {
            transport.PeerConnected += id => EnqueueNetworkEvent(() => HandlePeerConnected(GetRelayPeer(id)));
            transport.PeerDisconnected += id => EnqueueNetworkEvent(() => HandlePeerDisconnected(GetRelayPeer(id), "Relay connection closed."));
            transport.PacketReceived += (id, bytes) =>
            {
                TransportPeer peer;
                if (!relayPeerMap.TryGetValue(id, out peer)) return;
                EnqueueNetworkEvent(() => Process(peer, bytes));
            };
        }

        private TransportPeer GetRelayPeer(int id)
        {
            TransportPeer peer;
            if (!relayPeerMap.TryGetValue(id, out peer))
            {
                peer = new TransportPeer { RelayPeerId = id, Address = "Unity Relay player " + id,
                    DisconnectAction = () => { if (relayTransport != null) relayTransport.Disconnect(id); } };
                relayPeerMap[id] = peer;
            }
            return peer;
        }

        private TransportPeer GetLitePeer(LiteNetPeer peer)
        {
            if (peer == null) return null;
            TransportPeer wrapped;
            if (!litePeerMap.TryGetValue(peer, out wrapped))
            {
                wrapped = new TransportPeer { LitePeer = peer, Address = peer.Address.ToString() };
                litePeerMap[peer] = wrapped;
            }
            return wrapped;
        }

        public void Update(float deltaTime, Vector3 localPosition, Quaternion localRotation, Vector3 localVelocity)
        {
            Update(deltaTime, localPosition, localRotation, localVelocity, 0, 1, 0, 100);
        }

        public void Update(float deltaTime, Vector3 localPosition, Quaternion localRotation, Vector3 localVelocity, byte movementFlags, byte weaponSlot, byte weaponVariation, int health = 100)
        {
            Update(deltaTime, localPosition, localRotation, localVelocity, movementFlags, weaponSlot, weaponVariation, health, localRotation);
        }

        public void Update(float deltaTime, Vector3 localPosition, Quaternion localRotation, Vector3 localVelocity, byte movementFlags, byte weaponSlot, byte weaponVariation, int health, Quaternion viewRotation, string weaponName = null, ulong weaponInventoryMask = 0, bool hasLocalPlayer = true, int animatorStateHash = 0, float animatorNormalizedTime = 0f, bool weaponAlternate = false)
        {
            if (!IsRunning || (!relayTransportActive && manager == null)) return;
            try
            {
                // LiteNetLib delivers listener callbacks only when PollEvents is
                // called unless UnsyncedEvents is enabled. Keep callbacks on the
                // Unity thread, then drain our bounded queue below.
                if (relayTransportActive) relayTransport?.Update();
                else manager.PollEvents();
            }
            catch (Exception ex)
            {
                LastError = "Network event polling failed: " + ex.Message;
            }
            DrainPendingNetworkEvents();
            networkTimeSeconds += Math.Max(0f, deltaTime);
            if (relayTransportActive && snapshotPumpTimer == null) PumpNetworkSnapshots(null);
            if (isHost)
            {
                RemoveExpiredReconnectReservations();
                RemoveExpiredPlayerHandshakes();
            }
            UpdateReconnect(deltaTime);
            currentPosition = localPosition;
            currentRotation = localRotation;
            currentVelocity = localVelocity;
            currentMovementFlags = movementFlags;
            currentWeaponSlot = weaponSlot;
            currentWeaponVariation = weaponVariation;
            currentWeaponName = weaponName ?? string.Empty;
            currentWeaponInventoryMask = weaponInventoryMask;
            currentHealth = (ushort)Math.Max(0, Math.Min(200, health));
            currentViewRotation = viewRotation;

            var local = new PlayerSnapshot
            {
                PlayerId = localPlayerId,
                PlayerName = localPlayerName,
                Position = currentPosition,
                Rotation = currentRotation,
                ViewRotation = currentViewRotation,
                Velocity = currentVelocity,
                MovementFlags = currentMovementFlags,
                WeaponSlot = currentWeaponSlot,
                WeaponVariation = currentWeaponVariation,
                WeaponAlternate = weaponAlternate,
                WeaponName = currentWeaponName,
                WeaponInventoryMask = currentWeaponInventoryMask,
                Health = currentHealth,
                // The first Unity Update can race the timer's first pump. Tick zero
                // is reserved as invalid on the wire, so seed the initial snapshot.
                Tick = snapshotTick == 0 ? 1u : snapshotTick,
                PrimaryColor = currentPrimaryColor,
                SecondaryColor = currentSecondaryColor,
                LightColor = currentLightColor,
                GlowIntensity = currentGlowIntensity,
                AnimatorStateHash = animatorStateHash,
                AnimatorNormalizedTime = Mathf.Clamp(animatorNormalizedTime, 0f, 0.9999f)
            };
            if (!hasLocalPlayer || (!sceneSnapshotSyncReady && !localSnapshotsDuringBarrier))
            {
                UpdateDiagnostics(deltaTime);
                return;
            }
            if (isHost) authoritativeVitalsInitialized.Add(localPlayerId);
            else if (!isHost && sceneSnapshotSyncReady)
            {
                PlayerSnapshot authoritativeLocal;
                if (players.TryGetValue(localPlayerId, out authoritativeLocal))
                {
                    local.Health = authoritativeLocal.Health;
                    local.MovementFlags = (byte)((local.MovementFlags & ~4) | (authoritativeLocal.MovementFlags & 4));
                }
            }
            PlayerSnapshot previousLocal;
            var localLifecycleChanged = isHost && players.TryGetValue(localPlayerId, out previousLocal) && HasLifecycleChange(previousLocal, local);
            players[localPlayerId] = local;
            if (localLifecycleChanged) BroadcastPlayerLifecycle(local);
            System.Threading.Interlocked.Exchange(ref latestLocalSnapshot, local);
            UpdateDiagnostics(deltaTime);
        }

        private void StartSnapshotPump()
        {
            lock (snapshotPumpLock)
            {
                snapshotPumpEnabled = true;
                snapshotPumpHeartbeatElapsed = 0d;
                snapshotPumpLastTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                if (snapshotPumpTimer != null) snapshotPumpTimer.Dispose();
                snapshotPumpTimer = relayTransportActive ? null : new System.Threading.Timer(PumpNetworkSnapshots, null, 0, 16);
            }
        }

        private void PumpNetworkSnapshots(object state)
        {
            lock (snapshotPumpLock)
            {
                if (!snapshotPumpEnabled || !IsRunning || (!relayTransportActive && manager == null)) return;
                try
                {
                    var now = System.Diagnostics.Stopwatch.GetTimestamp();
                    var elapsed = (now - snapshotPumpLastTimestamp) / (double)System.Diagnostics.Stopwatch.Frequency;
                    snapshotPumpLastTimestamp = now;
                    snapshotPumpHeartbeatElapsed += Math.Max(0d, elapsed);
                    if (snapshotPumpHeartbeatElapsed >= 2d)
                    {
                        snapshotPumpHeartbeatElapsed %= 2d;
                        var ping = BitConverter.GetBytes(Environment.TickCount);
                        if (isHost)
                        {
                            if (relayTransportActive) BroadcastUnreliable(MessageType.Ping, ping);
                            else
                            {
                                manager.GetConnectedPeers(peerScratch);
                                foreach (var peer in peerScratch) Send(GetLitePeer(peer), MessageType.Ping, ping, DeliveryMethod.Unreliable);
                                peerScratch.Clear();
                            }
                        }
                        else if (IsConnected && hostPeer != null)
                        {
                            Send(hostPeer, MessageType.Ping, ping, DeliveryMethod.Unreliable);
                        }
                    }

                    if (!sceneSnapshotSyncReady && !localSnapshotsDuringBarrier) return;
                    var latest = System.Threading.Interlocked.CompareExchange(ref latestLocalSnapshot, null, null);
                    if (latest == null) return;
                    var snapshot = latest.Clone();
                    unchecked { snapshotTick++; }
                    if (snapshotTick == 0) snapshotTick = 1;
                    snapshot.Tick = snapshotTick;
                    if (isHost)
                    {
                        if (ConnectedPeerCount > 0)
                        {
                            lock (diagnosticsLock) sentSnapshotsWindow++;
                        }
                        BroadcastSnapshot(snapshot, null);
                    }
                    else if (IsConnected && hostPeer != null)
                    {
                        RecordLocalPrediction(snapshot.Tick, snapshot.Position);
                        Send(hostPeer, MessageType.PlayerSnapshot, snapshot.Encode(), DeliveryMethod.Sequenced);
                        lock (diagnosticsLock) sentSnapshotsWindow++;
                    }
                }
                catch (Exception ex)
                {
                    LastError = "Snapshot network pump failed: " + ex.Message;
                }
            }
        }

        public void SetSceneSnapshotSyncReady(bool ready)
        {
            lock (snapshotPumpLock)
            {
                sceneSnapshotSyncReady = ready;
                if (ready) localSnapshotsDuringBarrier = false;
                if (!ready)
                {
                    localSnapshotsDuringBarrier = false;
                    lastAcceptedMovementAt.Clear();
                }
                System.Threading.Interlocked.Exchange(ref latestLocalSnapshot, null);
                lock (predictionLock) predictedLocalPositionsByTick.Clear();
            }
        }

        public void SetLocalSnapshotsDuringBarrier(bool enabled)
        {
            localSnapshotsDuringBarrier = enabled;
        }

        private void RecordLocalPrediction(uint tick, Vector3 position)
        {
            lock (predictionLock)
            {
                predictedLocalPositionsByTick[tick] = position;
                if (predictedLocalPositionsByTick.Count <= 180) return;
                var staleTicks = new List<uint>();
                foreach (var savedTick in predictedLocalPositionsByTick.Keys)
                    if (unchecked((int)(tick - savedTick)) > 120) staleTicks.Add(savedTick);
                foreach (var staleTick in staleTicks) predictedLocalPositionsByTick.Remove(staleTick);
            }
        }

        public void RecordAppliedPositionCorrection(Vector3 correction)
        {
            if (!IsFinite(correction)) return;
            lock (predictionLock)
            {
                // Acknowledge corrections already applied to the current player so
                // a batch of old snapshots cannot apply the same displacement again.
                var ticks = new List<uint>(predictedLocalPositionsByTick.Keys);
                foreach (var tick in ticks) predictedLocalPositionsByTick[tick] += correction;
            }
        }

        private void UpdateReconnect(float deltaTime)
        {
            if (isHost || !reconnectEnabled || !clientEverWelcomed || IsConnected || hostPeer != null || connectAttemptPending || manager == null) return;
            reconnectDelayTimer -= Math.Max(0f, deltaTime);
            if (reconnectDelayTimer > 0f) return;
            connectAttemptPending = true;
            reconnectAttempts++;
            try
            {
                manager.Connect(clientAddress, clientPort, connectionKey);
                SetStatus("Reconnecting to " + clientAddress + ":" + clientPort + " (attempt " + reconnectAttempts + ")");
            }
            catch (Exception ex)
            {
                connectAttemptPending = false;
                ScheduleReconnect("Reconnect attempt failed: " + ex.Message);
            }
        }

        private void ScheduleReconnect(string reason)
        {
            if (!reconnectEnabled || !clientEverWelcomed) return;
            reconnectDelayTimer = ReconnectBackoff.DelaySeconds(reconnectAttempts);
            SetStatus(reason + " Retrying in " + reconnectDelayTimer.ToString("0") + " s.");
        }

        private void RemoveExpiredReconnectReservations()
        {
            expiredReconnectPlayerIds.Clear();
            reconnectReservations.RemoveExpired(networkTimeSeconds, expiredReconnectPlayerIds);
            for (var i = 0; i < expiredReconnectPlayerIds.Count; i++)
            {
                var playerId = expiredReconnectPlayerIds[i];
                authorizedWeaponUnlocks.Remove(playerId);
                authoritativeWeaponInventoryInitialized.Remove(playerId);
                authoritativeVitalsInitialized.Remove(playerId);
                latestLifecycleByPlayer.Remove(playerId);
                lifecycleEventTickByPlayer.Remove(playerId);
            }
            expiredReconnectPlayerIds.Clear();
        }

        public void DiscoverLan(int port)
        {
            if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            if (isHost) throw new InvalidOperationException("The host cannot search for rooms while hosting.");
            if (IsRunning && !isDiscovering)
                throw new InvalidOperationException("Disconnect from the current session before searching for LAN rooms.");
            if (manager == null)
            {
                manager = new LiteNetManager(this) { BroadcastReceiveEnabled = true, UnconnectedMessagesEnabled = true, UpdateTime = 5 };
                if (!manager.Start(0)) { manager = null; throw new InvalidOperationException("Could not open UDP for LAN discovery."); }
                IsRunning = true;
            }
            discoveredLobbies.Clear();
            isDiscovering = true;
            SendDiscoveryProbes(port);
            SetStatus("Searching for LAN rooms on UDP port " + port);
        }

        private void SendDiscoveryProbes(int port)
        {
            var frame = new NetworkMessage(MessageType.DiscoveryRequest, NextSequence(), Array.Empty<byte>()).Encode();
            manager.SendBroadcast(frame, port);
            CountSent(frame.Length, 1);
            // Limited broadcast may not loop back on Windows; this also supports a same-machine host.
            manager.SendUnconnectedMessage(frame, new IPEndPoint(IPAddress.Loopback, port));
            CountSent(frame.Length, 1);
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up) continue;
                IPInterfaceProperties properties;
                try { properties = adapter.GetIPProperties(); }
                catch { continue; }
                foreach (var address in properties.UnicastAddresses)
                {
                    if (address.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || address.IPv4Mask == null) continue;
                    var ipBytes = address.Address.GetAddressBytes();
                    var maskBytes = address.IPv4Mask.GetAddressBytes();
                    var broadcastBytes = new byte[4];
                    for (var i = 0; i < broadcastBytes.Length; i++) broadcastBytes[i] = (byte)(ipBytes[i] | (byte)~maskBytes[i]);
                    var broadcastAddress = new IPAddress(broadcastBytes);
                    if (IPAddress.IsLoopback(address.Address)) continue;
                    manager.SendUnconnectedMessage(frame, new IPEndPoint(broadcastAddress, port));
                    CountSent(frame.Length, 1);
                }
            }
        }

        public void JoinDiscovered(DiscoveredLobby lobby, string playerName, string password = "")
        {
            if (lobby == null) throw new ArgumentNullException(nameof(lobby));
            StartClient(lobby.Address, lobby.Port, playerName, password);
        }

        public bool RequestReconnect()
        {
            if (isHost || !reconnectEnabled || !clientEverWelcomed || !IsConnected || hostPeer == null) return false;
            var peer = hostPeer;
            hostPeer = null;
            IsConnected = false;
            connectAttemptPending = false;
            ScheduleReconnect("Reconnecting to the current host.");
            if (peer.LitePeer != null) peer.LitePeer.Disconnect();
            return true;
        }

        public bool SendToHost(MessageType type, byte[] payload)
        {
            if (isHost || !IsConnected || hostPeer == null) return false;
            Send(hostPeer, type, payload ?? Array.Empty<byte>(), DeliveryMethod.ReliableOrdered);
            return true;
        }

        public bool SendUnreliableToHost(MessageType type, byte[] payload)
        {
            if (isHost || !IsConnected || hostPeer == null) return false;
            Send(hostPeer, type, payload ?? Array.Empty<byte>(), DeliveryMethod.Unreliable);
            return true;
        }

        public void SendToPlayer(uint playerId, MessageType type, byte[] payload)
        {
            if (!isHost || (!relayTransportActive && manager == null)) return;
            foreach (var pair in playerIds)
                if (pair.Value == playerId) { Send(pair.Key, type, payload ?? Array.Empty<byte>(), DeliveryMethod.ReliableOrdered); return; }
        }

        public void BroadcastReliable(MessageType type, byte[] payload)
        {
            if (!isHost || (!relayTransportActive && manager == null)) return;
            var frame = Frame(type, payload ?? Array.Empty<byte>());
            CountSent(frame.Length, ConnectedPeerCount);
            if (relayTransportActive) relayTransport.SendToAll(frame, IsCombatMessage(type) ? DeliveryMethod.ReliableUnordered : DeliveryMethod.ReliableOrdered);
            else manager.SendToAll(frame, IsCombatMessage(type) ? DeliveryMethod.ReliableUnordered : DeliveryMethod.ReliableOrdered);
            if (!relayTransportActive && IsImmediateMessage(type)) manager.TriggerUpdate();
        }

        public void BroadcastUnreliable(MessageType type, byte[] payload)
        {
            if (!isHost || (!relayTransportActive && manager == null)) return;
            var frame = Frame(type, payload ?? Array.Empty<byte>());
            CountSent(frame.Length, ConnectedPeerCount);
            if (relayTransportActive) relayTransport.SendToAll(frame, DeliveryMethod.Unreliable);
            else manager.SendToAll(frame, DeliveryMethod.Unreliable);
        }

        private void Process(TransportPeer peer, byte[] bytes)
        {
            try
            {
                var message = NetworkMessage.Decode(bytes);
                if (isHost) ProcessHost(peer, message);
                else ProcessClient(peer, message);
            }
            catch (Exception ex)
            {
                LastError = "Rejected packet: " + ex.Message;
            }
        }

        private void ProcessHost(TransportPeer peer, NetworkMessage message)
        {
            uint senderId;
            var knownPeer = playerIds.TryGetValue(peer, out senderId);
            if (!knownPeer) return;
            if (message.Type == MessageType.Hello)
            {
                if (sessionTokenByPlayer.ContainsKey(senderId)) return;
                pendingPlayerHandshakes.Remove(peer);
                var hello = SessionHandshakeCodec.DecodeHello(message.Payload);
                var name = SanitizeName(hello.PlayerName, "Player " + senderId);
                RemoveExpiredReconnectReservations();
                ReconnectReservation reservation;
                var hasReservation = reconnectReservations.TryTake(hello.SessionToken, networkTimeSeconds, out reservation);
                var otherPeerCount = Math.Max(0, playerIds.Count - 1); // Exclude this provisional handshake peer.
                var reservedPeerCapacity = otherPeerCount + 1 + reconnectReservations.Count;
                if (reservedPeerCapacity > maxPlayers - 1)
                {
                    if (hasReservation)
                        reconnectReservations.Store(hello.SessionToken, reservation.PlayerId, reservation.PlayerName,
                            reservation.Snapshot, networkTimeSeconds + ReconnectReservationSeconds);
                    peer.Disconnect();
                    return;
                }
                if (hasReservation && IsPlayerIdConnected(reservation.PlayerId))
                {
                    reconnectReservations.Store(hello.SessionToken, reservation.PlayerId, reservation.PlayerName,
                        reservation.Snapshot, networkTimeSeconds + ReconnectReservationSeconds);
                    if (peer.LitePeer != null) peer.LitePeer.Disconnect();
                    return;
                }
                if (hasReservation)
                {
                    playerIds.Remove(peer);
                    playerNames.Remove(senderId);
                    players.Remove(senderId);
                    authorizedWeaponUnlocks.Remove(senderId);
                authoritativeWeaponInventoryInitialized.Remove(senderId);
                    authoritativeVitalsInitialized.Remove(senderId);
                    lastSnapshotTickByPlayer.Remove(senderId);
                    senderId = reservation.PlayerId;
                    playerIds[peer] = senderId;
                    playerNames[senderId] = name;
                    var restored = reservation.Snapshot.Clone();
                    restored.PlayerId = senderId;
                    restored.PlayerName = name;
                    players[senderId] = restored;
                    authoritativeVitalsInitialized.Add(senderId);
                    authoritativeWeaponInventoryInitialized.Add(senderId);
                }
                else
                {
                    playerNames[senderId] = name;
                    players[senderId] = new PlayerSnapshot { PlayerId = senderId, PlayerName = name };
                    authoritativeVitalsInitialized.Remove(senderId);
                    authoritativeWeaponInventoryInitialized.Remove(senderId);
                }
                sessionTokenByPlayer[senderId] = hello.SessionToken;
                Send(peer, MessageType.Welcome, EncodeWelcome(senderId, maxPlayers), DeliveryMethod.ReliableOrdered);
                foreach (var player in players.Values) Send(peer, MessageType.PlayerSnapshot, player.Encode(), DeliveryMethod.ReliableOrdered);
                PlayerJoined?.Invoke(players[senderId]);
                SetStatus(hasReservation ? name + " reconnected as player " + senderId + "." : name + " connected.");
            }
            else if (message.Type == MessageType.PlayerSnapshot)
            {
                if (!sceneSnapshotSyncReady && !localSnapshotsDuringBarrier) return;
                var snapshot = PlayerSnapshot.Decode(message.Payload);
                if (!ObserveSnapshot(senderId, snapshot.Tick)) return;
                snapshotFreshness.Record(senderId, networkTimeSeconds);
                receivedSnapshotsWindow++;
                snapshot.PlayerId = senderId;
                snapshot.PlayerName = playerNames.ContainsKey(senderId) ? playerNames[senderId] : "Player " + senderId;
                snapshot.Health = (ushort)Math.Min(snapshot.Health, MaximumPlayerHealth);
                PlayerSnapshot previous;
                var hadPrevious = players.TryGetValue(senderId, out previous);
                if (authoritativeVitalsInitialized.Contains(senderId) && hadPrevious)
                {
                    snapshot.Health = previous.Health;
                    snapshot.MovementFlags = (byte)((snapshot.MovementFlags & ~4) | (previous.MovementFlags & 4));
                }
                else
                {
                    authoritativeVitalsInitialized.Add(senderId);
                }
                var weaponInventoryInitialized = authoritativeWeaponInventoryInitialized.Contains(senderId);
                ApplyWeaponInventoryAuthority(senderId, snapshot, previous, weaponInventoryInitialized);
                authoritativeWeaponInventoryInitialized.Add(senderId);
                var lifecycleChanged = hadPrevious && HasLifecycleChange(previous, snapshot);
                if (hadPrevious) ApplyMovementAuthority(snapshot, previous, senderId);
                lastAcceptedMovementAt[senderId] = networkTimeSeconds;
                players[senderId] = snapshot;
                RecordPlayerSnapshot(senderId, snapshot);
                if (lifecycleChanged) BroadcastPlayerLifecycle(snapshot);
                // Echo the accepted authoritative state to the sending client for
                // prediction reconciliation as well as relaying it to other players.
                BroadcastSnapshot(snapshot, null);
            }
            else if (message.Type == MessageType.Ping)
            {
                Send(peer, MessageType.Pong, message.Payload, DeliveryMethod.Unreliable);
            }
            else
            {
                MessageReceived?.Invoke(senderId, message.Type, message.Payload);
            }
        }

        private void ProcessClient(TransportPeer peer, NetworkMessage message)
        {
            if (peer != hostPeer) return;
            if (message.Type == MessageType.Welcome)
            {
                using (var stream = new MemoryStream(message.Payload, false))
                using (var reader = new BinaryReader(stream))
                {
                    localPlayerId = reader.ReadUInt32();
                    maxPlayers = reader.ReadByte();
                    if (stream.Position != stream.Length) throw new InvalidDataException("Invalid welcome packet.");
                }
                players.Clear();
                playerSnapshotHistory.Clear();
                lastSnapshotTickByPlayer.Clear();
                snapshotFreshness.Clear();
                lock (predictionLock) predictedLocalPositionsByTick.Clear();
                latestLifecycleByPlayer.Clear();
                authoritativeVitalsInitialized.Clear();
                IsConnected = true;
                clientEverWelcomed = true;
                reconnectAttempts = 0;
                reconnectDelayTimer = 0f;
                connectAttemptPending = false;
                SetStatus("Connected as player " + localPlayerId);
            }
            else if (message.Type == MessageType.PlayerSnapshot)
            {
                var snapshot = PlayerSnapshot.Decode(message.Payload);
                if (!ObserveSnapshot(snapshot.PlayerId, snapshot.Tick)) return;
                snapshotFreshness.Record(snapshot.PlayerId, networkTimeSeconds);
                receivedSnapshotsWindow++;
                if (snapshot.PlayerId == localPlayerId && sceneSnapshotSyncReady)
                {
                    Vector3 predictedPosition;
                    lock (predictionLock)
                    {
                        if (predictedLocalPositionsByTick.TryGetValue(snapshot.Tick, out predictedPosition))
                        {
                            var correction = snapshot.Position - predictedPosition;
                            // A stale acknowledgement or a level-origin mismatch must
                            // never teleport the local player. Normal prediction drift
                            // is small; discard anything beyond the active play space.
                            if (IsFinite(correction) && correction.sqrMagnitude > 0.0001f &&
                                correction.sqrMagnitude <= MaximumLocalPositionCorrection * MaximumLocalPositionCorrection)
                                LocalPositionReconciled?.Invoke(snapshot, correction);
                            var acknowledgedTicks = new List<uint>();
                            foreach (var savedTick in predictedLocalPositionsByTick.Keys)
                                if (unchecked((int)(snapshot.Tick - savedTick)) >= 0) acknowledgedTicks.Add(savedTick);
                            foreach (var acknowledgedTick in acknowledgedTicks) predictedLocalPositionsByTick.Remove(acknowledgedTick);
                        }
                    }
                }
                PlayerLifecycleUpdate latestLifecycle;
                if (latestLifecycleByPlayer.TryGetValue(snapshot.PlayerId, out latestLifecycle))
                    PlayerLifecycleStatePolicy.ApplyToSnapshot(latestLifecycle, snapshot);
                var isNew = !players.ContainsKey(snapshot.PlayerId);
                players[snapshot.PlayerId] = snapshot;
                if (isNew && snapshot.PlayerId != localPlayerId) PlayerJoined?.Invoke(snapshot);
            }
            else if (message.Type == MessageType.PlayerLeft)
            {
                if (message.Payload.Length != 4) throw new InvalidDataException("Invalid player-left packet.");
                var id = BitConverter.ToUInt32(message.Payload, 0);
                latestLifecycleByPlayer.Remove(id);
                lastSnapshotTickByPlayer.Remove(id);
                snapshotFreshness.Remove(id);
                playerSnapshotHistory.Remove(id);
                lastAcceptedMovementAt.Remove(id);
                respawnMovementGraceUntil.Remove(id);
                authorizedWeaponUnlocks.Remove(id);
                authoritativeWeaponInventoryInitialized.Remove(id);
                if (players.Remove(id)) PlayerLeft?.Invoke(id);
            }
            else if (message.Type == MessageType.PlayerLifecycle)
            {
                var update = PlayerLifecycleCodec.Decode(message.Payload);
                PlayerLifecycleUpdate previousLifecycle;
                if (latestLifecycleByPlayer.TryGetValue(update.PlayerId, out previousLifecycle) &&
                    !SnapshotTickPolicy.IsNewer(update.SnapshotTick, previousLifecycle.SnapshotTick)) return;
                latestLifecycleByPlayer[update.PlayerId] = update;
                if (!update.IsDead && previousLifecycle != null && previousLifecycle.IsDead && update.PlayerId == localPlayerId)
                {
                    lock (predictionLock) predictedLocalPositionsByTick.Clear();
                }
                PlayerSnapshot player;
                if (players.TryGetValue(update.PlayerId, out player))
                {
                    player.Health = update.Health;
                    player.MovementFlags = update.IsDead ? (byte)(player.MovementFlags | 4) : (byte)(player.MovementFlags & ~4);
                }
                else
                {
                    players[update.PlayerId] = new PlayerSnapshot
                    {
                        PlayerId = update.PlayerId,
                        PlayerName = update.PlayerId == localPlayerId ? localPlayerName : "Player " + update.PlayerId,
                        Health = update.Health,
                        MovementFlags = update.IsDead ? (byte)4 : (byte)0
                    };
                }
                PlayerLifecycleChanged?.Invoke(update);
            }
            else if (message.Type == MessageType.Ping)
            {
                Send(peer, MessageType.Pong, message.Payload, DeliveryMethod.Unreliable);
            }
            else
            {
                MessageReceived?.Invoke(1, message.Type, message.Payload);
            }
        }

        private void BroadcastSnapshot(PlayerSnapshot snapshot, TransportPeer except)
        {
            if (!relayTransportActive && manager == null) return;
            var frame = Frame(MessageType.PlayerSnapshot, snapshot.Encode());
            var recipientCount = Math.Max(0, ConnectedPeerCount - (except == null ? 0 : 1));
            CountSent(frame.Length, recipientCount);
            if (relayTransportActive) relayTransport.SendToAll(frame, DeliveryMethod.Sequenced);
            else if (except == null) manager.SendToAll(frame, DeliveryMethod.Sequenced);
            else manager.SendToAll(frame, DeliveryMethod.Sequenced, except.LitePeer);
        }

        private void BroadcastPlayerLifecycle(PlayerSnapshot snapshot)
        {
            uint previousTick;
            var eventTick = snapshot.Tick;
            if (lifecycleEventTickByPlayer.TryGetValue(snapshot.PlayerId, out previousTick) &&
                !SnapshotTickPolicy.IsNewer(eventTick, previousTick))
                unchecked { eventTick = previousTick + 1; }
            if (eventTick == 0) eventTick = 1;
            lifecycleEventTickByPlayer[snapshot.PlayerId] = eventTick;
            BroadcastReliable(MessageType.PlayerLifecycle,
                PlayerLifecycleCodec.Encode(snapshot.PlayerId, snapshot.Health, (snapshot.MovementFlags & 4) != 0, eventTick));
        }

        private void ApplyMovementAuthority(PlayerSnapshot snapshot, PlayerSnapshot previous, uint playerId)
        {
            float respawnUntil;
            if (respawnMovementGraceUntil.TryGetValue(playerId, out respawnUntil))
            {
                if (networkTimeSeconds <= respawnUntil) return;
                respawnMovementGraceUntil.Remove(playerId);
            }
            float lastAcceptedAt;
            if (snapshot == null || previous == null || !lastAcceptedMovementAt.TryGetValue(playerId, out lastAcceptedAt)) return;
            var elapsed = Mathf.Clamp(networkTimeSeconds - lastAcceptedAt, 1f / 60f, 0.25f);
            var displacement = snapshot.Position - previous.Position;
            var maximumDistance = PlayerMovementPositionTolerance + MaximumPlayerMovementSpeed * elapsed;
            if (displacement.sqrMagnitude > maximumDistance * maximumDistance)
            {
                snapshot.Position = previous.Position + displacement.normalized * maximumDistance;
                snapshot.Velocity = displacement.normalized * Mathf.Min(MaximumPlayerMovementSpeed, maximumDistance / elapsed);
            }
            else if (snapshot.Velocity.sqrMagnitude > MaximumPlayerMovementSpeed * MaximumPlayerMovementSpeed)
            {
                snapshot.Velocity = snapshot.Velocity.normalized * MaximumPlayerMovementSpeed;
            }
        }

        private static bool IsFinite(Vector3 value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
                !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
                !float.IsNaN(value.z) && !float.IsInfinity(value.z);
        }

        private void ApplyWeaponInventoryAuthority(uint playerId, PlayerSnapshot snapshot, PlayerSnapshot previous, bool hadPrevious)
        {
            if (snapshot == null) return;

            if (hadPrevious && previous != null)
            {
                // Keep each player's accepted inventory independent. New bits must
                // have been authorized by a reachable campaign pickup.
                var previousMask = previous.WeaponInventoryMask;
                ulong allowedMask;
                authorizedWeaponUnlocks.TryGetValue(playerId, out allowedMask);
                var acceptedAdditions = (snapshot.WeaponInventoryMask & ~previousMask) & allowedMask;
                snapshot.WeaponInventoryMask = previousMask | acceptedAdditions;
                authorizedWeaponUnlocks[playerId] = allowedMask & ~snapshot.WeaponInventoryMask;
            }

            if (snapshot.WeaponSlot == 0 || WeaponInventoryPolicy.IsUnlocked(
                    snapshot.WeaponInventoryMask, snapshot.WeaponSlot, snapshot.WeaponVariation))
                return;

            // A player may only report an equipped weapon that exists in their own
            // accepted inventory. Fall back to that player's last accepted weapon;
            // on the first frame, use the unarmed state.
            if (hadPrevious && previous != null)
            {
                snapshot.WeaponSlot = previous.WeaponSlot;
                snapshot.WeaponVariation = previous.WeaponVariation;
                snapshot.WeaponName = previous.WeaponName;
            }
            else
            {
                snapshot.WeaponSlot = 0;
                snapshot.WeaponVariation = 0;
                snapshot.WeaponName = string.Empty;
            }
        }

        private void RecordPlayerSnapshot(uint playerId, PlayerSnapshot snapshot)
        {
            List<PlayerSnapshot> history;
            if (!playerSnapshotHistory.TryGetValue(playerId, out history))
            {
                history = new List<PlayerSnapshot>(16);
                playerSnapshotHistory[playerId] = history;
            }
            history.Add(snapshot.Clone());
            while (history.Count > 16) history.RemoveAt(0);
        }

        public void AllowPlayerReposition(uint playerId)
        {
            if (!IsHost || !players.ContainsKey(playerId)) return;
            respawnMovementGraceUntil[playerId] = networkTimeSeconds + 2f;
            playerSnapshotHistory.Remove(playerId);
        }

        public bool SetAuthoritativePlayerVitals(uint playerId, ushort health, bool isDead)
        {
            if (!IsHost || playerId == 0 || health > 200) return false;
            health = Math.Min(health, MaximumPlayerHealth);
            PlayerSnapshot player;
            if (!players.TryGetValue(playerId, out player) || player == null) return false;
            var previousDead = (player.MovementFlags & 4) != 0;
            if (player.Health == health && previousDead == isDead) return false;
            if (previousDead && !isDead)
            {
                // The next alive snapshot can be at a distant checkpoint, rather
                // than a movement-speed violation from the corpse's old position.
                AllowPlayerReposition(playerId);
            }
            player.Health = health;
            player.MovementFlags = isDead ? (byte)(player.MovementFlags | 4) : (byte)(player.MovementFlags & ~4);
            authoritativeVitalsInitialized.Add(playerId);
            BroadcastPlayerLifecycle(player);
            return true;
        }

        private static bool HasLifecycleChange(PlayerSnapshot previous, PlayerSnapshot current)
        {
            return previous.Health != current.Health || ((previous.MovementFlags ^ current.MovementFlags) & 4) != 0;
        }

        private uint NextSequence()
        {
            lock (sequenceLock) return sequence++;
        }

        private byte[] Frame(MessageType type, byte[] payload)
        {
            return new NetworkMessage(type, NextSequence(), payload).Encode();
        }

        private void Send(TransportPeer peer, MessageType type, byte[] payload, DeliveryMethod delivery)
        {
            if (peer == null) return;
            try
            {
                var frame = Frame(type, payload);
                var combat = delivery == DeliveryMethod.ReliableOrdered && IsCombatMessage(type);
                if (peer.LitePeer != null) peer.LitePeer.Send(frame, combat ? DeliveryMethod.ReliableUnordered : delivery);
                else if (relayTransport != null) relayTransport.Send(peer.RelayPeerId, frame, combat ? DeliveryMethod.ReliableUnordered : delivery);
                CountSent(frame.Length, 1);
                if (!relayTransportActive && IsImmediateMessage(type)) manager?.TriggerUpdate();
            }
            catch (Exception ex) { LastError = "Send failed: " + ex.Message; }
        }

        private static bool IsCombatMessage(MessageType type) => type == MessageType.DamageRequest || type == MessageType.EnemyDamageState || type == MessageType.WeaponFire;
        private static bool IsImmediateMessage(MessageType type) => IsCombatMessage(type) || type == MessageType.DoorRequest ||
            type == MessageType.DoorState || type == MessageType.CheckpointEvent || type == MessageType.PlayerLifecycle;

        private void UpdateDiagnostics(float deltaTime)
        {
            lock (diagnosticsLock)
            {
                diagnosticsAccumulator += Math.Max(0f, deltaTime);
                if (diagnosticsAccumulator < 1f) return;
                var elapsed = diagnosticsAccumulator;
                diagnosticsAccumulator = 0f;
                SentBytesPerSecond = sentBytesWindow / elapsed;
                ReceivedBytesPerSecond = receivedBytesWindow / elapsed;
                SnapshotSendRate = sentSnapshotsWindow / elapsed;
                SnapshotReceiveRate = receivedSnapshotsWindow / elapsed;
                PacketSendRate = sentPacketsWindow / elapsed;
                PacketReceiveRate = receivedPacketsWindow / elapsed;
                sentBytesWindow = receivedBytesWindow = sentPacketsWindow = receivedPacketsWindow = 0;
                sentSnapshotsWindow = receivedSnapshotsWindow = 0;
            }
        }

        private void CountSent(int frameBytes, int recipients)
        {
            if (recipients <= 0) return;
            lock (diagnosticsLock)
            {
                var bytes = (long)frameBytes * recipients;
                sentBytesWindow += bytes;
                TotalBytesSent += bytes;
                sentPacketsWindow += recipients;
            }
        }

        private void CountReceived(int frameBytes)
        {
            lock (diagnosticsLock)
            {
                receivedBytesWindow += frameBytes;
                TotalBytesReceived += frameBytes;
                receivedPacketsWindow++;
            }
        }

        private bool ObserveSnapshot(uint playerId, uint tick)
        {
            uint previous;
            if (lastSnapshotTickByPlayer.TryGetValue(playerId, out previous))
            {
                var delta = unchecked(tick - previous);
                if (!SnapshotTickPolicy.IsNewer(tick, previous)) return false;
                if (delta > 1) lostSnapshots += delta - 1;
            }
            lastSnapshotTickByPlayer[playerId] = tick;
            observedSnapshots++;
            return true;
        }

        private void ResetDiagnostics()
        {
            lastSnapshotTickByPlayer.Clear();
            latestLifecycleByPlayer.Clear();
            lifecycleEventTickByPlayer.Clear();
            snapshotFreshness.Clear();
            lastLatencyByPeer.Clear();
            latencyWindow.Clear();
            sentBytesWindow = receivedBytesWindow = sentPacketsWindow = receivedPacketsWindow = 0;
            sentSnapshotsWindow = receivedSnapshotsWindow = lostSnapshots = observedSnapshots = 0;
            TotalBytesSent = TotalBytesReceived = 0;
            SnapshotSendRate = SnapshotReceiveRate = PacketSendRate = PacketReceiveRate = SentBytesPerSecond = ReceivedBytesPerSecond = jitterEwma = 0f;
            diagnosticsAccumulator = 0f;
        }

        private byte[] EncodeLobbyInfo()
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                WriteShortString(writer, LobbyName, 40);
                WriteShortString(writer, localPlayerName, 24);
                writer.Write((byte)players.Count);
                writer.Write((byte)maxPlayers);
                writer.Write(requiresPassword);
                return stream.ToArray();
            }
        }

        private static DiscoveredLobby DecodeLobbyInfo(System.Net.IPEndPoint endpoint, byte[] payload)
        {
            using (var stream = new MemoryStream(payload, false))
            using (var reader = new BinaryReader(stream))
            {
                var name = ReadShortString(reader, 40);
                var host = ReadShortString(reader, 24);
                var count = reader.ReadByte();
                var maximum = reader.ReadByte();
                var requiresPassword = reader.ReadBoolean();
                if (stream.Position != stream.Length || maximum < 2 || maximum > 4 || count > maximum) throw new InvalidDataException("Invalid LAN room metadata.");
                return new DiscoveredLobby { Name = name, HostName = host, PlayerCount = count, MaxPlayers = maximum, RequiresPassword = requiresPassword, Address = endpoint.Address.ToString(), Port = endpoint.Port, LastSeenUtc = DateTime.UtcNow };
            }
        }

        private static string BuildConnectionKey(string password)
        {
            if (string.IsNullOrEmpty(password)) return DefaultConnectionKey;
            using (var sha = SHA256.Create())
            {
                var digest = sha.ComputeHash(Encoding.UTF8.GetBytes(DefaultConnectionKey + "\n" + password));
                var builder = new StringBuilder(digest.Length * 2);
                foreach (var value in digest) builder.Append(value.ToString("x2"));
                return builder.ToString();
            }
        }

        private static void WriteShortString(BinaryWriter writer, string value, int maximumBytes)
        {
            var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            if (bytes.Length > maximumBytes) Array.Resize(ref bytes, maximumBytes);
            writer.Write((byte)bytes.Length);
            writer.Write(bytes);
        }

        private static string ReadShortString(BinaryReader reader, int maximumBytes)
        {
            var length = reader.ReadByte();
            if (length > maximumBytes) throw new InvalidDataException("LAN metadata string is too long.");
            return Encoding.UTF8.GetString(reader.ReadBytes(length));
        }

        private static byte[] EncodeWelcome(uint id, int playerLimit)
        {
            using (var stream = new MemoryStream(5))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(id);
                writer.Write((byte)playerLimit);
                return stream.ToArray();
            }
        }

        private static string CreateSessionToken()
        {
            var bytes = new byte[SessionHandshakeCodec.SessionTokenHexLength / 2];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            var builder = new StringBuilder(SessionHandshakeCodec.SessionTokenHexLength);
            foreach (var value in bytes) builder.Append(value.ToString("x2"));
            return builder.ToString();
        }

        private bool IsPlayerIdConnected(uint playerId)
        {
            foreach (var id in playerIds.Values) if (id == playerId) return true;
            return false;
        }

        private static string SanitizeName(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) value = fallback;
            value = value.Trim();
            return value.Length > 24 ? value.Substring(0, 24) : value;
        }

        private void SetStatus(string status) { StatusChanged?.Invoke(status); }

        public void OnPeerConnected(LiteNetPeer peer)
        {
            EnqueueNetworkEvent(() => HandlePeerConnected(GetLitePeer(peer)));
        }

        private void HandlePeerConnected(TransportPeer peer)
        {
            if (isHost)
            {
                if (ConnectedPeerCount > maxPlayers - 1 && !reconnectReservations.HasLiveReservation(networkTimeSeconds))
                {
                    if (peer.LitePeer != null) peer.LitePeer.Disconnect();
                    return;
                }
                var id = nextPlayerId++;
                playerIds[peer] = id;
                pendingPlayerHandshakes[peer] = networkTimeSeconds;
                playerNames[id] = "Player " + id;
                players[id] = new PlayerSnapshot { PlayerId = id, PlayerName = playerNames[id] };
                SetStatus("Peer connected from " + peer.Address + ". Waiting for player handshake.");
            }
            else
            {
                hostPeer = peer;
                connectAttemptPending = false;
                Send(hostPeer, MessageType.Hello, SessionHandshakeCodec.EncodeHello(localPlayerName, clientSessionToken), DeliveryMethod.ReliableOrdered);
                SetStatus("Connected to host. Handshaking...");
            }
        }

        public void OnPeerDisconnected(LiteNetPeer peer, DisconnectInfo disconnectInfo)
        {
            EnqueueNetworkEvent(() => HandlePeerDisconnected(GetLitePeer(peer), disconnectInfo.Reason.ToString()));
        }

        private void HandlePeerDisconnected(TransportPeer peer, string disconnectReason)
        {
            pendingPlayerHandshakes.Remove(peer);
            if (isHost)
            {
                uint id;
                if (playerIds.TryGetValue(peer, out id))
                {
                    string token;
                    PlayerSnapshot disconnectedSnapshot;
                    if (sessionTokenByPlayer.TryGetValue(id, out token) && players.TryGetValue(id, out disconnectedSnapshot))
                    {
                        var reservedName = playerNames.ContainsKey(id) ? playerNames[id] : disconnectedSnapshot.PlayerName;
                        reconnectReservations.Store(token, id, reservedName, disconnectedSnapshot,
                            networkTimeSeconds + ReconnectReservationSeconds);
                    }
                    sessionTokenByPlayer.Remove(id);
                    playerIds.Remove(peer);
                    playerNames.Remove(id);
                    players.Remove(id);
                    lastSnapshotTickByPlayer.Remove(id);
                    playerSnapshotHistory.Remove(id);
                    lastAcceptedMovementAt.Remove(id);
                    snapshotFreshness.Remove(id);
                    var payload = BitConverter.GetBytes(id);
                    var frame = Frame(MessageType.PlayerLeft, payload);
                    CountSent(frame.Length, ConnectedPeerCount);
                    if (relayTransportActive) relayTransport.SendToAll(frame, DeliveryMethod.ReliableOrdered);
                    else manager.SendToAll(frame, DeliveryMethod.ReliableOrdered);
                    PlayerLeft?.Invoke(id);
                }
                lastLatencyByPeer.Remove(peer);
            }
            else if (peer == hostPeer || (hostPeer == null && connectAttemptPending))
            {
                IsConnected = false;
                hostPeer = null;
                connectAttemptPending = false;
                if (clientEverWelcomed)
                {
                    ClearDisconnectedRemotePlayers();
                    ScheduleReconnect("Disconnected from host: " + disconnectReason);
                }
                else
                    SetStatus("Disconnected from host: " + disconnectReason);
            }
            if (peer.LitePeer != null) litePeerMap.Remove(peer.LitePeer);
            else relayPeerMap.Remove(peer.RelayPeerId);
        }

        private void RemoveExpiredPlayerHandshakes()
        {
            expiredHandshakePeers.Clear();
            foreach (var pair in pendingPlayerHandshakes)
                if (networkTimeSeconds - pair.Value >= PlayerHandshakeTimeoutSeconds)
                    expiredHandshakePeers.Add(pair.Key);

            for (var i = 0; i < expiredHandshakePeers.Count; i++)
            {
                var peer = expiredHandshakePeers[i];
                uint playerId;
                pendingPlayerHandshakes.Remove(peer);
                if (playerIds.TryGetValue(peer, out playerId))
                {
                    playerIds.Remove(peer);
                    playerNames.Remove(playerId);
                    players.Remove(playerId);
                }
                SetStatus("Player handshake timed out for " + peer.Address + ". Check the connection and try again.");
                try { if (peer.LitePeer != null) peer.LitePeer.Disconnect(); }
                catch (Exception ex) { LastError = "Could not disconnect an unresponsive peer: " + ex.Message; }
            }
            expiredHandshakePeers.Clear();
        }

        private void ClearDisconnectedRemotePlayers()
        {
            disconnectedRemotePlayerIds.Clear();
            foreach (var playerId in players.Keys)
                if (playerId != localPlayerId) disconnectedRemotePlayerIds.Add(playerId);
            for (var i = 0; i < disconnectedRemotePlayerIds.Count; i++)
            {
                var playerId = disconnectedRemotePlayerIds[i];
                players.Remove(playerId);
                latestLifecycleByPlayer.Remove(playerId);
                lastSnapshotTickByPlayer.Remove(playerId);
                snapshotFreshness.Remove(playerId);
                playerSnapshotHistory.Remove(playerId);
                lastAcceptedMovementAt.Remove(playerId);
                authorizedWeaponUnlocks.Remove(playerId);
                authoritativeWeaponInventoryInitialized.Remove(playerId);
                authoritativeVitalsInitialized.Remove(playerId);
                PlayerLeft?.Invoke(playerId);
            }
            disconnectedRemotePlayerIds.Clear();
        }

        public void OnNetworkReceive(LiteNetPeer peer, NetPacketReader reader, DeliveryMethod deliveryMethod)
        {
            byte[] bytes;
            try { bytes = reader.GetRemainingBytes(); }
            finally { reader.Recycle(); }
            var discardable = deliveryMethod == DeliveryMethod.Unreliable || deliveryMethod == DeliveryMethod.Sequenced;
            var queued = EnqueueNetworkEvent(() =>
            {
                CountReceived(bytes.Length);
                Process(GetLitePeer(peer), bytes);
            }, discardable, bytes.Length);
            if (!queued && !discardable)
            {
                LastError = "Reliable network backlog exceeded its memory limit; disconnected peer " + peer.Address + ".";
                peer.Disconnect();
            }
        }

        public void OnConnectionRequest(LiteConnectionRequest request)
        {
            if (isHost && manager != null &&
                (manager.ConnectedPeersCount < maxPlayers - 1 || reconnectReservations.HasLiveReservation(networkTimeSeconds)))
                request.AcceptIfKey(connectionKey);
            else request.Reject();
        }

        public void OnNetworkError(System.Net.IPEndPoint endPoint, System.Net.Sockets.SocketError socketError)
        {
            EnqueueNetworkEvent(() => LastError = "Network error " + socketError + " at " + endPoint);
        }

        public void OnNetworkLatencyUpdate(LiteNetPeer peer, int latency)
        {
            EnqueueNetworkEvent(() => HandleNetworkLatencyUpdate(GetLitePeer(peer), latency), true);
        }

        private void HandleNetworkLatencyUpdate(TransportPeer peer, int latency)
        {
            latencyWindow.Add(latency);
            int previous;
            if (lastLatencyByPeer.TryGetValue(peer, out previous))
            {
                var variation = Math.Abs(latency - previous);
                jitterEwma = jitterEwma == 0f ? variation : jitterEwma * 0.75f + variation * 0.25f;
            }
            lastLatencyByPeer[peer] = latency;
        }

        public void OnNetworkReceiveUnconnected(System.Net.IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
        {
            byte[] bytes;
            try { bytes = reader.GetRemainingBytes(); }
            finally { reader.Recycle(); }
            EnqueueNetworkEvent(() => ProcessUnconnected(remoteEndPoint, bytes), true, bytes.Length);
        }

        private void ProcessUnconnected(System.Net.IPEndPoint remoteEndPoint, byte[] bytes)
        {
            try
            {
                CountReceived(bytes.Length);
                var message = NetworkMessage.Decode(bytes);
                if (message.Type == MessageType.DiscoveryRequest && isHost && IsPublic)
                {
                    var response = new NetworkMessage(MessageType.DiscoveryResponse, NextSequence(), EncodeLobbyInfo()).Encode();
                    CountSent(response.Length, 1);
                    manager.SendUnconnectedMessage(response, remoteEndPoint);
                }
                else if (message.Type == MessageType.DiscoveryResponse && !isHost)
                {
                    var lobby = DecodeLobbyInfo(remoteEndPoint, message.Payload);
                    var existing = discoveredLobbies.Find(l => l.Address == lobby.Address && l.Port == lobby.Port);
                    if (existing == null) discoveredLobbies.Add(lobby);
                    else
                    {
                        existing.Name = lobby.Name;
                        existing.HostName = lobby.HostName;
                        existing.PlayerCount = lobby.PlayerCount;
                        existing.MaxPlayers = lobby.MaxPlayers;
                        existing.RequiresPassword = lobby.RequiresPassword;
                        existing.LastSeenUtc = lobby.LastSeenUtc;
                    }
                }
            }
            catch (Exception ex) { LastError = "Invalid LAN discovery packet: " + ex.Message; }
        }

        private bool EnqueueNetworkEvent(Action callback, bool dropWhenBacklogged = false, int payloadBytes = 0)
        {
            if (callback == null || payloadBytes < 0 || payloadBytes > MaximumPendingNetworkBytes) return false;
            lock (pendingNetworkEventsLock)
            {
                if ((dropWhenBacklogged && pendingNetworkEvents.Count >= UnreliableBacklogLimit) ||
                    pendingNetworkEvents.Count >= MaximumPendingNetworkEvents ||
                    pendingNetworkBytes > MaximumPendingNetworkBytes - payloadBytes) return false;
                pendingNetworkEvents.Enqueue(new PendingNetworkEvent { Callback = callback, PayloadBytes = payloadBytes });
                pendingNetworkBytes += payloadBytes;
                return true;
            }
        }

        private void DrainPendingNetworkEvents()
        {
            var processed = 0;
            while (processed < 2048)
            {
                PendingNetworkEvent pending;
                lock (pendingNetworkEventsLock)
                {
                    if (pendingNetworkEvents.Count == 0) break;
                    pending = pendingNetworkEvents.Dequeue();
                    pendingNetworkBytes -= pending.PayloadBytes;
                }
                try { pending.Callback(); }
                catch (Exception ex) { LastError = "Network event processing failed: " + ex.Message; }
                processed++;
            }
        }

        public void OnPeerAddressChanged(LiteNetPeer peer, System.Net.IPEndPoint previousAddress) { }
        public void OnMessageDelivered(LiteNetPeer peer, object userData) { }

        public void Stop()
        {
            reconnectEnabled = false;
            respawnMovementGraceUntil.Clear();
            sceneSnapshotSyncReady = true;
            localSnapshotsDuringBarrier = false;
            clientEverWelcomed = false;
            connectAttemptPending = false;
            reconnectAttempts = 0;
            reconnectDelayTimer = 0f;
            lock (snapshotPumpLock)
            {
                snapshotPumpEnabled = false;
                if (snapshotPumpTimer != null) snapshotPumpTimer.Dispose();
                snapshotPumpTimer = null;
                System.Threading.Interlocked.Exchange(ref latestLocalSnapshot, null);
                snapshotPumpHeartbeatElapsed = 0d;
                snapshotTick = 0;
            }
            if (relayTransport != null)
            {
                try { relayTransport.Dispose(); }
                catch (Exception ex) { LastError = "Relay stop failed: " + ex.Message; }
                relayTransport = null;
            }
            relayTransportActive = false;
            relayJoinCode = null;
            litePeerMap.Clear();
            relayPeerMap.Clear();
            if (manager != null)
            {
                try { manager.Stop(); }
                catch (Exception ex) { LastError = "Stop failed: " + ex.Message; }
                manager = null;
            }
            lock (pendingNetworkEventsLock)
            {
                pendingNetworkEvents.Clear();
                pendingNetworkBytes = 0;
            }
            IsRunning = false;
            IsConnected = false;
            isHost = false;
            isDiscovering = false;
            hostPeer = null;
            clientAddress = null;
            clientPort = 0;
            ListenPort = 0;
            localPlayerId = 1;
            nextPlayerId = 2;
            networkTimeSeconds = 0f;
            sequence = 0;
            playerIds.Clear();
            pendingPlayerHandshakes.Clear();
            playerNames.Clear();
            sessionTokenByPlayer.Clear();
            reconnectReservations.Clear();
            players.Clear();
            playerSnapshotHistory.Clear();
            authorizedWeaponUnlocks.Clear();
            authoritativeWeaponInventoryInitialized.Clear();
            authoritativeVitalsInitialized.Clear();
            lastSnapshotTickByPlayer.Clear();
            lastAcceptedMovementAt.Clear();
            lock (predictionLock) predictedLocalPositionsByTick.Clear();
            discoveredLobbies.Clear();
            ResetDiagnostics();
            clientSessionToken = null;
        }

        public void Dispose() { Stop(); }
    }
}



