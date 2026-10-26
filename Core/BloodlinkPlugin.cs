using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using Gravity;
using ULTRAKILL.Bloodlink.Combat;
using ULTRAKILL.Bloodlink.Enemies;
using ULTRAKILL.Bloodlink.Levels;
using ULTRAKILL.Bloodlink.Networking;
using ULTRAKILL.Bloodlink.Players;
using ULTRAKILL.Bloodlink.UI;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

namespace ULTRAKILL.Bloodlink.Core
{
    [DefaultExecutionOrder(10000)]
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class BloodlinkPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "org.bloodlink.ultrakill.coop";
        private const string MultiplayerPanelStateKey = "bloodlink-multiplayer-panel";
        private const string LevelOverlayStateKey = "bloodlink-level-loading-overlay";
        public const string PluginName = "ULTRAKILL CO-OP: BLOODLINK";
        public const string PluginVersion = "0.112.0";
        public static BloodlinkPlugin Instance { get; private set; }
        internal void LogRemoteAnimatorDiagnostics(string message) { Logger.LogInfo(message); }
        private static readonly int[] availableMissions = BuildAvailableMissions();

        private readonly NetworkService network = new NetworkService();
        private readonly EnemySyncRegistry enemySync = new EnemySyncRegistry();
        private readonly EnemyProjectileSync enemyProjectileSync = new EnemyProjectileSync();
        private readonly EnemyExplosionSync enemyExplosionSync = new EnemyExplosionSync();
        private readonly DoorStateSync doorStateSync = new DoorStateSync();
        private readonly WorldObjectSync worldObjectSync = new WorldObjectSync();
        private readonly SceneObjectStateSync sceneObjectStateSync = new SceneObjectStateSync();
        internal ItemSync ItemSync { get; } = new ItemSync();
        private float nextManualResyncAt;
        private readonly WorldAudioSync worldAudioSync = new WorldAudioSync();
        private readonly WeaponPickupSync weaponPickupSync = new WeaponPickupSync();
        private readonly SpectatorController spectator = new SpectatorController();
        private sealed class ActiveTeamMarker
        {
            public uint OwnerPlayerId;
            public uint Sequence;
            public Vector3 Position;
            public byte Kind;
            public float ExpiresAt;
        }

        private sealed class PendingSecretBonus
        {
            public SecretBonusLocation Location;
            public float ExpiresAt;
        }

        private readonly Queue<byte[]> pendingDamage = new Queue<byte[]>();
        private readonly List<ActiveTeamMarker> activeTeamMarkers = new List<ActiveTeamMarker>();
        private readonly ReliableEventSequenceTracker teamMarkerSequences = new ReliableEventSequenceTracker();
        private readonly DamageRequestRateLimiter teamMarkerRateLimiter = new DamageRequestRateLimiter(2f, 1f);
        private readonly Dictionary<uint, RemotePlayerVisual> remoteViews = new Dictionary<uint, RemotePlayerVisual>();
        private readonly Dictionary<MessageType, float> nextRejectedMessageLogAt = new Dictionary<MessageType, float>();
        private readonly Dictionary<MessageType, int> suppressedRejectedMessageCounts = new Dictionary<MessageType, int>();
        private readonly Dictionary<uint, Queue<PlayerAnimationEvent>> pendingRemoteAnimations = new Dictionary<uint, Queue<PlayerAnimationEvent>>();
        private readonly HashSet<uint> presentRemotePlayers = new HashSet<uint>();
        private readonly List<uint> removedRemotePlayerIds = new List<uint>();
        private readonly WeaponFireRateLimiter fireRateLimiter = new WeaponFireRateLimiter(64f, 128f);
        private readonly WeaponFireRateLimiter animationRateLimiter = new WeaponFireRateLimiter(3f, 10f);
        private readonly WeaponFireRateLimiter coinProjectileRateLimiter = new WeaponFireRateLimiter(4f, 6f);
        private readonly WeaponFireRateLimiter nailProjectileRateLimiter = new WeaponFireRateLimiter(48f, 80f);
        private readonly WeaponFireRateLimiter rocketProjectileRateLimiter = new WeaponFireRateLimiter(64f, 80f);
        private readonly WindowedEventSequenceTracker damageRequestSequences = new WindowedEventSequenceTracker();
        private readonly ReliableEventSequenceTracker playerVitalsRequestSequences = new ReliableEventSequenceTracker();
        private readonly DamageRequestRateLimiter damageRequestRateLimiter = new DamageRequestRateLimiter(512f, 2048f);
        private readonly DamageRequestRateLimiter resyncRequestRateLimiter = new DamageRequestRateLimiter(1f, 0.2f);
        private readonly DamageRequestRateLimiter playerVitalsRateLimiter = new DamageRequestRateLimiter(12f, 24f);
        private readonly ReliableEventSequenceTracker secretBonusSequences = new ReliableEventSequenceTracker();
        private readonly DamageRequestRateLimiter secretBonusRateLimiter = new DamageRequestRateLimiter(4f, 8f);
        private readonly List<SecretBonusLocation> collectedSecretBonuses = new List<SecretBonusLocation>();
        private readonly Dictionary<string, PendingSecretBonus> pendingSecretBonusRelays = new Dictionary<string, PendingSecretBonus>();
        private NewMovement localPlayer;
        private Vector3 previousNetworkPlayerPosition;
        private float previousNetworkPlayerPositionAt;
        private bool hasPreviousNetworkPlayerPosition;
        private GunControl localGun;
        private string playerName = "Player";
        private string lobbyName = "Bloodlink Room";
        private string password = "";
        private string address = "127.0.0.1";
        private string bindAddress = "0.0.0.0";
        private string portText = "27015";
        private string status = "Offline";
        private int maxPlayers = 4;
        private int startMissionNumber = 1;
        private Vector2 startMissionScroll;
        private bool isPublic = true;
        private bool showPanel;
        private bool panelCursorCaptured;
        private CursorLockMode previousCursorLockState;
        private bool previousCursorVisible;
        private bool panelStateCaptured;
        private bool previousPlayerInputLocked;
        private bool previousCameraLocked;
        private bool previousGameCursorLocked;
        private bool loadingOverlayInputActive;
        private bool overlayCursorCaptured;
        private CursorLockMode overlayPreviousCursorLockState;
        private bool overlayPreviousCursorVisible;
        private bool overlayStateCaptured;
        private bool overlayPreviousPlayerInputLocked;
        private bool overlayPreviousCameraLocked;
        private bool overlayPreviousGameCursorLocked;
        private int menuTab;
        private Vector2 lobbyScroll;
        private Vector2 panelContentScroll;
        private Texture2D panelTexture;
        private Texture2D accentTexture;
        private Texture2D accentHoverTexture;
        private Texture2D fieldTexture;
        private Texture2D scrimTexture;
        private GUIStyle panelStyle;
        private GUIStyle scrimStyle;
        private GUIStyle titleStyle;
        private GUIStyle sectionStyle;
        private GUIStyle labelStyle;
        private GUIStyle statusStyle;
        private GUIStyle loadingStatusStyle;
        private GUIStyle hudTextStyle;
        private GUIStyle buttonStyle;
        private GUIStyle selectedTabStyle;
        private GUIStyle textFieldStyle;
        private ConfigEntry<string> primaryColorConfig;
        private ConfigEntry<string> secondaryColorConfig;
        private ConfigEntry<string> lightColorConfig;
        private ConfigEntry<int> glowIntensityConfig;
        private ConfigEntry<int> sessionDifficultyConfig;
        private ConfigEntry<int> cooperativeModeConfig;
        private ConfigEntry<bool> friendlyFireConfig;
        private bool remoteFriendlyFire;
        public bool FriendlyFireEnabled => network.IsHost ? friendlyFireConfig.Value : remoteFriendlyFire;
        private ConfigEntry<KeyCode> teamMarkerKeyConfig;
        private ConfigEntry<KeyCode> menuKeyConfig;
        private ConfigEntry<string> relayProjectIdConfig;
        private string relayJoinCode = string.Empty;
        private bool relayStartPending;
        private string primaryColorText;
        private string secondaryColorText;
        private string lightColorText;
        private uint localPrimaryColor = PlayerAppearanceColor.DefaultPrimary;
        private uint localSecondaryColor = PlayerAppearanceColor.DefaultSecondary;
        private uint localLightColor = PlayerAppearanceColor.DefaultLight;
        private byte localGlowIntensity = PlayerAppearanceColor.DefaultGlow;
        private float teamWipeTimer;
        private bool restartRequested;
        private float nextSyncDiagnosticsAt;
        private uint nextDamageRequestSequence;
        private uint nextPlayerVitalsRequestSequence;
        private uint nextTeamMarkerSequence;
        private uint nextSecretBonusSequence;
        private bool applyingAuthoritativeVitals;
        private PlayerLifecycleUpdate pendingLocalVitals;
        private bool pendingSceneVitalsReset;
        private Vector3 lastSafePlayerPosition;
        private Quaternion lastSafePlayerRotation = Quaternion.identity;
        private string lastSafeSceneName;
        private bool hasLastSafePlayerPosition;
        private float lastSafePlayerAt;
        private float fallRecoveryStartedAt = -1f;
        private float glitchEffectUntil;
        private uint glitchEffectSeed;
        private Vector3 pendingLocalPositionCorrection;
        private V1AppearancePreview appearancePreview;
        private int appearanceColorPart;
        private byte previewWeaponSlot = 1;
        private byte previewWeaponVariation;
        private string lastSceneName;
        private float nextMainMenuAttachAttempt;
        private float nextSceneStateBroadcastAt;
        private float nextPlayerVisualTemplateSearchAt;
        private float nextRemoteVisualWarningAt;
        private float nextAutomaticResyncAt;
        private int automaticResyncsRemaining;
        private bool automaticWorldResyncStarted;
        private float clientHitFeedbackUntil;
        private PlayerAnimations playerVisualTemplate;
        private bool loggedMainMenuFallback;
        private bool loggedPanelDraw;
        private bool loggedUpdate;
        private bool loggedOnGui;
        private bool loggedMenuProbe;
        private bool mainMenuVisible;
        private bool hasSavedRunInBackground;
        private bool previousRunInBackground;

        public bool IsHost { get { return network.IsHost; } }
        public bool IsConnected { get { return network.IsConnected; } }
        internal bool HasActivePlayerMovement { get { return localPlayer != null && localPlayer.rb != null; } }
        internal bool IsMultiplayerPanelOpen { get { return showPanel; } }
        internal bool IsUiInputLocked { get { return showPanel || loadingOverlayInputActive; } }
        public NetworkService Network { get { return network; } }
        public int SessionDifficulty { get; private set; } = 2;
        public int CooperativeMode { get; private set; }

        private void Awake()
        {
            DontDestroyOnLoad(transform.root.gameObject);
            Logger.LogInfo("Bloodlink startup: Awake entered.");
            Instance = this;
            primaryColorConfig = Config.Bind("Appearance", "PrimaryColor", "#CC2F3D", "Main V1 armor color in #RRGGBB form.");
            secondaryColorConfig = Config.Bind("Appearance", "SecondaryColor", "#30343B", "Secondary V1 armor color in #RRGGBB form.");
            lightColorConfig = Config.Bind("Appearance", "LightColor", "#75D9FF", "V1 light/detail color in #RRGGBB form.");
            glowIntensityConfig = Config.Bind("Appearance", "GlowIntensity", 96, "V1 emission strength from 0 to 255.");
            // Awake can run while Unity's InputSystem is still initializing. Do not
            // force MonoSingleton<PrefsManager> creation from the plugin bootstrap.
            sessionDifficultyConfig = Config.Bind("Multiplayer", "SessionDifficulty", 2,
                "Difficulty selected by the host for every level in the co-op session (0-4).");
            SessionDifficulty = Mathf.Clamp(sessionDifficultyConfig.Value, 0, 4);
            cooperativeModeConfig = Config.Bind("Multiplayer", "CooperativeMode", 0,
                "Enemy durability profile: 0 Classic, 1 Balanced, 2 Chaos.");
            CooperativeMode = Mathf.Clamp(cooperativeModeConfig.Value, 0, 2);
            friendlyFireConfig = Config.Bind("Multiplayer", "FriendlyFire", false, "Allow teammates to damage one another. Controlled by the host.");
            teamMarkerKeyConfig = Config.Bind("Multiplayer", "TeamMarkerKey", KeyCode.G,
                "Key used to place a temporary marker for connected teammates.");
            menuKeyConfig = Config.Bind("Multiplayer", "MenuKey", KeyCode.F7,
                "Key used to open and close the Bloodlink multiplayer panel.");
            relayProjectIdConfig = Config.Bind("Unity Relay", "ProjectId", "f1193525-4982-4232-9f9c-7cc0a50be273",
                "Unity Gaming Services Project ID used for Relay rooms.");
            primaryColorText = primaryColorConfig.Value;
            secondaryColorText = secondaryColorConfig.Value;
            lightColorText = lightColorConfig.Value;
            UpdateAppearanceConfig();
            Logger.LogInfo("Bloodlink startup: configuration initialized. Menu key: " + menuKeyConfig.Value + ".");
            network.StatusChanged += OnNetworkStatus;
            network.PlayerJoined += OnPlayerJoined;
            network.PlayerLeft += OnPlayerLeft;
            network.PlayerLifecycleChanged += OnPlayerLifecycleChanged;
            network.LocalPositionReconciled += OnLocalPositionReconciled;
            network.MessageReceived += OnNetworkMessage;
            lastSceneName = SceneManager.GetActiveScene().name;
            SceneObjectIdentity.PrimeScene(SceneManager.GetActiveScene());
            sceneObjectStateSync.PrimeScene();
            weaponPickupSync.CaptureScene();
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            Logger.LogInfo("Bloodlink startup: applying Harmony patches.");
            new HarmonyLib.Harmony(PluginGuid).PatchAll();
            Logger.LogInfo("Bloodlink startup: Harmony patches applied.");
            Logger.LogInfo($"{PluginName} {PluginVersion} loaded. Multiplayer bootstrap initialized.");
        }

        private void Update()
        {
            if (!loggedUpdate)
            {
                loggedUpdate = true;
                Logger.LogInfo("Update active. Scene: '" + SceneManager.GetActiveScene().name + "'.");
            }
            if (localPlayer == null) localPlayer = FindObjectOfType<NewMovement>();
            if (localGun == null) localGun = FindObjectOfType<GunControl>();
            EncounterRecovery.ObserveBoss(this);
            UpdateFallRecovery();
            if (localPlayer != null)
            {
                if (network.IsHost) network.MaximumPlayerHealth = GetLocalPlayerHealthCap();
                if (pendingSceneVitalsReset && network.IsHost && network.IsConnected)
                {
                    if (localPlayer.dead)
                    {
                        localPlayer.Respawn();
                        if (localPlayer.rb != null) localPlayer.rb.velocity = Vector3.zero;
                    }
                    foreach (var player in network.Players.Values)
                    {
                        if (player == null) continue;
                        var wasDead = (player.MovementFlags & 4) != 0;
                        var restoredHealth = player.PlayerId == network.LocalPlayerId
                            ? (ushort)Mathf.Clamp(localPlayer.hp, 0, 200)
                            : wasDead ? network.MaximumPlayerHealth : player.Health;
                        network.SetAuthoritativePlayerVitals(player.PlayerId, restoredHealth, false);
                    }
                    pendingSceneVitalsReset = false;
                }
                UpdateAppearanceConfig();
                network.SetLocalAppearance(localPrimaryColor, localSecondaryColor, localLightColor, localGlowIntensity);
                var playerPosition = localPlayer.transform.position;
                var velocity = localPlayer.rb != null ? localPlayer.rb.velocity : Vector3.zero;
                var positionSampleTime = Time.unscaledTime;
                if (hasPreviousNetworkPlayerPosition)
                {
                    var elapsed = positionSampleTime - previousNetworkPlayerPositionAt;
                    var displacement = playerPosition - previousNetworkPlayerPosition;
                    if (elapsed > 0.001f && displacement.sqrMagnitude > 0.0009f &&
                        displacement.sqrMagnitude <= NetworkService.MaximumPlayerMovementSpeed * NetworkService.MaximumPlayerMovementSpeed * elapsed * elapsed)
                    {
                        var measuredVelocity = displacement / elapsed;
                        // ULTRAKILL can report vertical Rigidbody velocity while its
                        // custom controller moves the player horizontally. Select and
                        // blend planar components independently so gravity cannot mask
                        // walking/running motion on the remote V1 animator.
                        var rigidbodyPlanar = Vector3.ProjectOnPlane(velocity, Vector3.up);
                        var observedPlanar = Vector3.ProjectOnPlane(measuredVelocity, Vector3.up);
                        var planarVelocity = rigidbodyPlanar.sqrMagnitude < 0.25f
                            ? observedPlanar
                            : Vector3.Lerp(rigidbodyPlanar, observedPlanar, 0.65f);
                        velocity = planarVelocity + Vector3.up * velocity.y;
                    }
                }
                previousNetworkPlayerPosition = playerPosition;
                previousNetworkPlayerPositionAt = positionSampleTime;
                hasPreviousNetworkPlayerPosition = true;
                byte movementFlags = 0;
                if (localPlayer.boost) movementFlags |= 1;
                if (localPlayer.sliding) movementFlags |= 2;
                if (localPlayer.dead) movementFlags |= 4;
                if (localPlayer.gc != null && localPlayer.gc.onGround) movementFlags |= 8;
                var slot = localGun != null ? WeaponInventoryPolicy.ToNetworkSlot(localGun.currentSlotIndex) : (byte)1;
                var variation = localGun != null ? (byte)Mathf.Clamp(localGun.currentVariationIndex, 0, 255) : (byte)0;
                var viewRotation = localPlayer.cc != null && localPlayer.cc.cam != null ? localPlayer.cc.cam.transform.rotation : localPlayer.transform.rotation;
                var weaponName = localGun != null && localGun.currentWeapon != null ? localGun.currentWeapon.name : string.Empty;
                // The first-person viewmodel animator is not the V1 body locomotion
                // animator. Do not force its idle state onto remote body controllers.
                var weaponIdentity = localGun != null && localGun.currentWeapon != null ? localGun.currentWeapon.GetComponent<WeaponIdentifier>() : null;
                network.Update(Time.unscaledDeltaTime, playerPosition, localPlayer.transform.rotation, velocity, movementFlags, slot, variation, localPlayer.hp, viewRotation, weaponName, CaptureWeaponInventoryMask(), true, 0, 0f, weaponIdentity != null && weaponIdentity.alternateVersion);
            }
            else
            {
                UpdateAppearanceConfig();
                network.SetLocalAppearance(localPrimaryColor, localSecondaryColor, localLightColor, localGlowIntensity);
                network.Update(Time.unscaledDeltaTime, Vector3.zero, Quaternion.identity, Vector3.zero, 0, 1, 0, 100,
                    Quaternion.identity, string.Empty, 0, false);
            }
            ApplyLocalPositionCorrection();
            SceneLoadPatch.Update();
            UpdateAutomaticWorldResync();
            SetLoadingOverlayInputState(SceneLoadPatch.ShowLoadingOverlay);
            SyncRemotePlayers();
            enemySync.Update(network, Time.unscaledDeltaTime);
            EnemyMeleeSync.Update(network);
            if (network.IsConnected && Time.unscaledTime >= nextSyncDiagnosticsAt)
            {
                nextSyncDiagnosticsAt = Time.unscaledTime + 5f;
                Logger.LogInfo(enemySync.GetDiagnostics() + " " + worldObjectSync.Diagnostics + " " + sceneObjectStateSync.Diagnostics + " " + ItemSync.Diagnostics + " " + EnemyMeleeSync.Diagnostics + ", outgoingHits=" + pendingDamage.Count);
            }
            enemyProjectileSync.Update(network, Time.unscaledDeltaTime);
            doorStateSync.Update(network, Time.unscaledDeltaTime);
            worldObjectSync.Update(network, Time.unscaledDeltaTime);
            sceneObjectStateSync.Update(network);
            ItemSync.Update(network);
            LevelProgressionCoordinator.Update();
            CheckpointCoordinator.Update();
            weaponPickupSync.Update(network);
            RetrySecretBonusPickups();
            FlushPendingDamage();
            spectator.Update(localPlayer, network);
            if (spectator.ConsumeRespawnRequest()) RequestSpectatorRespawn();
            if (!network.IsHost && network.IsConnected && pendingLocalVitals != null && localPlayer != null)
            {
                var pending = pendingLocalVitals;
                pendingLocalVitals = null;
                ApplyAuthoritativePlayerVitals(pending);
            }
            UpdateTeamMarkers();
            UpdateTeamWipe();
            if (loadingOverlayInputActive && Input.GetKeyDown(KeyCode.Escape)) ExitReadinessScreen();
            if (Input.GetKeyDown(menuKeyConfig.Value)) ToggleMenuPanel();
            if (showPanel || loadingOverlayInputActive)
            {
                EnforceMultiplayerPanelInputStatePatch.Apply(GameStateManager.Instance);
            }
            BroadcastAuthoritativeSceneState();
            EnsureMainMenuButton();
        }

        private void ToggleMenuPanel()
        {
            SetPanelVisible(!showPanel);
            if (showPanel) panelContentScroll = Vector2.zero;
            Logger.LogInfo(showPanel ? "Bloodlink panel opened." : "Bloodlink panel closed.");
        }

        private void SetPanelVisible(bool visible)
        {
            if (visible && !showPanel)
            {
                previousCursorLockState = Cursor.lockState;
                previousCursorVisible = Cursor.visible;
                panelCursorCaptured = true;
                var currentStateManager = GameStateManager.Instance;
                if (currentStateManager != null)
                {
                    previousPlayerInputLocked = currentStateManager.PlayerInputLocked;
                    previousCameraLocked = currentStateManager.CameraLocked;
                    previousGameCursorLocked = currentStateManager.CursorLocked;
                    panelStateCaptured = true;
                }
            }
            showPanel = visible;
            var gameStateManager = GameStateManager.Instance;
            if (gameStateManager != null && visible)
            {
                if (!gameStateManager.IsStateActive(MultiplayerPanelStateKey))
                {
                    gameStateManager.RegisterState(new GameState(MultiplayerPanelStateKey, gameObject)
                    {
                        priority = int.MaxValue,
                        cursorLock = LockMode.Unlock,
                        cameraInputLock = LockMode.Lock,
                        playerInputLock = LockMode.Lock
                    });
                }
            }
            else if (gameStateManager != null && gameStateManager.IsStateActive(MultiplayerPanelStateKey))
            {
                gameStateManager.PopState(MultiplayerPanelStateKey);
            }
            if (visible)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            else if (panelCursorCaptured)
            {
                if (panelStateCaptured && gameStateManager != null)
                    EnforceMultiplayerPanelInputStatePatch.Restore(gameStateManager, previousPlayerInputLocked,
                        previousCameraLocked, previousGameCursorLocked);
                Cursor.lockState = previousCursorLockState;
                Cursor.visible = previousCursorVisible;
                panelCursorCaptured = false;
                panelStateCaptured = false;
            }
        }

        private void SetLoadingOverlayInputState(bool active)
        {
            if (active == loadingOverlayInputActive)
            {
                if (active) EnforceMultiplayerPanelInputStatePatch.Apply(GameStateManager.Instance);
                return;
            }
            var manager = GameStateManager.Instance;
            if (active)
            {
                overlayPreviousCursorLockState = Cursor.lockState;
                overlayPreviousCursorVisible = Cursor.visible;
                overlayCursorCaptured = true;
                if (manager != null)
                {
                    overlayPreviousPlayerInputLocked = manager.PlayerInputLocked;
                    overlayPreviousCameraLocked = manager.CameraLocked;
                    overlayPreviousGameCursorLocked = manager.CursorLocked;
                    overlayStateCaptured = true;
                    if (!manager.IsStateActive(LevelOverlayStateKey))
                        manager.RegisterState(new GameState(LevelOverlayStateKey, gameObject)
                        {
                            priority = int.MaxValue,
                            cursorLock = LockMode.Unlock,
                            cameraInputLock = LockMode.Lock,
                            playerInputLock = LockMode.Lock
                        });
                }
                loadingOverlayInputActive = true;
                EnforceMultiplayerPanelInputStatePatch.Apply(manager);
                return;
            }

            loadingOverlayInputActive = false;
            if (manager != null && manager.IsStateActive(LevelOverlayStateKey)) manager.PopState(LevelOverlayStateKey);
            if (overlayStateCaptured && manager != null)
                EnforceMultiplayerPanelInputStatePatch.Restore(manager, overlayPreviousPlayerInputLocked,
                    overlayPreviousCameraLocked, overlayPreviousGameCursorLocked);
            if (overlayCursorCaptured)
            {
                if (showPanel)
                {
                    Cursor.lockState = CursorLockMode.None;
                    Cursor.visible = true;
                }
                else
                {
                    Cursor.lockState = overlayPreviousCursorLockState;
                    Cursor.visible = overlayPreviousCursorVisible;
                }
            }
            overlayCursorCaptured = false;
            overlayStateCaptured = false;
        }

        private void EnsureMainMenuButton()
        {
            if (Time.unscaledTime < nextMainMenuAttachAttempt) return;
            nextMainMenuAttachAttempt = Time.unscaledTime + 0.5f;
            var sceneName = SceneManager.GetActiveScene().name;
            var menu = FindObjectOfType<MainMenuAgony>();
            var options = MonoSingleton<OptionsManager>.Instance;
            var optionsMenu = options != null && options.mainMenu;
            mainMenuVisible = menu != null || optionsMenu ||
                string.Equals(sceneName, "Main Menu", StringComparison.OrdinalIgnoreCase);
            if (!loggedMenuProbe)
            {
                loggedMenuProbe = true;
                Logger.LogInfo("Menu probe: scene='" + sceneName + "', MainMenuAgony=" + (menu != null) +
                    ", options.mainMenu=" + optionsMenu + ".");
            }
            if (!mainMenuVisible || menu == null) return;
            if (menu.GetComponent<MainMenuMultiplayerButton>() != null) return;
            menu.gameObject.AddComponent<MainMenuMultiplayerButton>();
            LogMainMenuInjected();
        }

        private void LateUpdate()
        {
            spectator.LateUpdate(localPlayer, network);
        }

        internal bool TryGetSpectatorPose(uint playerId, out Vector3 position)
        {
            RemotePlayerVisual visual;
            if (remoteViews.TryGetValue(playerId, out visual) && visual != null) { position = visual.transform.position; return true; }
            PlayerSnapshot snapshot;
            position = network.Players.TryGetValue(playerId, out snapshot) ? snapshot.Position : Vector3.zero;
            return snapshot != null;
        }

        private ulong CaptureWeaponInventoryMask()
        {
            if (localGun == null || localGun.slots == null) return 0;
            ulong mask = 0;
            var slotCount = Math.Min(WeaponInventoryPolicy.Slots, localGun.slots.Count);
            for (var slotIndex = 0; slotIndex < slotCount; slotIndex++)
            {
                var slot = localGun.slots[slotIndex];
                if (slot == null) continue;
                var variationCount = Math.Min(WeaponInventoryPolicy.VariationsPerSlot, slot.Count);
                for (var variation = 0; variation < variationCount; variation++)
                {
                    if (slot[variation] != null)
                        mask = WeaponInventoryPolicy.Add(mask, (byte)(slotIndex + 1), (byte)variation);
                }
            }
            return mask;
        }

        private void OnLocalPositionReconciled(PlayerSnapshot authoritative, Vector3 correction)
        {
            if (network.IsHost || authoritative == null || !network.SceneSnapshotSyncReady) return;
            if (!PlayerPositionCorrectionPolicy.ShouldCorrect(correction.sqrMagnitude)) return;
            var accumulated = correction;
            var maximumSquared = NetworkService.MaximumLocalPositionCorrection * NetworkService.MaximumLocalPositionCorrection;
            if (float.IsNaN(accumulated.sqrMagnitude) || float.IsInfinity(accumulated.sqrMagnitude) ||
                accumulated.sqrMagnitude > maximumSquared)
            {
                pendingLocalPositionCorrection = Vector3.zero;
                return;
            }
            pendingLocalPositionCorrection = accumulated;
        }

        internal void SetSceneSnapshotSyncReady(bool ready)
        {
            pendingLocalPositionCorrection = Vector3.zero;
            network.SetSceneSnapshotSyncReady(ready);
        }

        private void ApplyLocalPositionCorrection()
        {
            if (network.IsHost || localPlayer == null) return;
            if (!network.SceneSnapshotSyncReady)
            {
                pendingLocalPositionCorrection = Vector3.zero;
                return;
            }
            if (pendingLocalPositionCorrection.sqrMagnitude < 0.0001f) return;
            var correction = pendingLocalPositionCorrection;
            var maxCorrection = NetworkService.MaximumLocalPositionCorrection;
            if (float.IsNaN(correction.sqrMagnitude) || float.IsInfinity(correction.sqrMagnitude) ||
                correction.sqrMagnitude > maxCorrection * maxCorrection)
            {
                pendingLocalPositionCorrection = Vector3.zero;
                return;
            }
            correction = Vector3.ClampMagnitude(correction, PlayerPositionCorrectionPolicy.MaximumStep(Time.unscaledDeltaTime));
            pendingLocalPositionCorrection -= correction;
            if (localPlayer.rb != null) localPlayer.rb.position += correction;
            else localPlayer.transform.position += correction;
            network.RecordAppliedPositionCorrection(correction);
        }

        private void UpdateTeamWipe()
        {
            if (!network.IsHost || !network.IsConnected || localPlayer == null || !localPlayer.dead)
            {
                teamWipeTimer = 0f;
                if (localPlayer != null && !localPlayer.dead) restartRequested = false;
                return;
            }
            if (restartRequested || network.Players.Count == 0) return;
            var everyoneDead = network.Players.Values.All(player => (player.MovementFlags & 4) != 0);
            if (!everyoneDead)
            {
                teamWipeTimer = 0f;
                return;
            }
            teamWipeTimer += Time.unscaledDeltaTime;
            if (teamWipeTimer < 2.5f || string.IsNullOrEmpty(SceneHelper.CurrentScene)) return;
            restartRequested = true;
            Logger.LogInfo("All connected players are dead; reviving the party without rebuilding the level.");
            RequestSpectatorRespawn();
            foreach (var player in network.Players.Values.ToArray())
                if (player.PlayerId != network.LocalPlayerId && (player.MovementFlags & 4) != 0)
                    network.SetAuthoritativePlayerVitals(player.PlayerId, network.MaximumPlayerHealth, false);
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            SceneObjectIdentity.PrimeScene(scene);
            weaponPickupSync.CaptureScene();
            if (mode == LoadSceneMode.Single)
                SceneLoadPatch.OnSceneLoaded(SceneHelper.CurrentScene ?? scene.name);
            SessionDifficultyOverride.ApplyToLoadedPlayers();
            pendingLocalVitals = null;
            if (scene.name == "DontDestroyOnLoad" || (scene.name == lastSceneName && mode != LoadSceneMode.Single)) return;
            if (mode != LoadSceneMode.Single) return;
            EncounterRecovery.Reset();
            automaticWorldResyncStarted = false;
            automaticResyncsRemaining = 0;
            hasLastSafePlayerPosition = false;
            lastSafeSceneName = null;
            fallRecoveryStartedAt = -1f;
            if (network.IsConnected) TriggerGlitchEffect(0.28f);
            worldObjectSync.Reset();
            sceneObjectStateSync.OnSceneChanged();
            ItemSync.Reset();
            LevelProgressionCoordinator.OnSceneChanged();
            pendingSceneVitalsReset = network.IsHost && network.IsConnected;
            lastSceneName = scene.name;
            hasPreviousNetworkPlayerPosition = false;
            previousNetworkPlayerPositionAt = 0f;
            if (network.IsHost && network.IsConnected)
                status = "Hosting " + scene.name + ". Waiting for players.";
            CheckpointCoordinator.OnSceneLoaded(SceneHelper.CurrentScene ?? scene.name);
            teamWipeTimer = 0f;
            restartRequested = false;
            enemySync.ResetForLevel();
            EnemyMeleeSync.Reset();
            enemyProjectileSync.Reset();
            doorStateSync.ResetForScene();
            collectedSecretBonuses.Clear();
            pendingSecretBonusRelays.Clear();
            playerVisualTemplate = null;
            nextPlayerVisualTemplateSearchAt = 0f;
            ClearRemotePlayers();
            Logger.LogInfo("Level state cleared after loading " + scene.name + " (" + mode + ").");
        }

        private void OnSceneUnloaded(Scene scene)
        {
            if (!network.IsConnected || scene.name == "DontDestroyOnLoad") return;
            TriggerGlitchEffect(0.28f);
            // Keep the last valid room anchor through unload callbacks. A streamed
            // room can disappear while the player is still in the same level;
            // OnSceneLoaded resets this anchor for an actual Single level change.
        }

        private void UpdateFallRecovery()
        {
            if (!network.IsConnected || localPlayer == null || localPlayer.dead ||
                !string.IsNullOrEmpty(SceneHelper.PendingScene))
            {
                fallRecoveryStartedAt = -1f;
                return;
            }

            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded || scene.name == "Main Menu" || scene.name == "DontDestroyOnLoad") return;
            var position = localPlayer.transform.position;
            if (!IsFinite(position)) return;
            if (!string.Equals(lastSafeSceneName, scene.name, StringComparison.Ordinal))
            {
                hasLastSafePlayerPosition = false;
                lastSafeSceneName = scene.name;
                fallRecoveryStartedAt = -1f;
            }

            var grounded = localPlayer.gc != null && localPlayer.gc.onGround;
            if (grounded)
            {
                lastSafePlayerPosition = position;
                lastSafePlayerRotation = localPlayer.transform.rotation;
                lastSafePlayerAt = Time.unscaledTime;
                hasLastSafePlayerPosition = true;
                fallRecoveryStartedAt = -1f;
                return;
            }
            if (!hasLastSafePlayerPosition) return;

            var displacement = position - lastSafePlayerPosition;
            var belowSafeRoom = position.y < lastSafePlayerPosition.y - 24f;
            var outOfBoundsLaunch = displacement.sqrMagnitude > 150f * 150f;
            if (!belowSafeRoom && !outOfBoundsLaunch)
            {
                fallRecoveryStartedAt = -1f;
                return;
            }

            // Long drops and launch pads are normal level traversal. Recover only
            // when no playable floor remains below, rather than from distance alone.
            foreach (var hit in Physics.RaycastAll(position + Vector3.up * 0.2f, Vector3.down, 250f,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                if (IsRecoveryFloor(hit)) { fallRecoveryStartedAt = -1f; return; }

            if (fallRecoveryStartedAt < 0f) fallRecoveryStartedAt = Time.unscaledTime;
            var fallingFast = localPlayer.rb == null || localPlayer.rb.velocity.y < -3f;
            if (!outOfBoundsLaunch && (!fallingFast || Time.unscaledTime - fallRecoveryStartedAt < 0.18f)) return;

            var destination = lastSafePlayerPosition;
            var rotation = lastSafePlayerRotation;
            if (Time.unscaledTime - lastSafePlayerAt > 45f)
            {
                var checkpoint = CheckpointCoordinator.GetCurrentCheckpoint(SceneHelper.CurrentScene);
                if (checkpoint != null)
                {
                    destination = checkpoint.transform.position + Vector3.up * 1.25f;
                    rotation = checkpoint.transform.rotation;
                }
            }
            RecoverLocalPlayer(destination, rotation, "fell outside the active room");
        }

        private static bool IsFinite(Vector3 position)
        {
            return !float.IsNaN(position.x) && !float.IsInfinity(position.x) &&
                !float.IsNaN(position.y) && !float.IsInfinity(position.y) &&
                !float.IsNaN(position.z) && !float.IsInfinity(position.z);
        }

        private void RecoverLocalPlayer(Vector3 position, Quaternion rotation, string reason)
        {
            if (localPlayer == null || !IsFinite(position)) return;
            Vector3 standing;
            if (!TryFindStandingPosition(position, out standing) && !EncounterRecovery.TryGetSpawn(this, out standing))
            {
                var found = false;
                foreach (var teammate in network.Players.Values)
                {
                    if (teammate.PlayerId == network.LocalPlayerId || (teammate.MovementFlags & 4) != 0 ||
                        !network.HasFreshPlayerSnapshot(teammate.PlayerId, 2f)) continue;
                    if (TryFindStandingPosition(teammate.Position + Vector3.right * 2f, out standing)) { found = true; break; }
                }
                if (!found) return;
            }
            position = standing;
            if (localPlayer.rb != null)
            {
                localPlayer.rb.velocity = Vector3.zero;
                localPlayer.rb.angularVelocity = Vector3.zero;
                localPlayer.rb.position = position;
                localPlayer.rb.rotation = rotation;
                localPlayer.rb.WakeUp();
            }
            localPlayer.transform.SetPositionAndRotation(position, rotation);
            previousNetworkPlayerPosition = position;
            previousNetworkPlayerPositionAt = Time.unscaledTime;
            hasPreviousNetworkPlayerPosition = true;
            pendingLocalPositionCorrection = Vector3.zero;
            lastSafePlayerPosition = position;
            lastSafePlayerRotation = rotation;
            lastSafePlayerAt = Time.unscaledTime;
            hasLastSafePlayerPosition = true;
            lastSafeSceneName = SceneManager.GetActiveScene().name;
            fallRecoveryStartedAt = -1f;
            TriggerGlitchEffect(0.24f);
            Logger.LogWarning("Recovered local player to the last safe room position: " + reason + ".");
        }

        private void TriggerGlitchEffect(float duration)
        {
            glitchEffectUntil = Mathf.Max(glitchEffectUntil, Time.unscaledTime + Mathf.Clamp(duration, 0.05f, 0.8f));
            unchecked { glitchEffectSeed = glitchEffectSeed * 1664525u + 1013904223u; }
        }

        private void DrawGlitchEffect()
        {
            if (Time.unscaledTime >= glitchEffectUntil || Screen.width <= 0 || Screen.height <= 0) return;
            var prior = GUI.color;
            var remaining = Mathf.Clamp01((glitchEffectUntil - Time.unscaledTime) / 0.28f);
            GUI.color = new Color(0.02f, 0.04f, 0.08f, 0.16f * remaining);
            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
            for (var i = 0; i < 9; i++)
            {
                var phase = Time.unscaledTime * (31f + i * 7f) + (glitchEffectSeed % 997u) * 0.01f + i * 43.7f;
                var y = Mathf.Repeat(Mathf.Sin(phase) * 0.5f + 0.5f + i * 0.137f, 1f) * Screen.height;
                var height = 1f + Mathf.Repeat(phase * 3.1f, 5f);
                GUI.color = i % 2 == 0
                    ? new Color(0.3f, 0.92f, 1f, 0.25f * remaining)
                    : new Color(1f, 0.18f, 0.28f, 0.2f * remaining);
                var shift = Mathf.Sin(phase * 1.7f) * 24f;
                GUI.DrawTexture(new Rect(shift, y, Screen.width * 0.42f, height), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(Screen.width * 0.56f - shift, y + 2f, Screen.width * 0.38f, 1f), Texture2D.whiteTexture);
            }
            GUI.color = prior;
        }

        internal void RelocateLocalPlayerIntoArena(ActivateArena arena)
        {
            if (!network.IsConnected || arena == null) return;
            EncounterRecovery.Capture(arena, this);
            if (localPlayer == null || localPlayer.dead) return;
            var triggers = new HashSet<Collider>();
            foreach (var collider in arena.GetComponentsInParent<Collider>(true)) if (collider != null) triggers.Add(collider);
            foreach (var collider in arena.GetComponentsInChildren<Collider>(true)) if (collider != null) triggers.Add(collider);
            var position = localPlayer.transform.position;
            var foundTrigger = false;
            var inside = false;
            Collider chosen = null;
            var chosenVolume = float.PositiveInfinity;
            foreach (var collider in triggers)
            {
                if (collider == null || !collider.enabled || !collider.isTrigger || !collider.gameObject.activeInHierarchy) continue;
                foundTrigger = true;
                var closest = collider.ClosestPoint(position);
                if ((closest - position).sqrMagnitude <= 0.25f) { inside = true; break; }
                var bounds = collider.bounds;
                var volume = bounds.size.x * bounds.size.y * bounds.size.z;
                if (volume > 0f && volume < chosenVolume) { chosenVolume = volume; chosen = collider; }
            }
            if (inside || (!foundTrigger && (position - arena.transform.position).sqrMagnitude <= 100f * 100f)) return;

            if (chosen == null) return;
            // Prefer a teammate who has actually entered the encounter. Trigger
            // bounds can extend above the roof; their center is not a spawn point.
            foreach (var teammate in network.Players.Values)
            {
                if (teammate.PlayerId == network.LocalPlayerId || (teammate.MovementFlags & 4) != 0 ||
                    !network.HasFreshPlayerSnapshot(teammate.PlayerId, 2f) ||
                    (chosen.ClosestPoint(teammate.Position) - teammate.Position).sqrMagnitude > 40f * 40f) continue;
                foreach (var offset in new[] { Vector3.right * 2f, Vector3.left * 2f, Vector3.forward * 2f, Vector3.back * 2f })
                {
                    Vector3 destination;
                    if (TryFindStandingPosition(teammate.Position + offset, out destination))
                    { RecoverLocalPlayer(destination, Quaternion.Euler(0f, localPlayer.transform.eulerAngles.y, 0f), "entering the active arena"); return; }
                }
            }
            var edge = chosen.ClosestPoint(position);
            var inward = chosen.bounds.center - edge;
            inward.y = 0f;
            var candidate = edge + inward.normalized * 3f;
            candidate.y = position.y;
            Vector3 safe;
            if (TryFindStandingPosition(candidate, out safe) && chosen.bounds.Contains(safe))
                RecoverLocalPlayer(safe, Quaternion.Euler(0f, localPlayer.transform.eulerAngles.y, 0f), "entering the active arena");
        }

        private static bool IsRecoveryFloor(RaycastHit hit)
        {
            return hit.collider != null && hit.normal.y >= 0.55f &&
                hit.collider.GetComponentInParent<NewMovement>() == null &&
                hit.collider.GetComponentInParent<EnemyIdentifier>() == null &&
                hit.collider.GetComponentInParent<RemotePlayerVisual>() == null;
        }

        internal bool TryFindStandingPosition(Vector3 near, out Vector3 destination)
        {
            destination = near;
            if (localPlayer == null || !IsFinite(near)) return false;
            var capsule = localPlayer.GetComponent<CapsuleCollider>();
            var height = capsule != null ? Mathf.Max(1f, capsule.height * Mathf.Abs(capsule.transform.lossyScale.y)) : 2.5f;
            var radius = capsule != null ? Mathf.Clamp(capsule.radius * Mathf.Abs(capsule.transform.lossyScale.x), 0.15f, 0.8f) : 0.4f;
            height = Mathf.Max(height, radius * 2f);
            var hits = Physics.RaycastAll(near + Vector3.up * 0.3f, Vector3.down, 8f,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var hit in hits)
            {
                if (!IsRecoveryFloor(hit) || hit.point.y > near.y - height * 0.25f) continue;
                var center = hit.point + Vector3.up * (height * 0.5f + 0.08f);
                var extent = Mathf.Max(0f, height * 0.5f - radius);
                var blocked = false;
                foreach (var obstacle in Physics.OverlapCapsule(center - Vector3.up * extent, center + Vector3.up * extent,
                    radius * 0.9f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                {
                    if (obstacle == null || obstacle.GetComponentInParent<NewMovement>() == localPlayer ||
                        obstacle.GetComponentInParent<EnemyIdentifier>() != null || obstacle.GetComponentInParent<RemotePlayerVisual>() != null) continue;
                    blocked = true; break;
                }
                if (blocked) continue;
                var offset = capsule != null ? capsule.transform.TransformVector(capsule.center) : Vector3.zero;
                destination = center - offset;
                return true;
            }
            return false;
        }

        private void SyncRemotePlayers()
        {
            if (localPlayer == null)
            {
                ClearRemotePlayers();
                return;
            }
            if (playerVisualTemplate == null && Time.unscaledTime >= nextPlayerVisualTemplateSearchAt)
            {
                playerVisualTemplate = FindObjectOfType<PlayerAnimations>();
                nextPlayerVisualTemplateSearchAt = Time.unscaledTime + 0.5f;
            }
            var template = playerVisualTemplate;
            if (!IsUsablePlayerVisualTemplate(template))
            {
                playerVisualTemplate = null;
                nextPlayerVisualTemplateSearchAt = Time.unscaledTime + 0.5f;
                template = null;
            }
            presentRemotePlayers.Clear();
            foreach (var state in network.Players.Values)
            {
                if (state == null) continue;
                if (state.PlayerId == network.LocalPlayerId) continue;
                presentRemotePlayers.Add(state.PlayerId);
                RemotePlayerVisual visual;
                if (remoteViews.TryGetValue(state.PlayerId, out visual) && visual == null)
                {
                    remoteViews.Remove(state.PlayerId);
                    visual = null;
                }
                if (visual == null)
                {
                    if (template == null) continue;
                    try
                    {
                        visual = RemotePlayerVisual.Create(template, localPlayer.transform, state.PlayerId, state.PlayerName);
                        if (visual == null || !visual.HasRenderableModel)
                        {
                            if (visual != null) Destroy(visual.gameObject);
                            visual = null;
                            playerVisualTemplate = null;
                            nextPlayerVisualTemplateSearchAt = Time.unscaledTime + 0.5f;
                            continue;
                        }
                        remoteViews.Add(state.PlayerId, visual);
                    }
                    catch (Exception ex)
                    {
                        var partial = GameObject.Find("Bloodlink Remote Player " + state.PlayerId);
                        if (partial != null) Destroy(partial);
                        playerVisualTemplate = null;
                        nextPlayerVisualTemplateSearchAt = Time.unscaledTime + 0.5f;
                        if (Time.unscaledTime >= nextRemoteVisualWarningAt)
                        {
                            nextRemoteVisualWarningAt = Time.unscaledTime + 5f;
                            Logger.LogWarning("Remote V1 visual creation will retry after the model finishes loading: " + ex);
                        }
                        continue;
                    }
                }
                try { visual.Push(state); }
                catch (Exception ex)
                {
                    Destroy(visual.gameObject);
                    remoteViews.Remove(state.PlayerId);
                    playerVisualTemplate = null;
                    nextPlayerVisualTemplateSearchAt = Time.unscaledTime + 0.5f;
                    if (Time.unscaledTime >= nextRemoteVisualWarningAt)
                    {
                        nextRemoteVisualWarningAt = Time.unscaledTime + 5f;
                        Logger.LogWarning("Remote V1 visual update will retry after the model finishes loading: " + ex);
                    }
                    continue;
                }
                Queue<PlayerAnimationEvent> queuedAnimations;
                if (pendingRemoteAnimations.TryGetValue(state.PlayerId, out queuedAnimations))
                {
                    while (queuedAnimations.Count > 0) visual.PlayPlayerAnimation(queuedAnimations.Dequeue());
                    pendingRemoteAnimations.Remove(state.PlayerId);
                }
                if (network.IsHost)
                {
                    var targetProxy = visual.GetComponent<RemotePlayerTargetProxy>();
                    if (targetProxy == null) targetProxy = visual.gameObject.AddComponent<RemotePlayerTargetProxy>();
                    targetProxy.SetState(state, network.HasFreshPlayerSnapshot(state.PlayerId, 0.75f));
                }
            }
            removedRemotePlayerIds.Clear();
            foreach (var pair in remoteViews)
                if (!presentRemotePlayers.Contains(pair.Key)) removedRemotePlayerIds.Add(pair.Key);
            foreach (var id in removedRemotePlayerIds)
            {
                if (remoteViews[id] != null) Destroy(remoteViews[id].gameObject);
                remoteViews.Remove(id);
            }
        }

        private void ClearRemotePlayers()
        {
            foreach (var view in remoteViews.Values)
                if (view != null) Destroy(view.gameObject);
            remoteViews.Clear();
            pendingRemoteAnimations.Clear();
        }

        private static bool IsUsablePlayerVisualTemplate(PlayerAnimations template)
        {
            if (template == null || !template.gameObject.activeInHierarchy) return false;
            var renderers = template.GetComponentsInChildren<Renderer>(true);
            var hasGeometry = false;
            for (var i = 0; i < renderers.Length && !hasGeometry; i++)
            {
                if (renderers[i] == null) continue;
                var skinned = renderers[i] as SkinnedMeshRenderer;
                if (skinned != null) hasGeometry = skinned.sharedMesh != null;
                else
                {
                    var filter = renderers[i].GetComponent<MeshFilter>();
                    hasGeometry = filter != null && filter.sharedMesh != null;
                }
            }
            return hasGeometry && template.GetComponentsInChildren<Animator>(true).Length > 0;
        }

        internal void BeginAutomaticWorldResync()
        {
            if (network.IsHost || !network.IsConnected || automaticWorldResyncStarted) return;
            automaticWorldResyncStarted = true;
            automaticResyncsRemaining = 3;
            nextAutomaticResyncAt = Time.unscaledTime + 0.75f;
        }

        private void UpdateAutomaticWorldResync()
        {
            if (automaticResyncsRemaining <= 0 || network.IsHost || !network.IsConnected ||
                !network.SceneSnapshotSyncReady || Time.unscaledTime < nextAutomaticResyncAt) return;
            if (network.SendToHost(MessageType.ResyncRequest, Array.Empty<byte>()))
            {
                automaticResyncsRemaining--;
                nextAutomaticResyncAt = Time.unscaledTime + 5.25f;
                Logger.LogInfo("Requested an automatic world-state refresh after joining the level (" +
                    automaticResyncsRemaining + " follow-up requests remain).");
            }
            else nextAutomaticResyncAt = Time.unscaledTime + 1f;
        }

        private void OnGUI()
        {
            if (!loggedOnGui)
            {
                loggedOnGui = true;
                Logger.LogInfo("OnGUI active. Scene: '" + SceneManager.GetActiveScene().name + "'.");
            }
            if (showPanel)
            {
                var current = Event.current;
                if (current != null && current.type == EventType.KeyDown && current.keyCode == KeyCode.Escape)
                {
                    current.Use();
                    if (SceneLoadPatch.ShowLoadingOverlay)
                    {
                        ExitReadinessScreen();
                    }
                    else
                    {
                        SetPanelVisible(false);
                        Logger.LogInfo("Bloodlink panel closed with Escape.");
                    }
                    return;
                }
                DrawMultiplayerPanel();
                ConsumePanelInputEvent();
                DrawGlitchEffect();
                return;
            }
            if (network.IsRunning)
            {
                DrawCoopHud();
                DrawTeammateMarkers();
                DrawTeamMarkers();
                if (!network.IsHost && Time.unscaledTime < clientHitFeedbackUntil && !SceneLoadPatch.ShowLoadingOverlay &&
                    localPlayer != null && !localPlayer.dead)
                {
                    var previousColor = GUI.color;
                    GUI.color = new Color(1f, 0.25f, 0.2f, 0.85f);
                    var x = Screen.width * 0.5f;
                    var y = Screen.height * 0.5f;
                    GUI.DrawTexture(new Rect(x - 9f, y - 9f, 3f, 3f), Texture2D.whiteTexture);
                    GUI.DrawTexture(new Rect(x + 6f, y - 9f, 3f, 3f), Texture2D.whiteTexture);
                    GUI.DrawTexture(new Rect(x - 9f, y + 6f, 3f, 3f), Texture2D.whiteTexture);
                    GUI.DrawTexture(new Rect(x + 6f, y + 6f, 3f, 3f), Texture2D.whiteTexture);
                    GUI.color = previousColor;
                }
            }
            if (SceneLoadPatch.ShowLoadingOverlay)
            {
                var current = Event.current;
                if (current != null && current.type == EventType.KeyDown && current.keyCode == KeyCode.Escape)
                {
                    current.Use();
                    ExitReadinessScreen();
                    return;
                }
                DrawLevelLoadingOverlay();
                ConsumePanelInputEvent();
            }
            if (spectator.ShowHud)
            {
                var hudWidth = Mathf.Min(420f, Screen.width - 32f);
                var hudRect = new Rect((Screen.width - hudWidth) * 0.5f, Mathf.Max(8f, Screen.height - 78f), hudWidth, 40f);
                GUI.Box(hudRect, spectator.Status);
                var respawnRect = new Rect((Screen.width - 164f) * 0.5f, Mathf.Max(8f, Screen.height - 36f), 164f, 30f);
                if (GUI.Button(respawnRect, "RESPAWN  [R]"))
                {
                    RequestSpectatorRespawn();
                    if (Event.current != null) Event.current.Use();
                }
            }
            if (mainMenuVisible) DrawMainMenuEntry();
            else if (localPlayer != null && !network.IsRunning) DrawOfflineHud();
            DrawGlitchEffect();
        }

        private static void ConsumePanelInputEvent()
        {
            var current = Event.current;
            if (current == null) return;
            switch (current.type)
            {
                case EventType.MouseDown:
                case EventType.MouseUp:
                case EventType.MouseDrag:
                case EventType.MouseMove:
                case EventType.ScrollWheel:
                case EventType.ContextClick:
                case EventType.KeyDown:
                case EventType.KeyUp:
                case EventType.ValidateCommand:
                case EventType.ExecuteCommand:
                    current.Use();
                    break;
            }
        }

        private void DrawMainMenuEntry()
        {
            EnsureMenuStyles();
            var statusWidth = Mathf.Max(180f, Mathf.Min(540f, Screen.width - 24f));
            var statusRect = new Rect(12f, 12f, statusWidth, 30f);
            GUI.Box(statusRect, GUIContent.none, panelStyle);
            GUI.Label(new Rect(22f, 17f, statusWidth - 20f, 22f), "BLOODLINK v" + PluginVersion + "  //  " + menuKeyConfig.Value + " MENU", labelStyle);
            if (MainMenuMultiplayerButton.UiPresent) return;
            if (!loggedMainMenuFallback)
            {
                loggedMainMenuFallback = true;
                Logger.LogInfo("Main menu IMGUI entry active.");
            }
            var buttonWidth = Mathf.Min(320f, Screen.width - 32f);
            var buttonRect = new Rect((Screen.width - buttonWidth) * 0.5f, Mathf.Max(8f, Screen.height - 128f), buttonWidth, 72f);
            if (GUI.Button(buttonRect, "MULTIPLAYER  [" + menuKeyConfig.Value + "]", buttonStyle))
            {
                ToggleMenuPanel();
            }
        }

        private void DrawOfflineHud()
        {
            EnsureMenuStyles();
            var width = Mathf.Max(180f, Mathf.Min(470f, Screen.width - 24f));
            var rect = new Rect(12f, 12f, width, 30f);
            GUI.Box(rect, GUIContent.none, panelStyle);
            GUI.Label(new Rect(22f, 17f, width - 20f, 22f), "BLOODLINK v" + PluginVersion + "  //  OFFLINE  //  " + menuKeyConfig.Value + " MENU", labelStyle);
        }

        private void DrawTeammateMarkers()
        {
            if (!network.IsConnected || localPlayer == null || localPlayer.cc == null || localPlayer.cc.cam == null) return;
            var camera = localPlayer.cc.cam;
            var center = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            var halfWidth = Mathf.Max(24f, center.x - 96f);
            var halfHeight = Mathf.Max(24f, center.y - 28f);
            foreach (var player in network.Players.Values)
            {
                if (player == null || player.PlayerId == network.LocalPlayerId || (player.MovementFlags & 4) != 0 ||
                    !network.HasFreshPlayerSnapshot(player.PlayerId, 0.75f)) continue;
                var projected = camera.WorldToScreenPoint(player.Position + Vector3.up * 1.5f);
                var screenPoint = new Vector2(projected.x, Screen.height - projected.y);
                if (projected.z < 0f)
                {
                    screenPoint = center * 2f - screenPoint;
                }
                else if (screenPoint.x >= 20f && screenPoint.x <= Screen.width - 20f &&
                         screenPoint.y >= 20f && screenPoint.y <= Screen.height - 20f)
                {
                    continue;
                }

                var direction = screenPoint - center;
                if (direction.sqrMagnitude < 0.001f) direction = Vector2.up;
                direction.Normalize();
                var scaleX = halfWidth / Mathf.Max(0.001f, Mathf.Abs(direction.x));
                var scaleY = halfHeight / Mathf.Max(0.001f, Mathf.Abs(direction.y));
                var markerCenter = center + direction * Mathf.Min(scaleX, scaleY);
                var rect = new Rect(markerCenter.x - 88f, markerCenter.y - 14f, 176f, 28f);
                rect.x = Mathf.Clamp(rect.x, 4f, Screen.width - rect.width - 4f);
                rect.y = Mathf.Clamp(rect.y, 4f, Screen.height - rect.height - 4f);
                var distance = Vector3.Distance(localPlayer.transform.position, player.Position);
                GUI.Box(rect, (player.PlayerName ?? "PLAYER") + "  " + player.Health + " HP  " + Mathf.RoundToInt(distance) + " m");
            }
        }

        private void UpdateTeamMarkers()
        {
            for (var i = activeTeamMarkers.Count - 1; i >= 0; i--)
                if (Time.unscaledTime >= activeTeamMarkers[i].ExpiresAt) activeTeamMarkers.RemoveAt(i);
            if (!network.IsConnected || showPanel || localPlayer == null || localPlayer.dead ||
                teamMarkerKeyConfig == null || !Input.GetKeyDown(teamMarkerKeyConfig.Value)) return;

            var camera = localPlayer.cc != null ? localPlayer.cc.cam : Camera.main;
            if (camera == null) return;
            unchecked { nextTeamMarkerSequence++; }
            if (nextTeamMarkerSequence == 0) nextTeamMarkerSequence = 1;
            var origin = camera.transform.position;
            var direction = camera.transform.forward;
            var kind = (byte)0;
            Vector3 point;
            RaycastHit hit;
            if (Physics.Raycast(origin, direction, out hit, 250f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                point = hit.point;
                if (hit.collider.GetComponentInParent<EnemyIdentifier>() != null) kind = 1;
            }
            else
            {
                point = origin + direction * 60f;
            }
            var payload = TeamMarkerCodec.EncodeRequest(nextTeamMarkerSequence, point, kind);
            if (network.IsHost) HandleTeamMarkerRequest(network.LocalPlayerId, payload);
            else network.SendToHost(MessageType.TeamMarker, payload);
        }

        private void BroadcastAuthoritativeSceneState()
        {
            if (!network.IsHost || !network.IsConnected || network.Players.Count <= 1 ||
                !network.SceneSnapshotSyncReady || SceneLoadPatch.IsTransitionInProgress ||
                Time.unscaledTime < nextSceneStateBroadcastAt) return;
            var sceneName = SceneHelper.CurrentScene;
            var active = SceneManager.GetActiveScene();
            if (!LevelSceneReadinessPolicy.IsFullyLoaded(sceneName, SceneHelper.CurrentScene,
                SceneHelper.PendingScene, active.IsValid(), active.isLoaded)) return;
            nextSceneStateBroadcastAt = Time.unscaledTime + 3f;
            network.BroadcastReliable(MessageType.JoinSessionState,
                JoinSessionStateCodec.Encode(sceneName, (byte)SessionDifficulty));
        }

        private void HandleTeamMarkerRequest(uint senderId, byte[] payload)
        {
            if (!network.IsHost || senderId == 0 ||
                (senderId != network.LocalPlayerId && !network.HasFreshPlayerSnapshot(senderId, 0.5f))) return;
            PlayerSnapshot player;
            if (!network.Players.TryGetValue(senderId, out player) || player == null || (player.MovementFlags & 4) != 0) return;
            var request = TeamMarkerCodec.DecodeRequest(payload);
            if (!teamMarkerRateLimiter.TryAccept(senderId, Time.unscaledTime) ||
                !teamMarkerSequences.TryAccept(senderId, request.Sequence)) return;
            if ((request.Position - player.Position).sqrMagnitude > 250f * 250f) return;

            var relay = TeamMarkerCodec.EncodeRelay(senderId, request.Sequence, request.Position, request.Kind);
            AddTeamMarker(new TeamMarker { OwnerPlayerId = senderId, Sequence = request.Sequence,
                Position = request.Position, Kind = request.Kind });
            network.BroadcastReliable(MessageType.TeamMarker, relay);
        }

        private void ReceiveTeamMarker(byte[] payload)
        {
            var marker = TeamMarkerCodec.DecodeRelay(payload);
            if (!network.Players.ContainsKey(marker.OwnerPlayerId)) return;
            AddTeamMarker(marker);
        }

        private void AddTeamMarker(TeamMarker marker)
        {
            if (marker == null) return;
            activeTeamMarkers.RemoveAll(item => item.OwnerPlayerId == marker.OwnerPlayerId && item.Sequence == marker.Sequence);
            activeTeamMarkers.Add(new ActiveTeamMarker
            {
                OwnerPlayerId = marker.OwnerPlayerId,
                Sequence = marker.Sequence,
                Position = marker.Position,
                Kind = marker.Kind,
                ExpiresAt = Time.unscaledTime + 8f
            });
        }

        private void DrawTeamMarkers()
        {
            if (activeTeamMarkers.Count == 0 || localPlayer == null) return;
            var camera = localPlayer.cc != null ? localPlayer.cc.cam : Camera.main;
            if (camera == null) return;
            var center = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            foreach (var marker in activeTeamMarkers)
            {
                PlayerSnapshot owner;
                var ownerName = network.Players.TryGetValue(marker.OwnerPlayerId, out owner) && owner != null
                    ? owner.PlayerName : "PLAYER";
                var projection = camera.WorldToScreenPoint(marker.Position);
                var screen = new Vector2(projection.x, Screen.height - projection.y);
                if (projection.z < 0f) screen = center * 2f - screen;
                var visible = projection.z > 0f && screen.x >= 16f && screen.x <= Screen.width - 16f &&
                    screen.y >= 16f && screen.y <= Screen.height - 16f;
                if (!visible)
                {
                    var direction = screen - center;
                    if (direction.sqrMagnitude < 0.001f) direction = Vector2.up;
                    direction.Normalize();
                    var maxX = Mathf.Max(20f, center.x - 92f);
                    var maxY = Mathf.Max(20f, center.y - 24f);
                    var scale = Mathf.Min(maxX / Mathf.Max(0.001f, Mathf.Abs(direction.x)),
                        maxY / Mathf.Max(0.001f, Mathf.Abs(direction.y)));
                    screen = center + direction * scale;
                }
                var distance = Mathf.RoundToInt(Vector3.Distance(localPlayer.transform.position, marker.Position));
                var label = ownerName + (marker.Kind == 1 ? "  ENEMY" : "  LOCATION") + "  " + distance + " m";
                var rect = new Rect(screen.x - 92f, screen.y - 20f, 184f, 40f);
                rect.x = Mathf.Clamp(rect.x, 2f, Screen.width - rect.width - 2f);
                rect.y = Mathf.Clamp(rect.y, 2f, Screen.height - rect.height - 2f);
                GUI.Box(rect, "!\n" + label);
            }
        }

        private void DrawLevelLoadingOverlay()
        {
            EnsureMenuStyles();
            var previousMatrix = GUI.matrix;
            var scale = GetUiScale();
            GUI.matrix = previousMatrix * Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            try
            {
                var screenWidth = Screen.width / scale;
                var screenHeight = Screen.height / scale;
                var cardWidth = Mathf.Min(620f, screenWidth - 32f);
                var cardHeight = Mathf.Min(376f, screenHeight - 16f);
                var card = new Rect((screenWidth - cardWidth) * 0.5f, (screenHeight - cardHeight) * 0.5f,
                    cardWidth, cardHeight);

                GUI.Box(new Rect(0f, 0f, screenWidth, screenHeight), GUIContent.none, scrimStyle);
                GUI.Box(card, GUIContent.none, panelStyle);
                GUILayout.BeginArea(new Rect(card.x + 24f, card.y + 20f, card.width - 48f, card.height - 40f));
                GUILayout.Label("BLOODLINK  //  LEVEL SYNCHRONIZATION", sectionStyle);
                GUILayout.Space(10f);
                GUILayout.Label(SceneLoadPatch.LoadingStatus, loadingStatusStyle, GUILayout.ExpandHeight(true));
                GUILayout.FlexibleSpace();
                if (SceneLoadPatch.CanManuallyReady && GUILayout.Button("I'M READY - START WHEN EVERYONE IS READY", selectedTabStyle, GUILayout.Height(44f)))
                    SceneLoadPatch.MarkLocalReady();
                else if (SceneLoadPatch.IsLocallyReady)
                    GUILayout.Label(SceneLoadPatch.LocalReadyStatus, sectionStyle);
                if (SceneLoadPatch.CanCancelHostTransition && GUILayout.Button(
                    SceneLoadPatch.IsJoinOnlyBarrier ? "CANCEL JOIN SYNC" : "CANCEL LEVEL CHANGE", buttonStyle, GUILayout.Height(36f)))
                    SceneLoadPatch.CancelHostTransitionByUser();
                if (GUILayout.Button("OPEN SESSION MENU", buttonStyle, GUILayout.Height(40f)))
                {
                    SetPanelVisible(true);
                    menuTab = 3;
                }
                if (GUILayout.Button("DISCONNECT AND CLOSE READY SCREEN", buttonStyle, GUILayout.Height(40f)))
                    ExitReadinessScreen();
                GUILayout.EndArea();
            }
            finally
            {
                GUI.matrix = previousMatrix;
            }
        }

        private void DrawCoopHud()
        {
            EnsureMenuStyles();
            GUI.Label(new Rect(16f, 10f, 300f, 20f), "BLOODLINK  //  " + network.PingMilliseconds + " ms" +
                (FriendlyFireEnabled ? "  //  FOGO AMIGO" : string.Empty), labelStyle);
            var row = 0;
            foreach (var player in network.Players.Values.OrderBy(p => p.PlayerId))
            {
                if (player.PlayerId == network.LocalPlayerId) continue;
                var dead = (player.MovementFlags & 4) != 0;
                var y = 38f + row * 78f;
                var card = new Rect(16f, y, Mathf.Min(290f, Screen.width - 32f), 65f);
                DrawHudRect(card, new Color(0.025f, 0.02f, 0.025f, 0.82f));
                var face = new Rect(card.x + 6f, card.y + 9f, 40f, 42f);
                DrawHudRect(new Rect(face.x + 6f, face.y - 5f, 40f, 42f), new Color(0.65f, 0.65f, 0.68f, 1f));
                DrawHudRect(face, Color.white);
                DrawHudRect(new Rect(face.x + 3f, face.y + 3f, face.width - 6f, face.height - 6f), new Color(0.12f, 0.13f, 0.16f));
                // A forward-facing V1 visor inside a raised white portrait frame.
                DrawHudRect(new Rect(face.x + 8f, face.y + 9f, 24f, 20f), dead ? Color.gray : new Color(0.72f, 0.76f, 0.83f));
                DrawHudRect(new Rect(face.x + 10f, face.y + 15f, 20f, 5f), dead ? new Color(0.2f, 0.2f, 0.2f) :
                    PlayerAppearanceColor.Unpack(player.LightColor));
                DrawHudRect(new Rect(face.x + 15f, face.y + 30f, 10f, 3f), Color.white);
                var nameX = card.x + 62f;
                GUI.Label(new Rect(nameX, card.y + 3f, card.width - 70f, 22f), player.PlayerName ?? "PLAYER", hudTextStyle);
                var bar = new Rect(nameX, card.y + 31f, card.width - 75f, 17f);
                DrawHudRect(new Rect(bar.x - 2f, bar.y - 2f, bar.width + 4f, bar.height + 4f), Color.white);
                DrawHudRect(bar, new Color(0.17f, 0.015f, 0.025f));
                var cap = player.Health > 100 ? 200f : 100f;
                DrawHudRect(new Rect(bar.x, bar.y, bar.width * (dead ? 0f : Mathf.Clamp01(player.Health / cap)), bar.height),
                    new Color(0.9f, 0.035f, 0.08f));
                GUI.Label(new Rect(nameX, card.y + 46f, card.width - 70f, 19f),
                    dead ? "ELIMINADO / ESPECTADOR" : player.Health + " HP" +
                    (network.HasFreshPlayerSnapshot(player.PlayerId, 2f) ? string.Empty : "  /  SEM SINAL"), labelStyle);
                row++;
            }
        }

        private static void DrawHudRect(Rect rect, Color color)
        {
            if (rect.width <= 0f || rect.height <= 0f) return;
            var old = GUI.color; GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = old;
        }

        private void SelectMenuTab(int tab)
        {
            if (menuTab == tab) return;
            menuTab = tab;
            panelContentScroll = Vector2.zero;
        }

        private void DrawMultiplayerPanel()
        {
            EnsureMenuStyles();
            if (!loggedPanelDraw)
            {
                loggedPanelDraw = true;
                Logger.LogInfo("Bloodlink panel rendered.");
            }
            var previousMatrix = GUI.matrix;
            var scale = GetUiScale();
            GUI.matrix = previousMatrix * Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            try
            {
                var screenWidth = Screen.width / scale;
                var screenHeight = Screen.height / scale;
                var margin = Mathf.Min(24f, Mathf.Max(6f, screenWidth * 0.02f));
                var width = Mathf.Max(320f, Mathf.Min(1080f, screenWidth - margin * 2f));
                var height = Mathf.Max(280f, Mathf.Min(860f, screenHeight - margin * 2f));
                var panel = new Rect((screenWidth - width) * 0.5f, (screenHeight - height) * 0.5f, width, height);

                GUI.Box(new Rect(0f, 0f, screenWidth, screenHeight), GUIContent.none, scrimStyle);
                GUI.Box(panel, GUIContent.none, panelStyle);
                GUI.DrawTexture(new Rect(panel.x, panel.y, 6f, panel.height), accentTexture);
                GUI.DrawTexture(new Rect(panel.x + 6f, panel.y + 72f, panel.width - 6f, 2f), accentTexture);
                GUI.Box(new Rect(panel.x + 6f, panel.y + 4f, panel.width - 10f, 64f), GUIContent.none, scrimStyle);
                GUI.Label(new Rect(panel.x + 26f, panel.y + 9f, panel.width - 220f, 34f), "BLOODLINK  //  CO-OP", titleStyle);
                GUI.Label(new Rect(panel.x + 28f, panel.y + 43f, panel.width - 240f, 20f),
                    "ULTRAKILL MULTIPLAYER SYSTEM     /     " + PluginVersion, sectionStyle);
                var closeRect = new Rect(panel.x + panel.width - 154f, panel.y + 17f, 128f, 38f);
                if (GUI.Button(closeRect, "CLOSE  [ESC]", buttonStyle))
                {
                    SetPanelVisible(false);
                    Logger.LogInfo("Bloodlink panel closed.");
                    return;
                }

                var contentRect = new Rect(panel.x + 22f, panel.y + 90f, panel.width - 44f, panel.height - 108f);
                GUILayout.BeginArea(contentRect);
                GUILayout.BeginHorizontal();

                var navigationWidth = Mathf.Clamp(width * 0.22f, 138f, 184f);
                GUILayout.BeginVertical(GUILayout.Width(navigationWidth), GUILayout.ExpandHeight(true));
                GUILayout.Label("OPERATIONS", sectionStyle, GUILayout.Height(28f));
                GUILayout.Space(8f);
                if (GUILayout.Button("01   /   CREATE ROOM", menuTab == 0 ? selectedTabStyle : buttonStyle, GUILayout.Height(48f))) SelectMenuTab(0);
                if (GUILayout.Button("02   /   JOIN / FIND", menuTab == 1 ? selectedTabStyle : buttonStyle, GUILayout.Height(48f))) SelectMenuTab(1);
                if (GUILayout.Button("03   /   CUSTOMIZE V1", menuTab == 2 ? selectedTabStyle : buttonStyle, GUILayout.Height(48f))) SelectMenuTab(2);
                if (GUILayout.Button("04   /   DIAGNOSTICS", menuTab == 3 ? selectedTabStyle : buttonStyle, GUILayout.Height(48f))) SelectMenuTab(3);
                GUILayout.FlexibleSpace();
                GUILayout.Label(network.IsConnected ? "LINK  /  ONLINE" : network.IsRunning ? "LINK  /  WAITING" : "LINK  /  OFFLINE", sectionStyle);
                GUILayout.Label("MENU KEY  " + menuKeyConfig.Value + "\nMOUSE  /  FREE TO CLICK", labelStyle);
                GUILayout.EndVertical();

                GUILayout.Space(18f);
                GUILayout.BeginVertical(GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
                GUILayout.BeginVertical(panelStyle, GUILayout.ExpandWidth(true));
                GUILayout.Label("SESSION STATUS   //   " + status, statusStyle, GUILayout.MinHeight(38f), GUILayout.ExpandWidth(true));
                GUILayout.EndVertical();
                GUILayout.Space(8f);
                DrawPrimaryActions();
                GUILayout.Space(8f);
                panelContentScroll = GUILayout.BeginScrollView(panelContentScroll, GUILayout.ExpandHeight(true), GUILayout.ExpandWidth(true));
                if (menuTab == 0) DrawCreateRoomTab();
                else if (menuTab == 1) DrawJoinRoomTab();
                else if (menuTab == 2) DrawAppearanceTab();
                else DrawDiagnosticsTab();
                GUILayout.EndScrollView();
                GUILayout.EndVertical();

                GUILayout.EndHorizontal();
                GUILayout.EndArea();
            }
            finally
            {
                GUI.matrix = previousMatrix;
            }
        }

        private void DrawPrimaryActions()
        {
            GUILayout.BeginHorizontal();
            if (!network.IsRunning)
            {
                if (GUILayout.Button("HOST GAME", selectedTabStyle, GUILayout.Height(44f)))
                {
                    SelectMenuTab(0);
                    StartHost();
                }
                if (GUILayout.Button("SEARCH LAN", selectedTabStyle, GUILayout.Height(44f)))
                {
                    SelectMenuTab(1);
                    lobbyScroll = Vector2.zero;
                    DiscoverLan();
                }
            }
            else if (network.IsDiscovering)
            {
                if (GUILayout.Button("STOP SEARCH", buttonStyle, GUILayout.Height(44f))) DisconnectSession();
                if (GUILayout.Button("HOST GAME", selectedTabStyle, GUILayout.Height(44f)))
                {
                    SelectMenuTab(0);
                    StartHost();
                }
            }
            else
            {
                if (network.CanRetryConnection)
                {
                    if (GUILayout.Button("RETRY CONNECTION", selectedTabStyle, GUILayout.Height(44f)))
                        RetryClientConnection();
                    if (GUILayout.Button("CANCEL", buttonStyle, GUILayout.Height(44f))) DisconnectSession();
                    GUILayout.EndHorizontal();
                    return;
                }
                if (network.IsHost)
                {
                    if (SceneLoadPatch.CanManuallyReady && GUILayout.Button("I'M READY", selectedTabStyle, GUILayout.Height(44f)))
                        SceneLoadPatch.MarkLocalReady();
                    else if (SceneLoadPatch.IsLocallyReady)
                        GUILayout.Label("READY - WAITING FOR PLAYERS", sectionStyle, GUILayout.ExpandWidth(true));
                    if (SceneLoadPatch.CanCancelHostTransition && GUILayout.Button(
                        SceneLoadPatch.IsJoinOnlyBarrier ? "CANCEL JOIN SYNC" : "CANCEL CHANGE", buttonStyle, GUILayout.Height(44f)))
                        SceneLoadPatch.CancelHostTransitionByUser();
                    if (!SceneLoadPatch.IsTransitionInProgress)
                    {
                        if (GUILayout.Button("LOAD SELECTED LEVEL", buttonStyle, GUILayout.Height(44f)))
                            LoadSelectedStartLevel();
                    }
                }
                else if (SceneLoadPatch.CanManuallyReady && GUILayout.Button("I'M READY", selectedTabStyle, GUILayout.Height(44f)))
                    SceneLoadPatch.MarkLocalReady();
                else if (!network.IsHost && SceneLoadPatch.IsLocallyReady)
                    GUILayout.Label("READY - WAITING FOR HOST", sectionStyle, GUILayout.ExpandWidth(true));
                var disconnectLabel = network.IsHost ? "CLOSE ROOM" : network.IsConnected ? "LEAVE ROOM" :
                    network.IsReconnecting ? "CANCEL RECONNECT" : "CANCEL CONNECTION";
                if (GUILayout.Button(disconnectLabel, buttonStyle, GUILayout.Height(44f))) DisconnectSession();
            }
            GUILayout.EndHorizontal();
            if (network.IsConnected)
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("RESYNC WORLD + REBUILD V1", buttonStyle, GUILayout.Height(36f))) RequestWorldResync();
                GUILayout.Label(worldObjectSync.Diagnostics + "  /  " + sceneObjectStateSync.Diagnostics + "  /  " + ItemSync.Diagnostics, sectionStyle);
                GUILayout.EndHorizontal();
            }
        }

        private void RequestWorldResync()
        {
            if (!network.IsConnected || Time.unscaledTime < nextManualResyncAt) return;
            nextManualResyncAt = Time.unscaledTime + 3f;
            ClearRemotePlayers();
            if (network.IsHost)
            {
                foreach (var player in network.Players.Values)
                    if (player.PlayerId != network.LocalPlayerId) SendInitialEnemySnapshot(player.PlayerId);
            }
            else network.SendToHost(MessageType.ResyncRequest, Array.Empty<byte>());
            Logger.LogInfo("Manual world resynchronization requested. " + worldObjectSync.Diagnostics + " " + ItemSync.Diagnostics);
        }

        private void DisconnectSession()
        {
            EnemyMeleeSync.Reset();
            worldObjectSync.Reset();
            sceneObjectStateSync.Reset();
            ItemSync.Reset();
            SceneLoadPatch.ResetSession();
            enemyProjectileSync.Reset();
            enemyExplosionSync.Reset();
            doorStateSync.Reset();
            weaponPickupSync.Reset();
            activeTeamMarkers.Clear();
            teamMarkerSequences.Clear();
            teamMarkerRateLimiter.Clear();
            fireRateLimiter.Clear();
            animationRateLimiter.Clear();
            coinProjectileRateLimiter.Clear();
            nailProjectileRateLimiter.Clear();
            rocketProjectileRateLimiter.Clear();
            damageRequestSequences.Clear();
            damageRequestRateLimiter.Clear();
            resyncRequestRateLimiter.Clear();
            playerVitalsRequestSequences.Clear();
            playerVitalsRateLimiter.Clear();
            PlayerVitalsRequestAuthority.Reset();
            pendingDamage.Clear();
            pendingLocalVitals = null;
            pendingSceneVitalsReset = false;
            teamWipeTimer = 0f;
            restartRequested = false;
            ClearRemotePlayers();
            network.Stop();
            SceneLoadPatch.ReleaseOverlayAfterDisconnect(this);
            SetLoadingOverlayInputState(false);
            RestoreBackgroundExecution();
            status = "Offline";
            Logger.LogInfo("Bloodlink session stopped.");
        }

        private void ExitReadinessScreen()
        {
            DisconnectSession();
            SetPanelVisible(false);
            Logger.LogInfo("Session disconnected and level readiness overlay closed.");
        }

        internal void DisconnectAfterTransitionFailure(string message)
        {
            DisconnectSession();
            status = message;
        }

        private void RetryClientConnection()
        {
            var relay = network.IsRelaySession;
            DisconnectSession();
            if (relay) StartRelayClient();
            else StartClient();
        }

        private void DrawCreateRoomTab()
        {
            GUILayout.Label("HOST SETTINGS", sectionStyle);
            var previousEnabled = GUI.enabled;
            GUI.enabled = !network.IsRunning || network.IsHost;
            var shownFriendlyFire = network.IsConnected && !network.IsHost ? FriendlyFireEnabled : friendlyFireConfig.Value;
            var friendlyFire = GUILayout.Toggle(shownFriendlyFire, "FOGO AMIGO  /  permitir dano entre jogadores");
            if ((!network.IsRunning || network.IsHost) && friendlyFire != friendlyFireConfig.Value)
            {
                friendlyFireConfig.Value = friendlyFire;
                if (network.IsHost) network.BroadcastReliable(MessageType.SessionRules, new[] { (byte)(friendlyFire ? 1 : 0) });
            }
            GUI.enabled = previousEnabled;
            if (network.IsConnected && !network.IsHost) GUILayout.Label("FOGO AMIGO: " + (FriendlyFireEnabled ? "ATIVADO" : "DESATIVADO") + "  /  controlado pelo host", labelStyle);
            DrawTextField("PLAYER NAME", ref playerName, 24);
            DrawTextField("ROOM NAME", ref lobbyName, 40);
            DrawTextField("BIND IPv4", ref bindAddress, 15);
            GUILayout.Label("Use 0.0.0.0 for all adapters, or enter a LAN / VPN adapter address.", labelStyle);
            DrawTextField("PASSWORD", ref password, 32, true);
            GUILayout.BeginHorizontal();
            GUILayout.Label("MAX PLAYERS", labelStyle, GUILayout.Width(150f));
            if (GUILayout.Button("-", buttonStyle, GUILayout.Width(40f))) maxPlayers = Math.Max(2, maxPlayers - 1);
            GUILayout.Label(maxPlayers + " PLAYERS", labelStyle, GUILayout.Width(120f));
            if (GUILayout.Button("+", buttonStyle, GUILayout.Width(40f))) maxPlayers = Math.Min(4, maxPlayers + 1);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("VISIBILITY", labelStyle, GUILayout.Width(150f));
            if (GUILayout.Button(isPublic ? "PUBLIC LAN ROOM" : "PRIVATE ROOM", buttonStyle, GUILayout.Width(220f))) isPublic = !isPublic;
            GUILayout.EndHorizontal();
            GUILayout.Space(8f);
            GUILayout.Label("ONLINE RELAY", sectionStyle);
            GUILayout.Label("Create an internet room with a short join code. LAN and direct IP hosting stay available above.", labelStyle);
            if (!network.IsRunning && GUILayout.Button(relayStartPending ? "CREATING RELAY ROOM..." : "HOST WITH UNITY RELAY",
                selectedTabStyle, GUILayout.Height(42f)) && !relayStartPending) StartRelayHost();
            if (network.IsRunning && network.IsHost && !string.IsNullOrEmpty(network.RelayJoinCode))
            {
                GUILayout.Label("RELAY JOIN CODE  " + network.RelayJoinCode, selectedTabStyle);
                if (GUILayout.Button("COPY JOIN CODE", buttonStyle, GUILayout.Height(34f))) GUIUtility.systemCopyBuffer = network.RelayJoinCode;
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label("SESSION DIFFICULTY", labelStyle, GUILayout.Width(150f));
            if (GUILayout.Button("-", buttonStyle, GUILayout.Width(40f)))
                sessionDifficultyConfig.Value = Mathf.Max(0, sessionDifficultyConfig.Value - 1);
            GUILayout.Label(((GameDifficulty)Mathf.Clamp(sessionDifficultyConfig.Value, 0, 4)).GetDifficultyName(), labelStyle, GUILayout.Width(170f));
            if (GUILayout.Button("+", buttonStyle, GUILayout.Width(40f)))
                sessionDifficultyConfig.Value = Mathf.Min(4, sessionDifficultyConfig.Value + 1);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("CO-OP PROFILE", labelStyle, GUILayout.Width(150f));
            if (GUILayout.Button("<", buttonStyle, GUILayout.Width(40f)))
                cooperativeModeConfig.Value = (Mathf.Clamp(cooperativeModeConfig.Value, 0, 2) + 2) % 3;
            var profileName = cooperativeModeConfig.Value == 0 ? "CLASSIC CO-OP" :
                cooperativeModeConfig.Value == 1 ? "BALANCED CO-OP" : "CHAOS CO-OP";
            GUILayout.Label(profileName, labelStyle, GUILayout.Width(170f));
            if (GUILayout.Button(">", buttonStyle, GUILayout.Width(40f)))
                cooperativeModeConfig.Value = (Mathf.Clamp(cooperativeModeConfig.Value, 0, 2) + 1) % 3;
            GUILayout.EndHorizontal();
            GUILayout.Label("Enemy durability scales with the number of connected players.", labelStyle);
            GUILayout.Space(8f);
            GUILayout.Label("INITIAL CAMPAIGN LEVEL", sectionStyle);
            var missionNumber = GetMissionName.GetMissionNumberOnly(startMissionNumber);
            var missionName = GetMissionName.GetMissionNameOnly(startMissionNumber);
            GUILayout.Label(missionNumber + "  " + missionName, labelStyle);
            startMissionScroll = GUILayout.BeginScrollView(startMissionScroll, GUILayout.Height(118f));
            var missions = availableMissions;
            var missionColumns = Screen.width / GetUiScale() < 900f ? 2 : 3;
            var missionButtonHeight = missionColumns == 2 ? 40f : 30f;
            for (var row = 0; row < missions.Length; row += missionColumns)
            {
                GUILayout.BeginHorizontal();
                for (var column = 0; column < missionColumns && row + column < missions.Length; column++)
                {
                    var mission = missions[row + column];
                    var label = GetMissionName.GetMissionNumberOnly(mission) + "  " + GetMissionName.GetMissionNameOnly(mission);
                    if (GUILayout.Button(label, startMissionNumber == mission ? selectedTabStyle : buttonStyle,
                        GUILayout.Height(missionButtonHeight)))
                        startMissionNumber = mission;
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            GUILayout.Space(10f);
            if (!network.IsRunning) GUILayout.Label("Set your options above, then choose HOST GAME.", labelStyle);
            else if (network.IsHost)
                GUILayout.Label("Use LOAD SELECTED LEVEL above when the party is ready.", labelStyle);
            if (network.IsHost || network.IsConnected) DrawPlayerRoster();
        }

        private static int[] BuildAvailableMissions()
        {
            var missions = new List<int>(48);
            for (var mission = 1; mission <= 35; mission++) missions.Add(mission);
            for (var mission = 100; mission <= 109; mission++) missions.Add(mission);
            missions.Add(666);
            missions.Add(667);
            missions.Add(668);
            missions.RemoveAll(mission => string.IsNullOrEmpty(GetMissionName.GetSceneName(mission)));
            return missions.ToArray();
        }

        private void LoadSelectedStartLevel()
        {
            if (SceneLoadPatch.IsTransitionInProgress)
            {
                status = "A level transition is already in progress.";
                return;
            }
            var sceneName = GetMissionName.GetSceneName(startMissionNumber);
            if (string.IsNullOrEmpty(sceneName))
            {
                status = "Selected mission is not available in this game build.";
                return;
            }
            if (!network.IsHost || !network.IsConnected) return;
            try
            {
                SessionDifficulty = Mathf.Clamp(sessionDifficultyConfig.Value, 0, 4);
                SessionDifficultyOverride.Apply(SessionDifficulty);
                CooperativeMode = Mathf.Clamp(cooperativeModeConfig.Value, 0, 2);
                SceneHelper.LoadSceneAsync(sceneName);
                status = "Host is loading " + GetMissionName.GetMissionNumberOnly(startMissionNumber) + ".";
            }
            catch (Exception ex)
            {
                status = "Could not load selected level: " + ex.Message;
                Logger.LogError(status);
            }
        }

        private void EnableBackgroundExecution()
        {
            if (!hasSavedRunInBackground)
            {
                previousRunInBackground = Application.runInBackground;
                hasSavedRunInBackground = true;
            }
            Application.runInBackground = true;
        }

        private void RestoreBackgroundExecution()
        {
            if (!hasSavedRunInBackground) return;
            Application.runInBackground = previousRunInBackground;
            hasSavedRunInBackground = false;
        }

        private void DrawJoinRoomTab()
        {
            GUILayout.Label("JOIN WITH UNITY RELAY", sectionStyle);
            DrawTextField("RELAY JOIN CODE", ref relayJoinCode, 12);
            if (!network.IsRunning && GUILayout.Button("JOIN RELAY ROOM", selectedTabStyle, GUILayout.Height(40f))) StartRelayClient();
            GUILayout.Label("JOIN BY ADDRESS", sectionStyle);
            DrawTextField("PLAYER NAME", ref playerName, 24);
            DrawTextField("HOST IP / VPN IP", ref address, 48);
            DrawTextField("UDP PORT", ref portText, 5);
            DrawTextField("ROOM PASSWORD", ref password, 32, true);
            GUILayout.BeginHorizontal();
            if (!network.IsHost && (!network.IsRunning || network.IsDiscovering) &&
                GUILayout.Button("CONNECT BY IP", buttonStyle, GUILayout.Height(40f))) StartClient();
            if (!network.IsHost && (!network.IsRunning || network.IsDiscovering) &&
                GUILayout.Button(network.IsDiscovering ? "REFRESH LAN SEARCH" : "FIND LAN ROOMS", buttonStyle, GUILayout.Height(40f)))
            {
                lobbyScroll = Vector2.zero;
                DiscoverLan();
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(8f);
            GUILayout.Label("DETECTED ROOMS", sectionStyle);
            GUILayout.Label(network.IsDiscovering ? "Scanning the local network..." : "Rooms found: " + network.DiscoveredLobbies.Count, labelStyle);
            lobbyScroll = GUILayout.BeginScrollView(lobbyScroll, GUILayout.Height(160f));
            var canJoinDiscovered = !network.IsHost && (!network.IsRunning || network.IsDiscovering);
            foreach (var lobby in network.DiscoveredLobbies.ToArray())
            {
                var lockLabel = lobby.RequiresPassword ? " [LOCKED]" : string.Empty;
                var label = $"JOIN  {lobby.Name}{lockLabel}   {lobby.PlayerCount}/{lobby.MaxPlayers}   {lobby.Address}:{lobby.Port}";
                if (canJoinDiscovered && GUILayout.Button(label, buttonStyle, GUILayout.Height(44f))) JoinLobby(lobby);
            }
            GUILayout.EndScrollView();
            if (network.IsConnected) DrawPlayerRoster();
        }

        private void DrawDiagnosticsTab()
        {
            GUILayout.Label("LIVE CONNECTION", sectionStyle);
            var connectionState = network.IsReconnecting ? "RECONNECTING" : network.IsConnected ? "CONNECTED" :
                network.CanRetryConnection ? "CONNECTION FAILED" : network.IsRunning ? "CONNECTING" : "OFFLINE";
            var connectionRole = network.IsHost ? "HOST" : network.IsConnected ? "CLIENT" :
                network.CanRetryConnection ? "CLIENT (FAILED)" : network.IsRunning ? "CLIENT (CONNECTING)" : "OFFLINE";
            GUILayout.Label("State: " + connectionState, labelStyle);
            GUILayout.Label("Role: " + connectionRole, labelStyle);
            GUILayout.Label("Players: " + network.Players.Count + " / " + (network.IsHost ? maxPlayers.ToString() : "4"), labelStyle);
            GUILayout.Label("UDP port: " + (network.ListenPort == 0 ? portText : network.ListenPort.ToString()), labelStyle);
            GUILayout.Space(8f);
            GUILayout.Label("ROUND TRIP TIME  " + network.PingMilliseconds + " ms", sectionStyle);
            GUILayout.Label("RTT P95: " + network.Rtt95Milliseconds + " ms", labelStyle);
            GUILayout.Label("Jitter EWMA: " + network.JitterMilliseconds.ToString("0.0") + " ms", labelStyle);
            GUILayout.Label("Snapshot loss estimate: " + network.PacketLossPercent.ToString("0.0") + "%", labelStyle);
            GUILayout.Label("Snapshot TX / RX: " + network.SnapshotSendRate.ToString("0.0") + " / " + network.SnapshotReceiveRate.ToString("0.0") + " per s", labelStyle);
            GUILayout.Label("Packet TX / RX: " + network.PacketSendRate.ToString("0.0") + " / " + network.PacketReceiveRate.ToString("0.0") + " per s", labelStyle);
            GUILayout.Label("Traffic TX / RX: " + network.SentBytesPerSecond.ToString("0") + " / " + network.ReceivedBytesPerSecond.ToString("0") + " B/s", labelStyle);
            if (!string.IsNullOrEmpty(network.LastError)) GUILayout.Label("Last error: " + network.LastError, labelStyle);
            DrawPlayerRoster();
        }

        private void DrawAppearanceTab()
        {
            GUILayout.Label("V1 COLOR PROFILE", sectionStyle);
            GUILayout.Label("Colors are saved locally and sent with your player snapshots.", labelStyle);
            GUILayout.Space(8f);
            DrawTextField("PRIMARY  #RRGGBB", ref primaryColorText, 7);
            DrawTextField("SECONDARY  #RRGGBB", ref secondaryColorText, 7);
            DrawTextField("LIGHTS  #RRGGBB", ref lightColorText, 7);
            DrawInteractiveColorPicker();
            GUILayout.BeginHorizontal();
            GUILayout.Label("GLOW INTENSITY", labelStyle, GUILayout.Width(150f));
            var sliderWidth = Mathf.Max(100f, Mathf.Min(260f, (Screen.width / GetUiScale() - 128f) * 0.3f));
            var glow = GUILayout.HorizontalSlider(glowIntensityConfig.Value, 0f, 255f, GUILayout.Width(sliderWidth));
            glowIntensityConfig.Value = Mathf.RoundToInt(glow);
            GUILayout.Label(glowIntensityConfig.Value.ToString(), labelStyle, GUILayout.Width(40f));
            GUILayout.EndHorizontal();

            GUILayout.Space(8f);
            GUILayout.Label("PRESETS", sectionStyle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("BLOOD", buttonStyle)) ApplyAppearancePreset("#CC2F3D", "#30343B", "#75D9FF", 96);
            if (GUILayout.Button("CERULEAN", buttonStyle)) ApplyAppearancePreset("#236B9B", "#152D43", "#54D9FF", 180);
            if (GUILayout.Button("GOLD", buttonStyle)) ApplyAppearancePreset("#C99A32", "#46351E", "#FFF0A6", 150);
            GUILayout.EndHorizontal();
            UpdateAppearanceConfig();
            GUILayout.Space(8f);
            GUILayout.BeginHorizontal();
            GUILayout.Label("LIVE V1 PREVIEW", sectionStyle, GUILayout.Width(220f));
            if (appearancePreview != null && !appearancePreview.IsAvailable)
            {
                appearancePreview.Dispose();
                appearancePreview = null;
            }
            if (appearancePreview == null)
            {
                var template = FindObjectOfType<PlayerAnimations>();
                if (template != null) appearancePreview = V1AppearancePreview.Create(template);
            }
            if (appearancePreview != null)
            {
                if (Event.current.type == EventType.Repaint)
                    appearancePreview.Render(localPrimaryColor, localSecondaryColor, localLightColor, localGlowIntensity,
                        previewWeaponSlot, previewWeaponVariation);
                GUILayout.Label(appearancePreview.Texture, GUILayout.Width(176f), GUILayout.Height(176f));
                HandlePreviewInteraction(GUILayoutUtility.GetLastRect());
            }
            else
            {
                GUILayout.Label("V1 preview becomes available when the game's player model is loaded.", labelStyle, GUILayout.Width(280f));
            }
            GUILayout.EndHorizontal();
            if (appearancePreview != null)
            {
                GUILayout.Label("ARRASTE PARA GIRAR  /  RODA DO MOUSE PARA ZOOM", labelStyle);
                GUILayout.BeginHorizontal();
                appearancePreview.AutoRotate = GUILayout.Toggle(appearancePreview.AutoRotate, "GIRAR AUTOMATICAMENTE");
                if (GUILayout.Button("ZOOM +", buttonStyle)) appearancePreview.Zoom(-0.3f);
                if (GUILayout.Button("ZOOM -", buttonStyle)) appearancePreview.Zoom(0.3f);
                if (GUILayout.Button("RESTAURAR VISAO", buttonStyle)) appearancePreview.ResetView();
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                foreach (var slot in new byte[] { 1, 2, 3, 4, 5 })
                    if (GUILayout.Button(new[] { "", "REVOLVER", "SHOTGUN", "NAILGUN", "RAILCANNON", "ROCKET" }[slot],
                        previewWeaponSlot == slot ? selectedTabStyle : buttonStyle)) previewWeaponSlot = slot;
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("VARIANTE " + (previewWeaponVariation + 1), buttonStyle)) previewWeaponVariation = (byte)((previewWeaponVariation + 1) % 3);
                if (GUILayout.Button("SOCO", buttonStyle)) appearancePreview.PlayAction(PlayerAnimationAction.Punch);
                if (GUILayout.Button("MOEDA", buttonStyle)) appearancePreview.PlayAction(PlayerAnimationAction.CoinToss);
                GUILayout.EndHorizontal();
            }
            GUILayout.Label("INVALID HEX VALUES FALL BACK TO THE BLOODLINK DEFAULT.", labelStyle);
        }

        private void HandlePreviewInteraction(Rect rect)
        {
            var current = Event.current;
            var control = GUIUtility.GetControlID(FocusType.Passive);
            if (current.type == EventType.MouseDown && current.button == 0 && rect.Contains(current.mousePosition))
            { GUIUtility.hotControl = control; current.Use(); }
            else if (current.type == EventType.MouseDrag && GUIUtility.hotControl == control)
            { appearancePreview.Rotate(current.delta.x * 0.8f); current.Use(); }
            else if (current.type == EventType.MouseUp && GUIUtility.hotControl == control)
            { GUIUtility.hotControl = 0; current.Use(); }
            else if (current.type == EventType.ScrollWheel && rect.Contains(current.mousePosition))
            { appearancePreview.Zoom(current.delta.y * 0.12f); current.Use(); }
        }

        private void DrawInteractiveColorPicker()
        {
            GUILayout.BeginHorizontal();
            var parts = new[] { "ARMADURA", "DETALHES", "LUZES" };
            for (var i = 0; i < parts.Length; i++)
                if (GUILayout.Button(parts[i], appearanceColorPart == i ? selectedTabStyle : buttonStyle)) appearanceColorPart = i;
            GUILayout.EndHorizontal();
            var html = appearanceColorPart == 0 ? primaryColorText : appearanceColorPart == 1 ? secondaryColorText : lightColorText;
            Color color;
            if (!ColorUtility.TryParseHtmlString(html, out color)) color = Color.white;
            var old = color;
            color.r = DrawColorSlider("R", color.r);
            color.g = DrawColorSlider("G", color.g);
            color.b = DrawColorSlider("B", color.b);
            GUILayout.BeginHorizontal();
            foreach (var swatch in new[] { "#CC2F3D", "#236B9B", "#C99A32", "#57CB86", "#9955DD", "#F0F0F0", "#25252D", "#FF843D" })
            {
                Color sample; ColorUtility.TryParseHtmlString(swatch, out sample);
                var rect = GUILayoutUtility.GetRect(26f, 24f, GUILayout.Width(26f));
                var before = GUI.color; GUI.color = sample;
                GUI.DrawTexture(rect, Texture2D.whiteTexture); GUI.color = before;
                if (GUI.Button(rect, GUIContent.none, GUIStyle.none)) color = sample;
            }
            if (GUILayout.Button("ALEATORIO", buttonStyle)) color = UnityEngine.Random.ColorHSV(0f, 1f, 0.5f, 1f, 0.45f, 1f);
            GUILayout.EndHorizontal();
            if (color != old)
            {
                var selected = "#" + ColorUtility.ToHtmlStringRGB(color);
                if (appearanceColorPart == 0) primaryColorText = selected;
                else if (appearanceColorPart == 1) secondaryColorText = selected;
                else lightColorText = selected;
            }
        }

        private float DrawColorSlider(string channel, float value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(channel, labelStyle, GUILayout.Width(20f));
            var next = GUILayout.HorizontalSlider(value, 0f, 1f);
            GUILayout.Label(Mathf.RoundToInt(next * 255f).ToString(), labelStyle, GUILayout.Width(36f));
            GUILayout.EndHorizontal();
            return next;
        }

        private void ApplyAppearancePreset(string primary, string secondary, string light, int glow)
        {
            primaryColorText = primaryColorConfig.Value = primary;
            secondaryColorText = secondaryColorConfig.Value = secondary;
            lightColorText = lightColorConfig.Value = light;
            glowIntensityConfig.Value = glow;
            UpdateAppearanceConfig();
        }

        private void UpdateAppearanceConfig()
        {
            if (primaryColorConfig == null) return;
            if (primaryColorText != primaryColorConfig.Value) primaryColorConfig.Value = primaryColorText;
            if (secondaryColorText != secondaryColorConfig.Value) secondaryColorConfig.Value = secondaryColorText;
            if (lightColorText != lightColorConfig.Value) lightColorConfig.Value = lightColorText;
            glowIntensityConfig.Value = Mathf.Clamp(glowIntensityConfig.Value, 0, 255);
            localPrimaryColor = ParseAppearanceColor(primaryColorConfig.Value, PlayerAppearanceColor.DefaultPrimary);
            localSecondaryColor = ParseAppearanceColor(secondaryColorConfig.Value, PlayerAppearanceColor.DefaultSecondary);
            localLightColor = ParseAppearanceColor(lightColorConfig.Value, PlayerAppearanceColor.DefaultLight);
            localGlowIntensity = (byte)glowIntensityConfig.Value;
        }

        private uint ParseAppearanceColor(string html, uint fallback)
        {
            Color color;
            return ColorUtility.TryParseHtmlString(html, out color) ? PlayerAppearanceColor.Pack(color) : fallback;
        }

        private void DrawPlayerRoster()
        {
            GUILayout.Space(8f);
            GUILayout.Label("SESSION ROSTER", sectionStyle);
            foreach (var player in network.Players.Values.OrderBy(p => p.PlayerId))
            {
                var life = (player.MovementFlags & 4) != 0 ? "DEAD" : player.Health + " HP";
                GUILayout.Label("#" + player.PlayerId + "  " + player.PlayerName + "   //   " + life, labelStyle);
            }
            if (spectator.IsActive) GUILayout.Label(spectator.Status, labelStyle);
        }

        private void DrawTextField(string label, ref string value, int maxLength, bool secret = false)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, labelStyle, GUILayout.Width(150f));
            value = secret
                ? GUILayout.PasswordField(value, '*', maxLength, textFieldStyle, GUILayout.Height(32f))
                : GUILayout.TextField(value, maxLength, textFieldStyle, GUILayout.Height(32f));
            GUILayout.EndHorizontal();
        }

        private void EnsureMenuStyles()
        {
            if (panelStyle != null) return;
            panelTexture = MakeMenuTexture(new Color(0.035f, 0.025f, 0.03f, 0.98f));
            accentTexture = MakeMenuTexture(new Color(0.42f, 0.025f, 0.04f, 1f));
            accentHoverTexture = MakeMenuTexture(new Color(0.68f, 0.035f, 0.055f, 1f));
            fieldTexture = MakeMenuTexture(new Color(0.09f, 0.075f, 0.08f, 1f));
            scrimTexture = MakeMenuTexture(new Color(0.015f, 0.01f, 0.015f, 0.72f));
            panelStyle = new GUIStyle(GUI.skin.box) { normal = { background = panelTexture }, border = new RectOffset(2, 2, 2, 2) };
            scrimStyle = new GUIStyle(GUI.skin.box) { normal = { background = scrimTexture }, border = new RectOffset(0, 0, 0, 0) };
            titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 24, fontStyle = FontStyle.Bold, normal = { textColor = new Color(1f, 0.2f, 0.23f) } };
            sectionStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, normal = { textColor = new Color(1f, 0.38f, 0.36f) } };
            labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = true, normal = { textColor = new Color(0.88f, 0.84f, 0.84f) } };
            statusStyle = new GUIStyle(labelStyle) { wordWrap = true, clipping = TextClipping.Clip };
            loadingStatusStyle = new GUIStyle(labelStyle)
            {
                fontSize = 17,
                wordWrap = true,
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = Color.white }
            };
            hudTextStyle = new GUIStyle(labelStyle)
            {
                fontSize = 12,
                wordWrap = false,
                clipping = TextClipping.Clip,
                normal = { textColor = Color.white }
            };
            buttonStyle = MakeButtonStyle(accentTexture, accentHoverTexture);
            selectedTabStyle = MakeButtonStyle(accentHoverTexture, accentHoverTexture);
            textFieldStyle = new GUIStyle(GUI.skin.textField) { fontSize = 14, normal = { background = fieldTexture, textColor = Color.white }, focused = { background = fieldTexture, textColor = Color.white } };
        }

        private static float GetUiScale()
        {
            var widthScale = Screen.width / 1280f;
            var heightScale = Screen.height / 720f;
            return Mathf.Clamp(Mathf.Min(widthScale, heightScale), 0.65f, 1f);
        }

        private static GUIStyle MakeButtonStyle(Texture2D normal, Texture2D hover)
        {
            return new GUIStyle(GUI.skin.button)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                wordWrap = true,
                alignment = TextAnchor.MiddleCenter,
                normal = { background = normal, textColor = Color.white },
                hover = { background = hover, textColor = Color.white },
                active = { background = hover, textColor = Color.white }
            };
        }

        private static Texture2D MakeMenuTexture(Color color)
        {
            var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            texture.hideFlags = HideFlags.HideAndDontSave;
            texture.filterMode = FilterMode.Point;
            texture.SetPixel(0, 0, color);
            texture.Apply();
            return texture;
        }

        private void StartHost()
        {
            try
            {
                EnableBackgroundExecution();
                int port;
                if (!int.TryParse(portText, out port)) throw new ArgumentException("Port must be a number.");
                network.LobbyName = lobbyName;
                SessionDifficulty = Mathf.Clamp(sessionDifficultyConfig.Value, 0, 4);
                CooperativeMode = Mathf.Clamp(cooperativeModeConfig.Value, 0, 2);
                SceneLoadPatch.ResetSession();
                enemyProjectileSync.Reset();
                activeTeamMarkers.Clear();
                teamMarkerSequences.Clear();
                teamMarkerRateLimiter.Clear();
                nextTeamMarkerSequence = 0;
                damageRequestSequences.Clear();
                playerVitalsRequestSequences.Clear();
                PlayerVitalsRequestAuthority.Reset();
                damageRequestRateLimiter.Clear();
            resyncRequestRateLimiter.Clear();
                nextDamageRequestSequence = 0;
                enemyExplosionSync.Reset();
                doorStateSync.Reset();
                weaponPickupSync.Reset();
                ResetSecretBonusSession();
                network.StartHost(port, maxPlayers, playerName, password, isPublic, bindAddress);
                SessionDifficultyOverride.Apply(SessionDifficulty);
            }
            catch (Exception ex)
            {
                if (!network.IsRunning) RestoreBackgroundExecution();
                status = "Host failed: " + ex.Message;
                Logger.LogError(status);
            }
        }

        private async void StartRelayHost()
        {
            relayStartPending = true;
            try
            {
                EnableBackgroundExecution();
                network.LobbyName = lobbyName;
                SessionDifficulty = Mathf.Clamp(sessionDifficultyConfig.Value, 0, 4);
                CooperativeMode = Mathf.Clamp(cooperativeModeConfig.Value, 0, 2);
                SceneLoadPatch.ResetSession();
                enemyProjectileSync.Reset();
                enemyExplosionSync.Reset();
                doorStateSync.Reset();
                weaponPickupSync.Reset();
                ResetSecretBonusSession();
                await network.StartRelayHostAsync(relayProjectIdConfig.Value, maxPlayers, playerName);
                SessionDifficultyOverride.Apply(SessionDifficulty);
                status = "Relay room ready. Share code: " + network.RelayJoinCode;
            }
            catch (Exception ex)
            {
                if (!network.IsRunning) RestoreBackgroundExecution();
                status = "Relay host failed: " + ex.Message;
                Logger.LogError(status + Environment.NewLine + ex);
            }
            finally { relayStartPending = false; }
        }

        private async void StartRelayClient()
        {
            try
            {
                EnableBackgroundExecution();
                SceneLoadPatch.ResetSession();
                damageRequestSequences.Clear();
                playerVitalsRequestSequences.Clear();
                PlayerVitalsRequestAuthority.Reset();
                damageRequestRateLimiter.Clear();
            resyncRequestRateLimiter.Clear();
                nextDamageRequestSequence = 0;
                enemyProjectileSync.Reset();
                enemyExplosionSync.Reset();
                doorStateSync.Reset();
                weaponPickupSync.Reset();
                ResetSecretBonusSession();
                await network.StartRelayClientAsync(relayProjectIdConfig.Value, relayJoinCode, playerName);
                status = "Connecting through Unity Relay...";
            }
            catch (Exception ex)
            {
                if (!network.IsRunning) RestoreBackgroundExecution();
                status = "Relay join failed: " + ex.Message;
                Logger.LogError(status + Environment.NewLine + ex);
            }
        }

        private void StartClient()
        {
            try
            {
                EnableBackgroundExecution();
                int port;
                if (!int.TryParse(portText, out port)) throw new ArgumentException("Port must be a number.");
                SceneLoadPatch.ResetSession();
                damageRequestSequences.Clear();
                playerVitalsRequestSequences.Clear();
                PlayerVitalsRequestAuthority.Reset();
                damageRequestRateLimiter.Clear();
            resyncRequestRateLimiter.Clear();
                nextDamageRequestSequence = 0;
                enemyProjectileSync.Reset();
                enemyExplosionSync.Reset();
                doorStateSync.Reset();
                weaponPickupSync.Reset();
                ResetSecretBonusSession();
                network.StartClient(address, port, playerName, password);
            }
            catch (Exception ex)
            {
                if (!network.IsRunning) RestoreBackgroundExecution();
                status = "Connect failed: " + ex.Message;
                Logger.LogError(status);
            }
        }

        private void DiscoverLan()
        {
            try
            {
                EnableBackgroundExecution();
                int port;
                if (!int.TryParse(portText, out port)) throw new ArgumentException("Port must be a number.");
                network.DiscoverLan(port);
            }
            catch (Exception ex)
            {
                if (!network.IsRunning) RestoreBackgroundExecution();
                status = "LAN search failed: " + ex.Message;
                Logger.LogError(status);
            }
        }

        private void JoinLobby(DiscoveredLobby lobby)
        {
            try
            {
                EnableBackgroundExecution();
                network.JoinDiscovered(lobby, playerName, password);
            }
            catch (Exception ex)
            {
                if (!network.IsRunning) RestoreBackgroundExecution();
                status = "Join failed: " + ex.Message;
                Logger.LogError(status);
            }
        }

        private void OnNetworkStatus(string message)
        {
            status = message;
            if (!network.IsHost && !network.IsConnected) SessionDifficultyOverride.Clear();
            if (!network.IsConnected)
            {
                remoteFriendlyFire = false;
                automaticWorldResyncStarted = false;
                automaticResyncsRemaining = 0;
                pendingLocalVitals = null;
                pendingSceneVitalsReset = false;
                enemyProjectileSync.Reset();
                enemyExplosionSync.Reset();
                doorStateSync.Reset();
                weaponPickupSync.Reset();
            }
            Logger.LogInfo(message);
        }

        private void OnPlayerJoined(PlayerSnapshot player)
        {
            if (network.IsHost) network.SendToPlayer(player.PlayerId, MessageType.SessionRules, new[] { (byte)(FriendlyFireEnabled ? 1 : 0) });
            Logger.LogInfo($"Player joined: #{player.PlayerId} {player.PlayerName}");
            if (network.IsHost && !SceneLoadPatch.SendPrepareToJoiningPlayer(player.PlayerId) && !string.IsNullOrEmpty(SceneHelper.CurrentScene))
                network.SendToPlayer(player.PlayerId, MessageType.JoinSessionState,
                    JoinSessionStateCodec.Encode(SceneHelper.CurrentScene, (byte)SessionDifficulty));
        }

        internal void SendInitialEnemySnapshot(uint playerId)
        {
            enemySync.SendFullSnapshotToPlayer(network, playerId);
            enemyProjectileSync.SendCurrentToPlayer(network, playerId);
            doorStateSync.SendCurrentToPlayer(network, playerId);
            worldObjectSync.SendCurrentToPlayer(network, playerId);
            sceneObjectStateSync.SendCurrentToPlayer(network, playerId);
            ItemSync.SendCurrent(network, playerId);
            LevelProgressionCoordinator.SendCurrentToPlayer(network, playerId);
            if (network.IsHost)
                foreach (var bonus in collectedSecretBonuses)
                    network.SendToPlayer(playerId, MessageType.BonusPickup, BonusPickupCodec.EncodeRelay(bonus));
        }

        internal void ApplyInitialEnemySnapshot()
        {
            enemySync.ApplyJoinSnapshot();
        }

        internal void SendCurrentCheckpointToPlayer(uint playerId, string sceneName)
        {
            string path;
            if (!network.IsHost || !CheckpointCoordinator.TryGetCurrentCheckpoint(sceneName, out path)) return;
            network.SendToPlayer(playerId, MessageType.CheckpointEvent,
                CheckpointEventCodec.Encode(CheckpointEventKind.Commit, path, CheckpointCoordinator.FindByStablePath(path)?.transform.position));
        }

        internal bool RequestSecretBonusPickup(Bonus bonus)
        {
            if (network.IsHost || !network.IsConnected || bonus == null || bonus.ghost || bonus.superCharge || bonus.secretNumber < 0) return false;
            string sceneName;
            string objectPath;
            byte componentIndex;
            if (!SceneObjectIdentity.TryBuild(bonus, out sceneName, out objectPath, out componentIndex)) return false;
            nextSecretBonusSequence++;
            if (nextSecretBonusSequence == 0) nextSecretBonusSequence = 1;
            var location = new SecretBonusLocation(sceneName, objectPath, componentIndex, bonus.secretNumber);
            return network.SendToHost(MessageType.BonusPickupRequest, BonusPickupCodec.EncodeRequest(nextSecretBonusSequence, location));
        }

        internal void NotifyHostSecretBonusPickedUp(SecretBonusLocation location)
        {
            if (!network.IsHost || !network.IsConnected || location == null) return;
            if (!SceneObjectIdentity.IsCurrentScene(location.SceneName)) return;
            RecordCollectedSecretBonus(location);
            network.BroadcastReliable(MessageType.BonusPickup, BonusPickupCodec.EncodeRelay(location));
        }

        private void HandleSecretBonusPickupRequest(uint senderId, byte[] payload)
        {
            if (!network.IsHost || !network.IsConnected) return;
            var request = BonusPickupCodec.DecodeRequest(payload);
            var location = request.Location;
            if (!SceneObjectIdentity.IsCurrentScene(location.SceneName)) return;
            if (!network.HasFreshPlayerSnapshot(senderId, 1f)) return;
            PlayerSnapshot player;
            if (!network.Players.TryGetValue(senderId, out player) || player == null || (player.MovementFlags & 4) != 0) return;
            if (!secretBonusRateLimiter.TryAccept(senderId, Time.unscaledTime)) return;
            if (!secretBonusSequences.TryAccept(senderId, request.Sequence)) return;
            var bonus = SceneObjectIdentity.Resolve<Bonus>(location.SceneName, location.ObjectPath, location.ComponentIndex);
            if (bonus == null || bonus.ghost || bonus.superCharge || bonus.secretNumber != location.SecretNumber) return;
            if (SecretBonusReplicationPatch.IsActivated(bonus)) return;
            if ((bonus.transform.position - player.Position).sqrMagnitude > 900f) return;
            var playerCollider = GetLocalPlayerCollider();
            if (playerCollider == null) return;
            if (!SecretBonusReplicationPatch.ApplyRemotePickup(bonus, playerCollider)) return;
            RecordCollectedSecretBonus(location);
            network.BroadcastReliable(MessageType.BonusPickup, BonusPickupCodec.EncodeRelay(location));
        }

        private void ReceiveSecretBonusPickup(byte[] payload)
        {
            var location = BonusPickupCodec.DecodeRelay(payload);
            if (!SceneObjectIdentity.IsCurrentScene(location.SceneName)) return;
            var key = SecretBonusKey(location);
            if (!TryApplySecretBonusPickup(location))
                pendingSecretBonusRelays[key] = new PendingSecretBonus { Location = location, ExpiresAt = Time.unscaledTime + 15f };
            else
                pendingSecretBonusRelays.Remove(key);
        }

        private bool TryApplySecretBonusPickup(SecretBonusLocation location)
        {
            if (localPlayer == null || localPlayer.dead) return false;
            var bonus = SceneObjectIdentity.Resolve<Bonus>(location.SceneName, location.ObjectPath, location.ComponentIndex);
            if (bonus == null) return false;
            if (bonus.ghost || bonus.superCharge || bonus.secretNumber != location.SecretNumber) return true;
            if (SecretBonusReplicationPatch.IsActivated(bonus)) return true;
            var playerCollider = GetLocalPlayerCollider();
            if (playerCollider == null) return false;
            return SecretBonusReplicationPatch.ApplyRemotePickup(bonus, playerCollider);
        }

        private void RetrySecretBonusPickups()
        {
            if (pendingSecretBonusRelays.Count == 0) return;
            var keys = new List<string>(pendingSecretBonusRelays.Keys);
            var now = Time.unscaledTime;
            foreach (var key in keys)
            {
                PendingSecretBonus pending;
                if (!pendingSecretBonusRelays.TryGetValue(key, out pending)) continue;
                if (pending.ExpiresAt <= now || !SceneObjectIdentity.IsCurrentScene(pending.Location.SceneName) || TryApplySecretBonusPickup(pending.Location))
                    pendingSecretBonusRelays.Remove(key);
            }
        }

        private static string SecretBonusKey(SecretBonusLocation location)
        {
            return location.SceneName + ":" + location.ObjectPath + ":" + location.ComponentIndex + ":" + location.SecretNumber;
        }

        private static Collider GetLocalPlayerCollider()
        {
            var plugin = Instance;
            var player = plugin != null && plugin.localPlayer != null ? plugin.localPlayer : FindObjectOfType<NewMovement>();
            if (player == null) return null;
            if (player.playerCollider != null) return player.playerCollider;
            return player.GetComponent<Collider>();
        }

        private void RecordCollectedSecretBonus(SecretBonusLocation location)
        {
            foreach (var existing in collectedSecretBonuses)
            {
                if (existing.ComponentIndex == location.ComponentIndex &&
                    string.Equals(existing.SceneName, location.SceneName, StringComparison.Ordinal) &&
                    string.Equals(existing.ObjectPath, location.ObjectPath, StringComparison.Ordinal) &&
                    existing.SecretNumber == location.SecretNumber) return;
            }
            collectedSecretBonuses.Add(location);
        }

        private void ResetSecretBonusSession()
        {
            ItemSync.Reset();
            worldObjectSync.Reset();
            sceneObjectStateSync.Reset();
            worldAudioSync.Reset();
            secretBonusSequences.Clear();
            secretBonusRateLimiter.Clear();
            collectedSecretBonuses.Clear();
            pendingSecretBonusRelays.Clear();
            nextSecretBonusSequence = 0;
        }

        private void OnPlayerLeft(uint id)
        {
            EnemyMeleeSync.ForgetPlayer(id);
            resyncRequestRateLimiter.Remove(id);
            sceneObjectStateSync.OnPlayerLeft(id);
            ItemSync.ForgetPlayer(network, id);
            pendingRemoteAnimations.Remove(id);
            SceneLoadPatch.OnPlayerLeft(id);
            LevelProgressionCoordinator.ForgetPlayer(id);
            activeTeamMarkers.RemoveAll(marker => marker.OwnerPlayerId == id);
            teamMarkerRateLimiter.Remove(id);
            teamMarkerSequences.Remove(id);
            fireRateLimiter.Remove(id);
            animationRateLimiter.Remove(id);
            coinProjectileRateLimiter.Remove(id);
            nailProjectileRateLimiter.Remove(id);
            rocketProjectileRateLimiter.Remove(id);
            worldAudioSync.RemovePlayer(id);
            damageRequestSequences.Remove(id);
            playerVitalsRequestSequences.Remove(id);
            PlayerVitalsRequestAuthority.ForgetPlayer(id);
            damageRequestRateLimiter.Remove(id);
            playerVitalsRateLimiter.Remove(id);
            secretBonusSequences.Remove(id);
            secretBonusRateLimiter.Remove(id);
            doorStateSync.RemovePlayer(id);
            enemyProjectileSync.RemovePlayer(id);
            weaponPickupSync.RemovePlayer(id);
            Logger.LogInfo($"Player left: #{id}");
        }

        private void OnNetworkMessage(uint senderId, MessageType type, byte[] payload)
        {
            try
            {
                if (type == MessageType.DamageRequest && network.IsHost)
                    DamageRequestAuthority.Apply(senderId, payload, network, enemySync, damageRequestSequences, damageRequestRateLimiter);
                else if (type == MessageType.EnemySnapshot && !network.IsHost && senderId == 1)
                    enemySync.ReceiveSnapshotBatch(payload);
                else if (type == MessageType.EnemyDamageState && !network.IsHost && senderId == 1)
                    enemySync.ReceiveDamageState(payload);
                else if (type == MessageType.EnemyDespawn && !network.IsHost && senderId == 1)
                    enemySync.ReceiveDespawn(payload);
                else if (type == MessageType.EnemyProjectileSnapshot && !network.IsHost && senderId == 1)
                    enemyProjectileSync.ReceiveBatch(payload);
                else if (type == MessageType.EnemyProjectileParryRequest && network.IsHost)
                {
                    byte[] parryResult;
                    if (enemyProjectileSync.TryAuthorizeParry(senderId, payload, network, out parryResult))
                        network.BroadcastReliable(MessageType.EnemyProjectileParryResult, parryResult);
                }
                else if (type == MessageType.EnemyProjectileParryResult && !network.IsHost && senderId == 1)
                    enemyProjectileSync.ReceiveParryResult(payload);
                else if (type == MessageType.EnemyExplosion && !network.IsHost && senderId == 1)
                    enemyExplosionSync.Receive(network, payload);
                else if (type == MessageType.DoorState && !network.IsHost && senderId == 1)
                    doorStateSync.ReceiveState(payload);
                else if (type == MessageType.DoorRequest && network.IsHost)
                    HandleDoorOpenRequest(senderId, payload);
                else if (type == MessageType.WorldMutationRequest && network.IsHost)
                    worldObjectSync.HandleRequest(senderId, payload, network);
                else if (type == MessageType.WorldMutation && !network.IsHost && senderId == 1)
                    worldObjectSync.Apply(payload, network);
                else if (type == MessageType.SceneObjectRequest && network.IsHost)
                    sceneObjectStateSync.HandleRequest(senderId, payload, network);
                else if (type == MessageType.SceneObjectState && !network.IsHost && senderId == 1)
                    sceneObjectStateSync.Receive(payload, network);
                else if (type == MessageType.ItemRequest && network.IsHost)
                    ItemSync.ReceiveRequest(senderId, payload, network);
                else if (type == MessageType.ItemState && !network.IsHost && senderId == 1)
                    ItemSync.Receive(payload, network);
                else if (type == MessageType.ResyncRequest && network.IsHost && payload.Length == 0 && network.Players.ContainsKey(senderId) && resyncRequestRateLimiter.TryAccept(senderId, Time.unscaledTime))
                    SendInitialEnemySnapshot(senderId);
                else if (type == MessageType.WorldAudio && network.IsHost)
                    worldAudioSync.TryRelay(senderId, payload, network, Time.unscaledTime);
                else if (type == MessageType.WorldAudio && !network.IsHost && senderId == 1)
                    worldAudioSync.PlayRemote(payload, network.LocalPlayerId);
                else if (type == MessageType.WeaponPickupRequest && network.IsHost)
                {
                    byte[] grant;
                    if (weaponPickupSync.TryAuthorizeRequest(senderId, payload, network, out grant))
                        network.SendToPlayer(senderId, MessageType.WeaponPickupGrant, grant);
                }
                else if (type == MessageType.WeaponPickupGrant && !network.IsHost && senderId == 1)
                    weaponPickupSync.ReceiveGrant(payload);
                else if (type == MessageType.LevelTransition && !network.IsHost)
                    SceneLoadPatch.ReceiveHostTransition(senderId, payload);
                else if (type == MessageType.LevelTransition && network.IsHost)
                    SceneLoadPatch.ReceiveClientReady(senderId, payload);
                else if (type == MessageType.JoinSessionState && !network.IsHost && senderId == 1)
                {
                    var joinState = JoinSessionStateCodec.DecodeState(payload);
                    SceneLoadPatch.ReceiveJoinSessionState(joinState.SceneName, joinState.Difficulty);
                }
                else if (type == MessageType.SessionRules && !network.IsHost && senderId == 1 && payload.Length == 1 && payload[0] <= 1)
                    remoteFriendlyFire = payload[0] == 1;
                else if (type == MessageType.EnemyPresence && !network.IsHost && senderId == 1 && network.SceneSnapshotSyncReady)
                    enemySync.ReceivePresence(payload);
                else if (type == MessageType.CheckpointEvent && network.IsHost)
                    HandleCheckpointRequest(senderId, payload);
                else if (type == MessageType.LevelProgression)
                    LevelProgressionCoordinator.Receive(senderId, payload);
                else if (type == MessageType.CheckpointEvent && !network.IsHost && senderId == 1)
                {
                    var checkpointEvent = CheckpointEventCodec.Decode(payload);
                    if (checkpointEvent.Kind == CheckpointEventKind.Commit)
                        CheckpointCoordinator.ApplyCommit(checkpointEvent);
                }
                else if (type == MessageType.WeaponFire && network.IsHost)
                    HandleWeaponFire(senderId, payload);
                else if (type == MessageType.WeaponFire && !network.IsHost && senderId == 1)
                {
                    var fire = WeaponFireCodec.DecodeRelay(payload);
                    if (fire.OwnerPlayerId != network.LocalPlayerId) PlayRemoteWeaponFire(fire.OwnerPlayerId, fire);
                }
                else if (type == MessageType.PlayerAnimation && network.IsHost)
                    HandlePlayerAnimation(senderId, payload);
                else if (type == MessageType.PlayerAnimation && !network.IsHost && senderId == 1)
                {
                    var animation = PlayerAnimationCodec.DecodeRelay(payload);
                    if (animation.OwnerPlayerId != network.LocalPlayerId) PlayRemotePlayerAnimation(animation.OwnerPlayerId, animation);
                }
                else if (type == MessageType.CoinProjectile && network.IsHost)
                    HandleCoinProjectile(senderId, payload);
                else if (type == MessageType.CoinProjectile && !network.IsHost && senderId == 1)
                {
                    var coin = CoinProjectileCodec.DecodeRelay(payload);
                    if (coin.OwnerPlayerId != network.LocalPlayerId) PlayRemoteCoinProjectile(coin);
                }
                else if (type == MessageType.NailProjectile && network.IsHost)
                    HandleNailProjectile(senderId, payload);
                else if (type == MessageType.NailProjectile && !network.IsHost && senderId == 1)
                {
                    var projectile = NailProjectileCodec.DecodeRelay(payload);
                    if (projectile.OwnerPlayerId != network.LocalPlayerId) PlayRemoteNailProjectile(projectile);
                }
                else if (type == MessageType.RocketProjectile && network.IsHost)
                    HandleRocketProjectile(senderId, payload);
                else if (type == MessageType.RocketProjectile && !network.IsHost && senderId == 1)
                {
                    var projectile = RocketProjectileCodec.DecodeRelay(payload);
                    if (projectile.OwnerPlayerId != network.LocalPlayerId) PlayRemoteRocketProjectile(projectile);
                }
                else if (type == MessageType.PlayerVitalsRequest && network.IsHost)
                    HandlePlayerVitalsRequest(senderId, payload);
                else if (type == MessageType.TeamMarker && network.IsHost)
                    HandleTeamMarkerRequest(senderId, payload);
                else if (type == MessageType.TeamMarker && !network.IsHost && senderId == 1)
                    ReceiveTeamMarker(payload);
                else if (type == MessageType.BonusPickupRequest && network.IsHost)
                    HandleSecretBonusPickupRequest(senderId, payload);
                else if (type == MessageType.BonusPickup && !network.IsHost && senderId == 1)
                    ReceiveSecretBonusPickup(payload);
            }
            catch (Exception ex)
            {
                int suppressed;
                suppressedRejectedMessageCounts.TryGetValue(type, out suppressed);
                suppressedRejectedMessageCounts[type] = suppressed + 1;
                float nextLogAt;
                if (!nextRejectedMessageLogAt.TryGetValue(type, out nextLogAt) || Time.unscaledTime >= nextLogAt)
                {
                    nextRejectedMessageLogAt[type] = Time.unscaledTime + 5f;
                    Logger.LogWarning("Rejected multiplayer message " + type + ": " + ex.Message +
                        (suppressed > 0 ? " (" + suppressed + " similar packets suppressed in the last 5s)" : string.Empty));
                    suppressedRejectedMessageCounts[type] = 0;
                }
            }
        }

        public void NotifyLocalWeaponFire(byte fireKind)
        {
            if (!network.IsConnected || localPlayer == null || localPlayer.dead) return;
            var aimRotation = localPlayer.cc != null && localPlayer.cc.cam != null
                ? localPlayer.cc.cam.transform.rotation
                : localPlayer.transform.rotation;
            var gun = MonoSingleton<GunControl>.Instance;
            if (gun == null || gun.currentWeapon == null) return;
            var slot = WeaponInventoryPolicy.ToNetworkSlot(gun.currentSlotIndex);
            var variation = (byte)Mathf.Clamp(gun.currentVariationIndex, 0, 7);
            if (!WeaponFireCodec.IsValidWeaponFire(slot, fireKind)) return;
            var identifier = gun.currentWeapon.GetComponent<WeaponIdentifier>();
            var alternate = identifier != null && identifier.alternateVersion;
            var payload = WeaponFireCodec.Encode(slot, variation, fireKind, aimRotation, alternate);
            if (network.IsHost)
            {
                ApplyFriendlyFire(network.LocalPlayerId, new WeaponFireEvent { WeaponSlot = slot, WeaponVariation = variation, FireKind = fireKind, AimRotation = aimRotation });
                network.BroadcastUnreliable(MessageType.WeaponFire, WeaponFireCodec.EncodeRelay(network.LocalPlayerId,
                    slot, variation, fireKind, aimRotation, alternate));
            }
            else
            {
                if (FriendlyFireEnabled) network.SendToHost(MessageType.WeaponFire, payload);
                else network.SendUnreliableToHost(MessageType.WeaponFire, payload);
            }
        }

        internal void NotifyLocalWorldAudio(AudioSource source, AudioClip clip, float volumeScale)
        {
            if (!network.IsConnected || WorldAudioSync.IsApplyingRemote || EnemySyncRegistry.IsReplayingAuthoritativeDamage || DamageRequestAuthority.IsCapturingNativeDamage || ItemSync.IsApplyingScenario || WorldObjectSync.IsApplyingRemote || WorldObjectSync.IsRunningNativeMutation) return;
            byte[] payload;
            if (!worldAudioSync.ShouldSendLocal(source, clip, volumeScale, out payload, network.LocalPlayerId)) return;
            if (network.IsHost) network.BroadcastUnreliable(MessageType.WorldAudio, payload);
            else network.SendUnreliableToHost(MessageType.WorldAudio, payload);
        }

        public void NotifyLocalPlayerAnimation(PlayerAnimationAction action, float speed)
        {
            if (!network.IsConnected || localPlayer == null || localPlayer.dead) return;
            var payload = PlayerAnimationCodec.Encode(action, speed);
            var reliable = PlayerAnimationCodec.RequiresReliableDelivery(action) || (FriendlyFireEnabled && action == PlayerAnimationAction.Punch);
            if (network.IsHost)
            {
                if (action == PlayerAnimationAction.Punch)
                    ApplyFriendlyFire(network.LocalPlayerId, new WeaponFireEvent { WeaponSlot = 0, AimRotation =
                        localPlayer.cc != null && localPlayer.cc.cam != null ? localPlayer.cc.cam.transform.rotation : localPlayer.transform.rotation });
                var relayPayload = PlayerAnimationCodec.EncodeRelay(network.LocalPlayerId, action, speed);
                if (reliable) network.BroadcastReliable(MessageType.PlayerAnimation, relayPayload);
                else network.BroadcastUnreliable(MessageType.PlayerAnimation, relayPayload);
            }
            else
            {
                if (reliable) network.SendToHost(MessageType.PlayerAnimation, payload);
                else network.SendUnreliableToHost(MessageType.PlayerAnimation, payload);
            }
        }

        public void NotifyLocalParry(NewMovement movement)
        {
            if (movement != null && movement == localPlayer)
                NotifyLocalPlayerAnimation(PlayerAnimationAction.Parry, 1f);
        }

        public void NotifyLocalCoinSpawn(Coin coin)
        {
            if (!network.IsConnected || coin == null || coin.sourceWeapon == null || localPlayer == null || localPlayer.dead) return;
            var gun = FindObjectOfType<GunControl>();
            if (gun == null || coin.sourceWeapon != gun.currentWeapon || coin.rb == null) return;
            var launch = new CoinProjectileEvent
            {
                WeaponVariation = network.CurrentWeaponVariation,
                Position = coin.transform.position,
                Rotation = coin.transform.rotation,
                Velocity = coin.rb.velocity,
                Gravity = coin.rb.GetGravityDirection() * Physics.gravity.magnitude,
                AngularVelocity = coin.rb.angularVelocity
            };
            var payload = CoinProjectileCodec.EncodeLaunchRequest(launch);
            if (network.IsHost)
                network.BroadcastReliable(MessageType.CoinProjectile,
                    CoinProjectileCodec.EncodeRelay(network.LocalPlayerId, launch));
            else
                network.SendToHost(MessageType.CoinProjectile, payload);
        }

        public void NotifyLocalNailSpawn(Nail nail)
        {
            if (!network.IsConnected || nail == null || nail.sourceWeapon == null || localPlayer == null || localPlayer.dead || nail.rb == null) return;
            var gun = FindObjectOfType<GunControl>();
            if (gun == null || nail.sourceWeapon != gun.currentWeapon || gun.currentSlotIndex != 3) return;
            if (nail.rb.velocity.sqrMagnitude < 1f || nail.rb.velocity.sqrMagnitude > 500f * 500f) return;
            var visualKind = (byte)(nail.chainsaw ? 3 : nail.sawblade ? 2 : nail.heated ? 1 : 0);
            var launch = new NailProjectileEvent
            {
                WeaponVariation = network.CurrentWeaponVariation,
                VisualKind = visualKind,
                Position = nail.transform.position,
                Rotation = nail.transform.rotation,
                Velocity = nail.rb.velocity,
                Gravity = nail.rb.useGravity ? Physics.gravity : Vector3.zero,
                AngularVelocity = nail.rb.angularVelocity
            };
            var payload = NailProjectileCodec.EncodeLaunchRequest(launch);
            if (network.IsHost)
                network.BroadcastUnreliable(MessageType.NailProjectile,
                    NailProjectileCodec.EncodeRelay(network.LocalPlayerId, launch));
            else
                network.SendUnreliableToHost(MessageType.NailProjectile, payload);
        }

        public void NotifyLocalRocketSpawn(Grenade rocket)
        {
            if (!network.IsConnected || rocket == null || !rocket.rocket || rocket.sourceWeapon == null ||
                localPlayer == null || localPlayer.dead || rocket.rb == null) return;
            var gun = FindObjectOfType<GunControl>();
            if (gun == null || rocket.sourceWeapon != gun.currentWeapon || gun.currentSlotIndex != 5) return;
            var velocity = rocket.rb.velocity;
            if (velocity.sqrMagnitude < 1f && rocket.rocketSpeed > 1f) velocity = rocket.transform.forward * rocket.rocketSpeed;
            if (velocity.sqrMagnitude < 1f || velocity.sqrMagnitude > 600f * 600f) return;
            var launch = new RocketProjectileEvent
            {
                WeaponVariation = network.CurrentWeaponVariation,
                VisualKind = 0,
                Position = rocket.transform.position,
                Rotation = rocket.transform.rotation,
                Velocity = velocity,
                Gravity = rocket.rb.useGravity ? Physics.gravity : Vector3.zero,
                AngularVelocity = rocket.rb.angularVelocity
            };
            SendRocketProjectile(launch);
        }

        public void NotifyLocalCannonballSpawn(Cannonball cannonball)
        {
            if (!network.IsConnected || cannonball == null || cannonball.sourceWeapon == null ||
                localPlayer == null || localPlayer.dead) return;
            var gun = FindObjectOfType<GunControl>();
            var body = cannonball.GetComponent<Rigidbody>();
            if (gun == null || body == null || cannonball.sourceWeapon != gun.currentWeapon || gun.currentSlotIndex != 5) return;
            if (body.velocity.sqrMagnitude < 1f || body.velocity.sqrMagnitude > 600f * 600f) return;
            var launch = new RocketProjectileEvent
            {
                WeaponVariation = network.CurrentWeaponVariation,
                VisualKind = 1,
                Position = cannonball.transform.position,
                Rotation = cannonball.transform.rotation,
                Velocity = body.velocity,
                Gravity = body.useGravity ? Physics.gravity : Vector3.zero,
                AngularVelocity = body.angularVelocity
            };
            SendRocketProjectile(launch);
        }

        public void NotifyLocalNapalmSpawn(GasolineProjectile napalm)
        {
            if (!network.IsConnected || napalm == null || localPlayer == null || localPlayer.dead) return;
            var gun = FindObjectOfType<GunControl>();
            var body = napalm.GetComponent<Rigidbody>();
            if (gun == null || body == null || gun.currentSlotIndex != 5 ||
                !DamageRequestPolicy.IsSelectedWeapon("RocketLauncher", gun.currentWeapon != null ? gun.currentWeapon.name : string.Empty)) return;
            if (body.velocity.sqrMagnitude < 1f || body.velocity.sqrMagnitude > 600f * 600f) return;
            var launch = new RocketProjectileEvent
            {
                WeaponVariation = network.CurrentWeaponVariation,
                VisualKind = 2,
                Position = napalm.transform.position,
                Rotation = napalm.transform.rotation,
                Velocity = body.velocity,
                Gravity = body.useGravity ? Physics.gravity : Vector3.zero,
                AngularVelocity = body.angularVelocity
            };
            SendRocketProjectile(launch);
        }

        private void SendRocketProjectile(RocketProjectileEvent launch)
        {
            var payload = RocketProjectileCodec.EncodeLaunchRequest(launch);
            if (network.IsHost)
                network.BroadcastUnreliable(MessageType.RocketProjectile,
                    RocketProjectileCodec.EncodeRelay(network.LocalPlayerId, launch));
            else
                network.SendUnreliableToHost(MessageType.RocketProjectile, payload);
        }

        private void HandleCoinProjectile(uint senderId, byte[] payload)
        {
            if (senderId == 0 || !network.HasFreshPlayerSnapshot(senderId, 0.5f)) return;
            PlayerSnapshot player;
            if (!network.Players.TryGetValue(senderId, out player)) return;
            var launch = CoinProjectileCodec.DecodeLaunchRequest(payload);
            if (!CoinProjectilePolicy.IsAuthorizedLaunch(player, launch) ||
                !coinProjectileRateLimiter.TryAccept(senderId, Time.unscaledTime)) return;
            network.BroadcastReliable(MessageType.CoinProjectile,
                CoinProjectileCodec.EncodeRelay(senderId, launch));
        }

        private void HandleNailProjectile(uint senderId, byte[] payload)
        {
            if (senderId == 0 || !network.HasFreshPlayerSnapshot(senderId, 0.5f)) return;
            PlayerSnapshot player;
            if (!network.Players.TryGetValue(senderId, out player)) return;
            var launch = NailProjectileCodec.DecodeLaunchRequest(payload);
            if (!NailProjectilePolicy.IsAuthorizedLaunch(player, launch) ||
                !nailProjectileRateLimiter.TryAccept(senderId, Time.unscaledTime)) return;
            network.BroadcastUnreliable(MessageType.NailProjectile,
                NailProjectileCodec.EncodeRelay(senderId, launch));
        }

        private void HandleRocketProjectile(uint senderId, byte[] payload)
        {
            if (senderId == 0 || !network.HasFreshPlayerSnapshot(senderId, 0.5f)) return;
            PlayerSnapshot player;
            if (!network.Players.TryGetValue(senderId, out player)) return;
            var launch = RocketProjectileCodec.DecodeLaunchRequest(payload);
            if (!RocketProjectilePolicy.IsAuthorizedLaunch(player, launch) ||
                !rocketProjectileRateLimiter.TryAccept(senderId, Time.unscaledTime)) return;
            network.BroadcastUnreliable(MessageType.RocketProjectile,
                RocketProjectileCodec.EncodeRelay(senderId, launch));
        }

        private void PlayRemoteCoinProjectile(CoinProjectileEvent coin)
        {
            RemotePlayerVisual visual;
            if (coin != null && remoteViews.TryGetValue(coin.OwnerPlayerId, out visual) && visual != null)
                visual.PlayCoinProjectile(coin);
        }

        private void PlayRemoteNailProjectile(NailProjectileEvent projectile)
        {
            RemotePlayerVisual visual;
            if (projectile != null && remoteViews.TryGetValue(projectile.OwnerPlayerId, out visual) && visual != null)
                visual.PlayNailProjectile(projectile);
        }

        private void PlayRemoteRocketProjectile(RocketProjectileEvent projectile)
        {
            RemotePlayerVisual visual;
            if (projectile != null && remoteViews.TryGetValue(projectile.OwnerPlayerId, out visual) && visual != null)
                visual.PlayRocketProjectile(projectile);
        }

        internal DoorStateSync DoorStateSync { get { return doorStateSync; } }

        internal WorldMutation CaptureBreakableMutation(Breakable target, float strength)
        {
            return worldObjectSync.CaptureBreakable(target, strength);
        }

        internal WorldMutation CaptureGlassMutation(Glass target)
        {
            return worldObjectSync.CaptureGlass(target);
        }

        internal void CommitCapturedWorldMutation(WorldMutation mutation)
        {
            worldObjectSync.CommitHostMutation(mutation, network);
        }

        internal SceneObjectState CaptureSceneObjectState(GameObject target, SceneObjectOperation operation, bool active, float delay = 0f)
        {
            return sceneObjectStateSync.Capture(target, operation, active, delay);
        }

        internal SceneObjectState CaptureSceneObjectComponent(Component target, float delay = 0f)
        {
            return sceneObjectStateSync.CaptureComponent(target, delay);
        }

        internal void CommitSceneObjectState(SceneObjectState state)
        {
            sceneObjectStateSync.CommitLocal(state, network);
        }

        internal bool ShouldActivateWeaponPickup(WeaponPickUp pickup)
        {
            return weaponPickupSync.ShouldActivate(network, pickup);
        }

        internal bool ShouldActivateAlternatePickup(AltPickUp pickup)
        {
            return weaponPickupSync.ShouldActivate(network, pickup);
        }

        internal void RequestDoorOpen(Door door, bool enemy, bool skull)
        {
            doorStateSync.RequestOpen(network, door, enemy, skull);
        }

        internal void BroadcastDoorMutation(Door door, DoorMutation mutation, bool enemy = false, bool skull = false, bool force = false)
        {
            doorStateSync.BroadcastMutation(network, door, mutation, enemy, skull, force);
        }

        private void HandleDoorOpenRequest(uint senderId, byte[] payload)
        {
            Door door;
            bool enemy;
            bool skull;
            if (doorStateSync.TryGetOpenRequest(senderId, payload, network, out door, out enemy, out skull))
                door.Open(enemy, skull);
        }

        public void RequestEnemyProjectileParry(Projectile projectile)
        {
            if (!network.IsConnected || network.IsHost) return;
            enemyProjectileSync.RequestParry(projectile, network);
        }

        public void NotifyEnemyExplosion(Explosion explosion)
        {
            enemyExplosionSync.Broadcast(network, explosion);
        }

        public void ScaleEnemyDamageForCoop(ref float multiplier)
        {
            if (!network.IsHost || !network.IsConnected || CooperativeMode <= 0) return;
            var playerCount = Mathf.Clamp(network.Players.Count, 1, 4);
            var perExtraPlayer = CooperativeMode == 1 ? 0.35f : 0.65f;
            var enemyDurability = 1f + perExtraPlayer * (playerCount - 1);
            multiplier /= enemyDurability;
        }

        public bool InterceptLocalPlayerDamage(NewMovement movement, int damage, bool invincible, bool ignoreInvincibility)
        {
            if (movement != localPlayer || !network.IsConnected || network.IsHost || applyingAuthoritativeVitals) return false;
            if (movement.dead || damage <= 0 || (invincible && movement.gameObject.layer == 15 && !ignoreInvincibility)) return false;
            return SendLocalVitalsRequest(PlayerVitalsAction.Damage, Math.Min(200, damage));
        }

        public bool InterceptLocalPlayerHeal(NewMovement movement, int health)
        {
            if (movement != localPlayer || !network.IsConnected || network.IsHost || applyingAuthoritativeVitals) return false;
            if (movement.dead || health <= 0) return false;
            return SendLocalVitalsRequest(PlayerVitalsAction.Heal, Math.Min(1000, health));
        }

        public bool InterceptLocalPlayerSuperCharge(NewMovement movement)
        {
            if (movement != localPlayer || !network.IsConnected || network.IsHost || applyingAuthoritativeVitals) return false;
            if (movement.dead) return false;
            return SendLocalVitalsRequest(PlayerVitalsAction.Heal, 200);
        }

        private bool SendLocalVitalsRequest(PlayerVitalsAction action, int amount)
        {
            unchecked { nextPlayerVitalsRequestSequence++; }
            if (nextPlayerVitalsRequestSequence == 0) nextPlayerVitalsRequestSequence = 1;
            byte[] payload;
            try { payload = PlayerVitalsRequestCodec.Encode(nextPlayerVitalsRequestSequence, network.LocalSnapshotTick, action, amount); }
            catch (ArgumentOutOfRangeException) { return false; }
            return network.SendToHost(MessageType.PlayerVitalsRequest, payload);
        }

        private void HandlePlayerVitalsRequest(uint senderId, byte[] payload)
        {
            if (senderId == 0 || !network.HasFreshPlayerSnapshot(senderId, 0.5f)) return;
            PlayerSnapshot player;
            if (!network.Players.TryGetValue(senderId, out player) || player == null) return;
            var request = PlayerVitalsRequestCodec.Decode(payload);
            PlayerSnapshot requestSnapshot;
            if (!network.TryGetPlayerSnapshotAtTick(senderId, request.SnapshotTick, 15, out requestSnapshot) || requestSnapshot == null) return;
            var playerIsDead = (player.MovementFlags & 4) != 0;
            var snapshotIsDead = (requestSnapshot.MovementFlags & 4) != 0;
            if (request.Action == PlayerVitalsAction.Respawn)
            {
                if (!playerIsDead || !snapshotIsDead ||
                    !playerVitalsRateLimiter.TryAccept(senderId, Time.unscaledTime) ||
                    !playerVitalsRequestSequences.TryAccept(senderId, request.Sequence)) return;
                network.SetAuthoritativePlayerVitals(senderId, network.MaximumPlayerHealth, false);
                Logger.LogInfo("Authorized spectator respawn for player " + senderId + ".");
                return;
            }
            if (playerIsDead || snapshotIsDead ||
                (request.Action == PlayerVitalsAction.Heal && requestSnapshot.Health >= network.MaximumPlayerHealth)) return;
            if (!playerVitalsRateLimiter.TryAccept(senderId, Time.unscaledTime) ||
                !playerVitalsRequestSequences.TryAccept(senderId, request.Sequence)) return;
            var authorizedAmount = PlayerVitalsRequestAuthority.GetAuthorizedAmount(network, requestSnapshot, request);
            if (authorizedAmount <= 0) return;
            if (request.Action == PlayerVitalsAction.Heal)
                PlayerVitalsRequestAuthority.RecordAcceptedHeal(senderId, requestSnapshot.Position);

            var updatedHealth = request.Action == PlayerVitalsAction.Damage
                ? Math.Max(0, player.Health - authorizedAmount)
                : Math.Min(network.MaximumPlayerHealth, player.Health + authorizedAmount);
            network.SetAuthoritativePlayerVitals(senderId, (ushort)updatedHealth, updatedHealth == 0);
        }

        private void RequestSpectatorRespawn()
        {
            if (localPlayer == null || !localPlayer.dead || !network.IsConnected) return;
            if (!network.IsHost)
            {
                if (SendLocalVitalsRequest(PlayerVitalsAction.Respawn, 1))
                    status = "Respawn requested from host.";
                return;
            }

            localPlayer.Respawn();
            localPlayer.hp = network.MaximumPlayerHealth;
            if (localPlayer.rb != null) localPlayer.rb.velocity = Vector3.zero;
            network.SetAuthoritativePlayerVitals(network.LocalPlayerId, network.MaximumPlayerHealth, false);
            Logger.LogInfo("Host respawned from spectator mode.");
        }

        internal bool InterceptNativeCheckpointRespawn()
        {
            if (!network.IsConnected || localPlayer == null) return false;
            RequestSpectatorRespawn();
            return true;
        }

        internal bool InterceptDeathRestart()
        {
            if (!network.IsConnected || localPlayer == null || !localPlayer.dead) return false;
            RequestSpectatorRespawn();
            return true;
        }

        internal bool PrepareCoopRespawn(NewMovement movement)
        {
            if (!network.IsConnected || movement != localPlayer) return true;
            if (!network.IsHost && !applyingAuthoritativeVitals && movement.dead)
            {
                SendLocalVitalsRequest(PlayerVitalsAction.Respawn, 1);
                return false;
            }
            spectator.PrepareForRespawn();
            return true;
        }

        internal void FinishCoopRespawn(NewMovement movement)
        {
            if (!network.IsConnected || movement != localPlayer || movement.dead) return;
            var checkpoint = CheckpointCoordinator.GetCurrentCheckpoint(SceneHelper.CurrentScene);
            var destination = hasLastSafePlayerPosition ? lastSafePlayerPosition : movement.transform.position;
            var yaw = hasLastSafePlayerPosition ? lastSafePlayerRotation.eulerAngles.y : movement.transform.eulerAngles.y;
            if (checkpoint != null)
            {
                destination = checkpoint.transform.position + checkpoint.transform.up * 1.25f;
                yaw = checkpoint.transform.eulerAngles.y + checkpoint.additionalSpawnRotation;
                if (checkpoint.toActivate != null) checkpoint.toActivate.SetActive(true);
            }
            Vector3 encounterSpawn;
            if (EncounterRecovery.TryGetSpawn(this, out encounterSpawn)) destination = encounterSpawn;
            movement.enabled = true;
            movement.activated = true;
            if (movement.transform.parent != null && movement.GetComponentInParent<GoreZone>() != null)
                movement.transform.SetParent(null, true);
            movement.rb.isKinematic = false;
            movement.rb.constraints = RigidbodyConstraints.FreezeRotation;
            movement.rb.angularVelocity = Vector3.zero;
            movement.rb.velocity = Vector3.zero;
            movement.rb.SetCustomGravityMode(false);
            var gravity = checkpoint != null && checkpoint.gravity.sqrMagnitude > 0.001f ? checkpoint.gravity : Physics.gravity;
            if (checkpoint != null && gravity.sqrMagnitude > 0.001f)
            {
                movement.rb.SetCustomGravity(gravity);
                movement.rb.SetCustomGravityMode(true);
            }
            movement.cc.enabled = true;
            movement.cc.activated = true;
            movement.cc.gravityRotation = gravity.sqrMagnitude > 0.001f
                ? Quaternion.FromToRotation(Vector3.down, gravity.normalized) : Quaternion.identity;
            movement.cc.gravityVec = gravity.sqrMagnitude > 0.001f ? gravity.normalized : Vector3.down;
            movement.cc.rotationOffset = Quaternion.identity;
            movement.cc.transitionRotationZ = movement.cc.transitionRotationZSmooth = 0f;
            movement.cc.tiltRotationZ = movement.cc.tiltRotationZSmooth = 0f;
            movement.cc.ResetCamera(yaw);
            movement.cc.ApplyRotations();
            movement.cc.StopShake();
            movement.cc.ResetToDefaultPos();
            if (movement.cc.cam != null) movement.cc.cam.useOcclusionCulling = true;
            RecoverLocalPlayer(destination, movement.cc.gravityRotation * Quaternion.Euler(0f, yaw, 0f), "co-op respawn");
            teamWipeTimer = 0f;
            restartRequested = false;
            if (network.IsHost) OnLocalPlayerVitalsChanged(movement);
        }

        private ushort GetLocalPlayerHealthCap()
        {
            if (localPlayer == null) return 100;
            var difficultyField = HarmonyLib.AccessTools.Field(typeof(NewMovement), "difficulty");
            var restartField = HarmonyLib.AccessTools.Field(typeof(NewMovement), "sameCheckpointRestarts");
            if (difficultyField == null) return (ushort)(localPlayer.hp > 100 ? 200 : 100);
            var difficulty = (int)difficultyField.GetValue(localPlayer);
            var restarts = restartField != null ? (int)restartField.GetValue(localPlayer) : 0;
            return (ushort)(difficulty == 0 || (difficulty == 1 && restarts > 2) ? 200 : 100);
        }

        public void OnLocalPlayerVitalsChanged(NewMovement movement)
        {
            if (movement != localPlayer || !network.IsHost || applyingAuthoritativeVitals) return;
            var health = (ushort)Mathf.Clamp(movement.hp, 0, 200);
            network.SetAuthoritativePlayerVitals(network.LocalPlayerId, health, movement.dead || health == 0);
        }

        private void OnPlayerLifecycleChanged(PlayerLifecycleUpdate update)
        {
            if (network.IsHost || update == null || update.PlayerId != network.LocalPlayerId) return;
            if (localPlayer == null)
            {
                pendingLocalVitals = update;
                return;
            }
            ApplyAuthoritativePlayerVitals(update);
        }

        private void ApplyAuthoritativePlayerVitals(PlayerLifecycleUpdate update)
        {
            if (update == null || localPlayer == null) return;
            if (localPlayer.hp == update.Health && localPlayer.dead == update.IsDead) return;
            applyingAuthoritativeVitals = true;
            try
            {
                var currentHealth = Mathf.Clamp(localPlayer.hp, 0, 200);
                if (update.IsDead && !localPlayer.dead)
                    localPlayer.GetHurt(Math.Max(1, currentHealth), false, 0f, false, true, 0f, true);
                else if (!update.IsDead && localPlayer.dead)
                {
                    localPlayer.Respawn();
                    if (localPlayer.rb != null) localPlayer.rb.velocity = Vector3.zero;
                }
                else if (update.Health < currentHealth)
                    localPlayer.GetHurt(currentHealth - update.Health, true, 0f, false, false, 0f, true);
                else if (update.Health > currentHealth)
                    localPlayer.GetHealth(update.Health - currentHealth, true, false, false);
                localPlayer.hp = update.Health;
            }
            finally
            {
                applyingAuthoritativeVitals = false;
            }
        }

        private void HandlePlayerAnimation(uint senderId, byte[] payload)
        {
            if (!network.HasFreshPlayerSnapshot(senderId, 0.5f)) return;
            PlayerSnapshot player;
            if (!network.Players.TryGetValue(senderId, out player) || (player.MovementFlags & 4) != 0) return;
            if (!animationRateLimiter.TryAccept(senderId, Time.unscaledTime)) return;
            var animation = PlayerAnimationCodec.Decode(payload);
            if (animation.Action == PlayerAnimationAction.Punch)
                ApplyFriendlyFire(senderId, new WeaponFireEvent { WeaponSlot = 0, AimRotation = player.ViewRotation });
            PlayRemotePlayerAnimation(senderId, animation);
            var relayPayload = PlayerAnimationCodec.EncodeRelay(senderId, animation);
            if (PlayerAnimationCodec.RequiresReliableDelivery(animation.Action))
                network.BroadcastReliable(MessageType.PlayerAnimation, relayPayload);
            else
                network.BroadcastUnreliable(MessageType.PlayerAnimation, relayPayload);
        }

        private void PlayRemotePlayerAnimation(uint playerId, PlayerAnimationEvent animation)
        {
            RemotePlayerVisual visual;
            if (remoteViews.TryGetValue(playerId, out visual) && visual != null)
            {
                visual.PlayPlayerAnimation(animation);
                return;
            }
            Queue<PlayerAnimationEvent> queued;
            if (!pendingRemoteAnimations.TryGetValue(playerId, out queued))
            {
                queued = new Queue<PlayerAnimationEvent>(4);
                pendingRemoteAnimations[playerId] = queued;
            }
            while (queued.Count >= 8) queued.Dequeue();
            queued.Enqueue(animation);
        }

        private void HandleWeaponFire(uint senderId, byte[] payload)
        {
            if (!network.HasFreshPlayerSnapshot(senderId, 0.5f)) return;
            PlayerSnapshot player;
            if (!network.Players.TryGetValue(senderId, out player) || (player.MovementFlags & 4) != 0) return;
            var fire = WeaponFireCodec.Decode(payload);
            if (!WeaponInventoryPolicy.IsUnlocked(player.WeaponInventoryMask, fire.WeaponSlot, fire.WeaponVariation)) return;
            if (!WeaponFireCodec.IsPlausibleAimRotation(fire.AimRotation, player.ViewRotation)) return;

            if (!fireRateLimiter.TryAccept(senderId, Time.unscaledTime)) return;

            PlayRemoteWeaponFire(senderId, fire);
            ApplyFriendlyFire(senderId, fire);
            network.BroadcastUnreliable(MessageType.WeaponFire,
                WeaponFireCodec.EncodeRelay(senderId, fire));
        }

        private void PlayRemoteWeaponFire(uint playerId, WeaponFireEvent fire)
        {
            RemotePlayerVisual visual;
            if (remoteViews.TryGetValue(playerId, out visual) && visual != null)
                visual.PlayWeaponFire(fire.WeaponSlot, fire.WeaponVariation, fire.FireKind, fire.AimRotation, fire.WeaponAlternate);
        }

        private void ApplyFriendlyFire(uint shooterId, WeaponFireEvent fire)
        {
            if (!network.IsHost || !FriendlyFireEnabled || !network.SceneSnapshotSyncReady) return;
            PlayerSnapshot shooter;
            if (!network.Players.TryGetValue(shooterId, out shooter) || (shooter.MovementFlags & 4) != 0) return;
            var origin = shooterId == network.LocalPlayerId && localPlayer != null && localPlayer.cc != null && localPlayer.cc.cam != null
                ? localPlayer.cc.cam.transform.position : shooter.Position + Vector3.up * 0.65f;
            var direction = fire.AimRotation * Vector3.forward;
            PlayerSnapshot victim = null; var closest = FriendlyFirePolicy.Range(fire.WeaponSlot);
            foreach (var candidate in network.Players.Values)
            {
                if (candidate.PlayerId == shooterId || candidate.Health == 0 || (candidate.MovementFlags & 4) != 0 ||
                    (candidate.PlayerId != network.LocalPlayerId && !network.HasFreshPlayerSnapshot(candidate.PlayerId, 1f))) continue;
                float distance;
                if (FriendlyFirePolicy.Intersects(origin, direction, candidate.Position, closest, fire.WeaponSlot == 2 ? 1.1f : 0.55f, out distance))
                { victim = candidate; closest = distance; }
            }
            if (victim == null) return;
            foreach (var obstacle in Physics.RaycastAll(origin, direction, closest, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                if (obstacle.collider.GetComponentInParent<NewMovement>() != null || obstacle.collider.GetComponentInParent<RemotePlayerVisual>() != null) continue;
                return;
            }
            var damage = FriendlyFirePolicy.Damage(fire.WeaponSlot, fire.FireKind);
            if (victim.PlayerId == network.LocalPlayerId && localPlayer != null)
                localPlayer.GetHurt(damage, false, 0f, false, false, 0f, true);
            else
            {
                var health = (ushort)Mathf.Max(0, victim.Health - damage);
                network.SetAuthoritativePlayerVitals(victim.PlayerId, health, health == 0);
            }
        }

        internal EnemyDamageCapture CaptureHostEnemyDamage(EnemyIdentifier enemy, GameObject target, Vector3 force, Vector3 hitPoint,
            float multiplier, bool explode, float critical, GameObject source, bool ignoreScale, bool fromExplosion)
        {
            PlayerSnapshot shooter;
            network.Players.TryGetValue(network.LocalPlayerId, out shooter);
            try { return DamageRequestAuthority.Capture(enemy, enemySync, shooter, target, force, hitPoint, multiplier, explode, critical, source, ignoreScale, fromExplosion); }
            catch (Exception error) { Logger.LogWarning("Native host hit capture failed: " + error.Message); return null; }
        }

        internal void PublishHostEnemyDamage(EnemyDamageCapture capture)
        {
            try { DamageRequestAuthority.Publish(capture, network, enemySync); }
            catch (Exception error) { Logger.LogWarning("Native hit replication failed: " + error.Message); }
        }

        public bool TryRelayEnemyDamage(EnemyIdentifier enemy, GameObject target, Vector3 force, Vector3 hitPoint,
            float multiplier, bool tryForExplode, float critMultiplier, GameObject sourceWeapon,
            bool ignoreTotalDamageTakenMultiplier, bool fromExplosion)
        {
            if (enemy == null || enemy.dead || !enemy.gameObject.activeInHierarchy || !network.IsConnected ||
                enemy.gameObject.scene != SceneManager.GetActiveScene()) return false;
            uint id;
            enemySync.TryGetNetworkId(enemy, out id);
            unchecked { if (++nextDamageRequestSequence == 0) nextDamageRequestSequence = 1; }
            PlayerSnapshot shooter;
            if (!network.Players.TryGetValue(network.LocalPlayerId, out shooter) || shooter == null)
                shooter = new PlayerSnapshot { PlayerId = network.LocalPlayerId, Position = localPlayer != null ? localPlayer.transform.position : Vector3.zero,
                    ViewRotation = localPlayer != null && localPlayer.cc != null && localPlayer.cc.cam != null ? localPlayer.cc.cam.transform.rotation : Quaternion.identity,
                    WeaponName = localGun != null && localGun.currentWeapon != null ? localGun.currentWeapon.name : "Unarmed" };
            var origin = localPlayer != null && localPlayer.cc != null && localPlayer.cc.cam != null ? localPlayer.cc.cam.transform.position : shooter.Position;
            var payload = DamageRequestCodec.Encode(nextDamageRequestSequence, id, enemy, target, shooter, origin, force, hitPoint,
                multiplier, tryForExplode, critMultiplier, sourceWeapon, ignoreTotalDamageTakenMultiplier, fromExplosion);
            if (!network.SendToHost(MessageType.DamageRequest, payload) && pendingDamage.Count < 512) pendingDamage.Enqueue(payload);
            clientHitFeedbackUntil = Time.unscaledTime + 0.09f;
            return true;
        }

        private void FlushPendingDamage()
        {
            if (!network.IsConnected) { pendingDamage.Clear(); return; }
            var count = pendingDamage.Count;
            for (var i = 0; i < count; i++)
            {
                var payload = pendingDamage.Dequeue();
                if (!network.SendToHost(MessageType.DamageRequest, payload)) { pendingDamage.Enqueue(payload); break; }
            }
        }

        private void OnDestroy()
        {
            var gameStateManager = GameStateManager.Instance;
            if (gameStateManager != null && gameStateManager.IsStateActive(MultiplayerPanelStateKey))
                gameStateManager.PopState(MultiplayerPanelStateKey);
            if (gameStateManager != null && gameStateManager.IsStateActive(LevelOverlayStateKey))
                gameStateManager.PopState(LevelOverlayStateKey);
            new HarmonyLib.Harmony(PluginGuid).UnpatchSelf();
            SessionDifficultyOverride.Clear();
            enemyProjectileSync.Reset();
            doorStateSync.Reset();
            weaponPickupSync.Reset();
            if (appearancePreview != null) appearancePreview.Dispose();
            appearancePreview = null;
            if (Instance == this) Instance = null;
            network.StatusChanged -= OnNetworkStatus;
            network.PlayerJoined -= OnPlayerJoined;
            network.PlayerLeft -= OnPlayerLeft;
            network.PlayerLifecycleChanged -= OnPlayerLifecycleChanged;
            network.MessageReceived -= OnNetworkMessage;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            network.Dispose();
            RestoreBackgroundExecution();
            fireRateLimiter.Clear();
            coinProjectileRateLimiter.Clear();
            nailProjectileRateLimiter.Clear();
            rocketProjectileRateLimiter.Clear();
            damageRequestSequences.Clear();
            playerVitalsRequestSequences.Clear();
            PlayerVitalsRequestAuthority.Reset();
            damageRequestRateLimiter.Clear();
            resyncRequestRateLimiter.Clear();
            playerVitalsRateLimiter.Clear();
            ResetSecretBonusSession();
            ClearRemotePlayers();
            if (panelTexture != null) Destroy(panelTexture);
            if (accentTexture != null) Destroy(accentTexture);
            if (accentHoverTexture != null) Destroy(accentHoverTexture);
            if (fieldTexture != null) Destroy(fieldTexture);
            if (scrimTexture != null) Destroy(scrimTexture);
        }

        [HarmonyLib.HarmonyPatch(typeof(OptionsManager), nameof(OptionsManager.Pause))]
        private static class PauseMenuInputPatch
        {
            private static bool Prefix()
            {
                var plugin = Instance;
                return plugin == null || !plugin.IsMultiplayerPanelOpen;
            }
        }

        [HarmonyLib.HarmonyPatch(typeof(OptionsManager), nameof(OptionsManager.UnPause))]
        private static class UnpauseMenuInputPatch
        {
            private static bool Prefix()
            {
                var plugin = Instance;
                return plugin == null || !plugin.IsMultiplayerPanelOpen;
            }
        }

        [HarmonyLib.HarmonyPatch(typeof(GameStateManager), "EvaluateState")]
        private static class EnforceMultiplayerPanelInputStatePatch
        {
            private static bool playerActionsCaptured;
            private static bool playerActionsWereEnabled;
            private static readonly System.Reflection.MethodInfo SetPlayerInputLocked =
                HarmonyLib.AccessTools.Property(typeof(GameStateManager), nameof(GameStateManager.PlayerInputLocked)).GetSetMethod(true);
            private static readonly System.Reflection.MethodInfo SetCameraLocked =
                HarmonyLib.AccessTools.Property(typeof(GameStateManager), nameof(GameStateManager.CameraLocked)).GetSetMethod(true);
            private static readonly System.Reflection.MethodInfo SetCursorLocked =
                HarmonyLib.AccessTools.Property(typeof(GameStateManager), nameof(GameStateManager.CursorLocked)).GetSetMethod(true);

            private static void Postfix(GameStateManager __instance)
            {
                var plugin = Instance;
                if (plugin == null || (!plugin.showPanel && !plugin.loadingOverlayInputActive) || __instance == null) return;
                Apply(__instance);
            }

            internal static void Apply(GameStateManager manager)
            {
                if (manager != null)
                {
                    SetPlayerInputLocked?.Invoke(manager, new object[] { true });
                    SetCameraLocked?.Invoke(manager, new object[] { true });
                    SetCursorLocked?.Invoke(manager, new object[] { false });
                }
                InputManager inputManager = null;
                if (MonoSingleton<InputManager>.TryGetInstance(out inputManager) && inputManager.InputSource != null)
                {
                    if (!playerActionsCaptured)
                    {
                        playerActionsWereEnabled = ArePlayerActionsEnabled(inputManager.InputSource.Actions);
                        playerActionsCaptured = true;
                    }
                    if (ArePlayerActionsEnabled(inputManager.InputSource.Actions)) inputManager.InputSource.Disable();
                }
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }

            internal static void Restore(GameStateManager manager, bool playerLocked, bool cameraLocked, bool cursorLocked)
            {
                var plugin = Instance;
                if (plugin != null && (plugin.showPanel || plugin.loadingOverlayInputActive))
                {
                    Apply(manager);
                    return;
                }
                if (manager != null)
                {
                    SetPlayerInputLocked?.Invoke(manager, new object[] { playerLocked });
                    SetCameraLocked?.Invoke(manager, new object[] { cameraLocked });
                    SetCursorLocked?.Invoke(manager, new object[] { cursorLocked });
                }
                InputManager inputManager = null;
                if (playerActionsCaptured && playerActionsWereEnabled &&
                    MonoSingleton<InputManager>.TryGetInstance(out inputManager) && inputManager.InputSource != null &&
                    !ArePlayerActionsEnabled(inputManager.InputSource.Actions))
                    inputManager.InputSource.Enable();
                playerActionsCaptured = false;
            }

            private static bool ArePlayerActionsEnabled(InputActions actions)
            {
                return actions.UI.enabled || actions.Movement.enabled || actions.Fist.enabled ||
                    actions.Weapon.enabled || actions.HUD.enabled;
            }
        }

        [HarmonyLib.HarmonyPatch(typeof(GameStateManager), "get_PlayerInputLocked")]
        private static class MultiplayerPanelPlayerInputGetterPatch
        {
            private static void Postfix(ref bool __result)
            {
                var plugin = Instance;
                if (plugin != null && plugin.IsUiInputLocked) __result = true;
            }
        }

        [HarmonyLib.HarmonyPatch(typeof(GameStateManager), "get_CameraLocked")]
        private static class MultiplayerPanelCameraInputGetterPatch
        {
            private static void Postfix(ref bool __result)
            {
                var plugin = Instance;
                if (plugin != null && plugin.IsUiInputLocked) __result = true;
            }
        }

        [HarmonyLib.HarmonyPatch(typeof(GameStateManager), "get_CursorLocked")]
        private static class MultiplayerPanelCursorGetterPatch
        {
            private static void Postfix(ref bool __result)
            {
                var plugin = Instance;
                if (plugin != null && plugin.IsUiInputLocked) __result = false;
            }
        }

        [HarmonyLib.HarmonyPatch(typeof(EventSystem), "Update")]
        private static class BlockUnderlyingGameUiWhileBloodlinkOpenPatch
        {
            private static bool Prefix()
            {
                var plugin = Instance;
                return plugin == null || !plugin.IsUiInputLocked;
            }
        }

        public void OpenMultiplayerPanel()
        {
            SetPanelVisible(true);
        }

        public void LogRejectedClientTransition(string sceneName)
        {
            Logger.LogWarning("Ignored local level transition while connected as a client; the host controls campaign progression (" + sceneName + ").");
        }

        public void LogLevelTransition(string message)
        {
            Logger.LogInfo(message);
        }

        internal void LogIsolatedPreferences(string path)
        {
            Logger.LogWarning("Another ULTRAKILL process holds the shared Preferences/Prefs.json file. This instance is using an isolated preferences profile at '" + path + "'.");
        }

        internal void SetLevelTransitionStatus(string message)
        {
            status = message;
        }

        public void LogMainMenuInjected()
        {
            Logger.LogInfo("Multiplayer button attached to the main menu.");
        }

        public void RequestCheckpoint(CheckPoint checkpoint)
        {
            if (checkpoint != null) CheckpointCoordinator.Request(checkpoint);
        }

        public void BroadcastCheckpointCommit(CheckPoint checkpoint)
        {
            var path = CheckpointCoordinator.GetStablePath(checkpoint);
            if (!string.IsNullOrEmpty(path))
            {
                CheckpointCoordinator.RecordCommit(path, SceneHelper.CurrentScene);
                network.BroadcastReliable(MessageType.CheckpointEvent, CheckpointEventCodec.Encode(CheckpointEventKind.Commit, path, checkpoint.transform.position));
            }
        }

        private void HandleCheckpointRequest(uint senderId, byte[] payload)
        {
            if (!network.Players.ContainsKey(senderId) || !network.HasFreshPlayerSnapshot(senderId, 2f) ||
                !network.SceneSnapshotSyncReady || !string.IsNullOrEmpty(SceneHelper.PendingScene)) return;
            var checkpointEvent = CheckpointEventCodec.Decode(payload);
            if (checkpointEvent.Kind != CheckpointEventKind.Request) return;
            PlayerSnapshot player;
            if (!network.Players.TryGetValue(senderId, out player) || (player.MovementFlags & 4) != 0) return;
            var checkpoint = CheckpointCoordinator.Resolve(checkpointEvent.Path, checkpointEvent.HasPosition ? (Vector3?)checkpointEvent.Position : null);
            if (checkpoint == null || !checkpoint.gameObject.scene.isLoaded ||
                checkpoint.gameObject.scene != SceneManager.GetActiveScene() ||
                (player.Position - checkpoint.transform.position).sqrMagnitude > 225f) return;
            if (checkpoint.activated)
            {
                network.SendToPlayer(senderId, MessageType.CheckpointEvent,
                    CheckpointEventCodec.Encode(CheckpointEventKind.Commit, CheckpointCoordinator.GetStablePath(checkpoint), checkpoint.transform.position));
                return;
            }
            checkpoint.ActivateCheckPoint();
        }
    }
}
