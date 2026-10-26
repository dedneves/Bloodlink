using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LiteNetLib;
using Unity.Collections;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Relay;
using Unity.Networking.Transport.Utilities;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Core.Configuration;
using Unity.Services.Core.Internal;
using Unity.Services.Qos;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;

namespace ULTRAKILL.Bloodlink.Networking
{
    internal sealed class UnityRelayTransport : IDisposable
    {
        private sealed class RelayPeer
        {
            public int Id;
            public NetworkConnection Connection;
        }

        private readonly List<RelayPeer> peers = new List<RelayPeer>(4);
        private NetworkDriver driver;
        private NativeList<NetworkConnection> hostConnections;
        private NetworkConnection clientConnection;
        private NetworkPipeline reliablePipeline;
        private NetworkPipeline combatReliablePipeline;
        private NetworkPipeline sequencedPipeline;
        private NetworkPipeline fragmentedPipeline;
        private NetworkPipeline fragmentedSequencedPipeline;
        private bool isHost;
        private int nextPeerId = 2;
        private readonly SynchronizationContext unityContext;
        private readonly int unityThreadId;
        private volatile bool disposed;

        public UnityRelayTransport()
        {
            unityContext = SynchronizationContext.Current;
            unityThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        public string JoinCode { get; private set; }
        public bool IsCreated { get { return driver.IsCreated; } }
        public int PeerCount { get { return peers.Count; } }
        public event Action<int> PeerConnected;
        public event Action<int> PeerDisconnected;
        public event Action<int, byte[]> PacketReceived;

        public async Task<string> StartHostAsync(string projectId, int maximumPlayers)
        {
            EnsureUnityThread("starting a Relay host");
            await InitializeServices(projectId);
            ThrowIfDisposed("Relay hosting was cancelled during Unity Services initialization.");
            var allocation = await RelayService.Instance.CreateAllocationAsync(Math.Max(1, Math.Min(3, maximumPlayers - 1)));
            ThrowIfDisposed("Relay hosting was cancelled while the allocation was being created.");
            JoinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);
            ThrowIfDisposed("Relay hosting was cancelled while its join code was being created.");
            EnsureUnityThread("creating the Relay host transport");
            var relayData = new RelayServerData(allocation, "udp");
            CreateDriver(ref relayData, true);
            return JoinCode;
        }

        public async Task StartClientAsync(string projectId, string joinCode)
        {
            EnsureUnityThread("joining a Relay allocation");
            await InitializeServices(projectId);
            ThrowIfDisposed("Joining Relay was cancelled during Unity Services initialization.");
            var allocation = await RelayService.Instance.JoinAllocationAsync(joinCode.Trim());
            ThrowIfDisposed("Joining Relay was cancelled while its allocation was being resolved.");
            EnsureUnityThread("creating the Relay client transport");
            var relayData = new RelayServerData(allocation, "udp");
            CreateDriver(ref relayData, false);
            clientConnection = driver.Connect();
            if (!clientConnection.IsCreated) throw new InvalidOperationException("Unity Relay could not start the client connection.");
        }

        private async Task InitializeServices(string projectId)
        {
            EnsureUnityThread("initializing Unity Gaming Services");
            if (string.IsNullOrWhiteSpace(projectId)) throw new ArgumentException("Unity Gaming Services Project ID is required.", nameof(projectId));
            var waitStarted = DateTime.UtcNow;
            while (UnityServices.State == ServicesInitializationState.Initializing)
            {
                if ((DateTime.UtcNow - waitStarted).TotalSeconds > 30)
                    throw new TimeoutException("Unity Gaming Services initialization did not finish within 30 seconds.");
                await Task.Yield();
                EnsureUnityThread("waiting for Unity Gaming Services initialization");
            }
            // Register package initializers before Core locks its component
            // registry. RuntimeInitializeOnLoadMethod is not replayed for UGS
            // assemblies loaded by BepInEx after Unity has started.
            var servicesWereUninitialized = UnityServices.State == ServicesInitializationState.Uninitialized;
            if (servicesWereUninitialized) RegisterDynamicallyLoadedServicePackages();
            var options = new InitializationOptions().SetOption("com.unity.services.core.project-id", projectId.Trim());
            try
            {
                // Always await the public UGS initializer. It returns the existing
                // initialization task when another package started UGS earlier.
                await UnityServices.InitializeAsync(options);
                EnsureUnityThread("resuming after Unity Services initialization");
            }
            catch (Exception ex) { throw new InvalidOperationException("UnityServices.InitializeAsync failed for the configured Project ID: " + ex.Message, ex); }

            // The Relay SDK's own RuntimeInitializeOnLoadMethod is skipped when
            // BepInEx loads the Relay assembly after Unity startup. Its HTTP client
            // requires this captured Unity TaskScheduler for every web request.
            InitializeRelayThreadHelper();

            // If the game initialized Core before Bloodlink loaded its UGS DLLs,
            // there was no opportunity to register these package initializers.
            // Bootstrap the missing providers against Core's existing registry.
            EnsureLateLoadedServicesInitialized();
            if (!AuthenticationService.Instance.IsSignedIn)
            {
                await AuthenticationService.Instance.SignInAnonymouslyAsync();
                EnsureUnityThread("resuming after Unity Authentication sign-in");
            }
        }

        internal static void InitializeRelayThreadHelper()
        {
            var unityContext = SynchronizationContext.Current;
            if (unityContext == null)
                throw new InvalidOperationException("Unity's main-thread synchronization context is unavailable; Relay's HTTP scheduler cannot be initialized.");

            var relayAssembly = typeof(RelayService).Assembly;
            var helper = relayAssembly.GetType("Unity.Services.Relay.Scheduler.ThreadHelper", true);
            var initialize = helper.GetMethod("Init", BindingFlags.Static | BindingFlags.Public);
            if (initialize == null) throw new MissingMethodException(helper.FullName, "Init");
            initialize.Invoke(null, null);

            var schedulerProperty = helper.GetProperty("TaskScheduler", BindingFlags.Static | BindingFlags.Public);
            if (schedulerProperty == null || schedulerProperty.GetValue(null) == null)
                throw new InvalidOperationException("Relay's HTTP TaskScheduler is still null after main-thread initialization.");
        }

        private static void RegisterDynamicallyLoadedServicePackages()
        {
            var authenticationAssembly = typeof(AuthenticationService).Assembly;
            InvokeStaticRegistration(authenticationAssembly, "Unity.Services.Authentication.AuthenticationPackageInitializer", "Register");

            var relayAssembly = typeof(RelayService).Assembly;
            InvokeStaticRegistration(relayAssembly, "Unity.Services.Relay.RelayServiceProvider", "Register");

            var qosAssembly = Assembly.Load("Unity.Services.QoS");
            var qosInitializer = qosAssembly.GetType("Unity.Services.Qos.QosPackageInitializer", true);
            var initializer = Activator.CreateInstance(qosInitializer, true);
            var register = qosInitializer.GetMethod("Register", BindingFlags.Instance | BindingFlags.NonPublic,
                null, new[] { typeof(CoreRegistry) }, null);
            if (register == null) throw new MissingMethodException(qosInitializer.FullName, "Register(CoreRegistry)");
            register.Invoke(initializer, new object[] { CoreRegistry.Instance });
        }

        private static void InvokeStaticRegistration(Assembly assembly, string typeName, string methodName)
        {
            var type = assembly.GetType(typeName, true);
            var method = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (method == null) throw new MissingMethodException(type.FullName, methodName);
            method.Invoke(null, null);
        }

        private static void EnsureLateLoadedServicesInitialized()
        {
            var hasAuthentication = IsServiceInitialized(() => AuthenticationService.Instance);
            var hasRelay = IsServiceInitialized(() => RelayService.Instance);
            if (hasAuthentication && hasRelay) return;

            var registry = CoreRegistry.Instance;
            if (registry == null)
                throw new InvalidOperationException("Unity Services Core has no active registry after InitializeAsync; Authentication and Relay packages could not be registered.");

            var componentRegistryProperty = typeof(CoreRegistry).GetProperty("ComponentRegistry", BindingFlags.Instance | BindingFlags.NonPublic);
            var componentRegistry = componentRegistryProperty?.GetValue(registry);
            var wasLocked = componentRegistry != null && componentRegistry.GetType().Name == "LockedComponentRegistry";
            if (wasLocked)
            {
                var innerRegistry = componentRegistry.GetType().GetProperty("Registry", BindingFlags.Instance | BindingFlags.NonPublic);
                componentRegistryProperty.SetValue(registry, innerRegistry.GetValue(componentRegistry));
            }
            try
            {
                if (!hasAuthentication)
                    InitializeLateLoadedPackage(typeof(AuthenticationService).Assembly,
                        "Unity.Services.Authentication.AuthenticationPackageInitializer", registry);
                if (!IsServiceInitialized(() => QosService.Instance))
                {
                    var qosAssembly = Assembly.Load("Unity.Services.QoS");
                    InitializeLateLoadedPackage(qosAssembly, "Unity.Services.Qos.QosPackageInitializer", registry);
                }
                if (!hasRelay)
                    InitializeLateLoadedPackage(typeof(RelayService).Assembly, "Unity.Services.Relay.RelayServiceProvider", registry);
            }
            finally
            {
                if (wasLocked)
                    typeof(CoreRegistry).GetMethod("LockComponentRegistration", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(registry, null);
            }

            // Accessing these singletons here makes an incomplete registry fail at
            // setup time with a useful stack, rather than later during allocation.
            if (!IsServiceInitialized(() => AuthenticationService.Instance) ||
                !IsServiceInitialized(() => RelayService.Instance))
                throw new InvalidOperationException("Unity Services initialized without usable Authentication and Relay providers.");
        }

        private static bool IsServiceInitialized(Func<object> getService)
        {
            try { return getService() != null; }
            catch (InvalidOperationException) { return false; }
        }

        private static void InitializeLateLoadedPackage(Assembly assembly, string typeName, CoreRegistry registry)
        {
            var type = assembly.GetType(typeName, true);
            var package = Activator.CreateInstance(type, true);
            var initialize = type.GetMethod("Initialize", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(CoreRegistry) }, null);
            if (initialize == null) throw new MissingMethodException(type.FullName, "Initialize(CoreRegistry)");
            var task = initialize.Invoke(package, new object[] { registry }) as Task;
            if (task != null) task.GetAwaiter().GetResult();
        }

        private void CreateDriver(ref RelayServerData relayData, bool host)
        {
            EnsureUnityThread("creating the Unity Transport driver");
            if (disposed) throw new ObjectDisposedException(nameof(UnityRelayTransport));
            DisposeDriver();
            isHost = host;
            try
            {
                var settings = new NetworkSettings();
                settings.WithFragmentationStageParameters(4096);
                settings.WithRelayParameters(ref relayData);
                driver = NetworkDriver.Create(settings);
                if (!driver.IsCreated) throw new InvalidOperationException("Unity Transport failed to create a Relay driver.");
                if (driver.Bind(NetworkEndPoint.AnyIpv4) != 0)
                    throw new InvalidOperationException("Unity Transport could not bind the Relay endpoint.");
                if (host)
                {
                    if (driver.Listen() != 0)
                        throw new InvalidOperationException("Unity Transport could not listen on the Relay allocation.");
                    hostConnections = new NativeList<NetworkConnection>(3, Allocator.Persistent);
                }

                reliablePipeline = driver.CreatePipeline(typeof(FragmentationPipelineStage), typeof(ReliableSequencedPipelineStage));
                combatReliablePipeline = driver.CreatePipeline(typeof(FragmentationPipelineStage), typeof(ReliableSequencedPipelineStage));
                sequencedPipeline = driver.CreatePipeline(typeof(UnreliableSequencedPipelineStage));
                fragmentedPipeline = driver.CreatePipeline(typeof(FragmentationPipelineStage));
                fragmentedSequencedPipeline = driver.CreatePipeline(typeof(FragmentationPipelineStage), typeof(UnreliableSequencedPipelineStage));
            }
            catch
            {
                if (hostConnections.IsCreated) hostConnections.Dispose();
                hostConnections = default;
                DisposeDriver();
                throw;
            }
        }

        public void Update()
        {
            EnsureUnityThread("polling the Relay transport");
            if (!driver.IsCreated) return;
            try
            {
                driver.ScheduleUpdate().Complete();
                if (isHost) UpdateHost();
                else UpdateClient();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Unity Relay transport update failed: " + ex.Message, ex);
            }
        }

        private void UpdateHost()
        {
            NetworkConnection incoming;
            while ((incoming = driver.Accept()) != default(NetworkConnection))
            {
                hostConnections.Add(incoming);
                var peer = new RelayPeer { Id = nextPeerId++, Connection = incoming };
                peers.Add(peer);
                PeerConnected?.Invoke(peer.Id);
            }

            for (var i = 0; i < peers.Count; i++)
            {
                var peer = peers[i];
                NetworkEvent.Type eventType;
                while ((eventType = driver.PopEventForConnection(peer.Connection, out var stream)) != NetworkEvent.Type.Empty)
                {
                    if (eventType == NetworkEvent.Type.Data)
                        PacketReceived?.Invoke(peer.Id, ReadPacket(stream));
                    else if (eventType == NetworkEvent.Type.Disconnect)
                    {
                        hostConnections[i] = default(NetworkConnection);
                        PeerDisconnected?.Invoke(peer.Id);
                        peers.RemoveAt(i--);
                        break;
                    }
                }
            }
            for (var i = hostConnections.Length - 1; i >= 0; i--)
                if (!hostConnections[i].IsCreated) hostConnections.RemoveAt(i);
        }

        private void UpdateClient()
        {
            if (!clientConnection.IsCreated) return;
            NetworkEvent.Type eventType;
            while ((eventType = clientConnection.PopEvent(driver, out var stream)) != NetworkEvent.Type.Empty)
            {
                if (eventType == NetworkEvent.Type.Connect)
                {
                    if (peers.Count == 0) peers.Add(new RelayPeer { Id = 1, Connection = clientConnection });
                    PeerConnected?.Invoke(1);
                }
                else if (eventType == NetworkEvent.Type.Data)
                    PacketReceived?.Invoke(1, ReadPacket(stream));
                else if (eventType == NetworkEvent.Type.Disconnect)
                {
                    if (peers.Count > 0) PeerDisconnected?.Invoke(1);
                    peers.Clear();
                    clientConnection = default(NetworkConnection);
                }
            }
        }

        public bool Send(int peerId, byte[] payload, DeliveryMethod delivery)
        {
            EnsureUnityThread("sending a Relay packet");
            if (!driver.IsCreated || payload == null) return false;
            var peer = peers.Find(candidate => candidate.Id == peerId);
            if (peer == null || !peer.Connection.IsCreated) return false;
            var pipeline = SelectPipeline(delivery);
            if (driver.BeginSend(pipeline, peer.Connection, out var writer) != 0) return false;
            for (var i = 0; i < payload.Length; i++)
                if (!writer.WriteByte(payload[i])) { driver.AbortSend(writer); return false; }
            return driver.EndSend(writer) >= 0;
        }

        public int SendToAll(byte[] payload, DeliveryMethod delivery)
        {
            var sent = 0;
            for (var i = 0; i < peers.Count; i++)
                if (Send(peers[i].Id, payload, delivery)) sent++;
            return sent;
        }

        public void Disconnect(int peerId)
        {
            EnsureUnityThread("disconnecting a Relay peer");
            var peer = peers.Find(candidate => candidate.Id == peerId);
            if (peer != null && peer.Connection.IsCreated) driver.Disconnect(peer.Connection);
        }

        private NetworkPipeline SelectPipeline(DeliveryMethod delivery)
        {
            // ReliableUnordered selects the independent combat stream at this
            // adapter boundary; both Relay pipelines preserve reliable ordering.
            if (delivery == DeliveryMethod.ReliableUnordered) return combatReliablePipeline;
            if (delivery == DeliveryMethod.ReliableOrdered)
                return reliablePipeline;
            if (delivery == DeliveryMethod.Sequenced)
                return fragmentedSequencedPipeline;
            return fragmentedPipeline;
        }

        private static byte[] ReadPacket(Unity.Networking.Transport.DataStreamReader reader)
        {
            var payload = new byte[reader.Length];
            for (var i = 0; i < payload.Length; i++) payload[i] = reader.ReadByte();
            return payload;
        }

        public void Dispose()
        {
            if (disposed) return;
            EnsureUnityThread("disposing the Relay transport");
            disposed = true;
            JoinCode = null;
            peers.Clear();
            if (hostConnections.IsCreated) hostConnections.Dispose();
            hostConnections = default;
            DisposeDriver();
        }

        private void DisposeDriver()
        {
            if (driver.IsCreated) driver.Dispose();
            driver = default;
            clientConnection = default;
        }

        private void EnsureUnityThread(string operation)
        {
            if (unityContext == null || Thread.CurrentThread.ManagedThreadId != unityThreadId ||
                !ReferenceEquals(SynchronizationContext.Current, unityContext))
                throw new InvalidOperationException("Unity main-thread context is required for " + operation + ". Start Relay from the Bloodlink UI and keep the game running during setup.");
        }

        private void ThrowIfDisposed(string message)
        {
            if (disposed) throw new OperationCanceledException(message);
        }
    }
}
