using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using ULTRAKILL.Bloodlink.Networking;
using UnityEngine;

namespace ULTRAKILL.Bloodlink.Players
{
    public sealed class RemotePlayerVisual : MonoBehaviour
    {
        private struct TimedSnapshot
        {
            public PlayerSnapshot State;
            public float ReceivedAt;
        }

        private readonly List<TimedSnapshot> buffer = new List<TimedSnapshot>(8);
        private readonly List<Animator> animators = new List<Animator>();
        private readonly Dictionary<Animator, HashSet<int>> animatorParameterHashes = new Dictionary<Animator, HashSet<int>>();
        private readonly List<GameObject> remoteCoinInstances = new List<GameObject>();
        private readonly List<GameObject> remoteNailInstances = new List<GameObject>();
        private readonly List<GameObject> remoteRocketInstances = new List<GameObject>();
        private Renderer[] visualRenderers;
        private MaterialPropertyBlock[] appearanceBlocks;
        private uint lastPrimaryColor = uint.MaxValue;
        private uint lastSecondaryColor = uint.MaxValue;
        private uint lastLightColor = uint.MaxValue;
        private byte lastGlowIntensity;
        private GameObject[] baseWeapons;
        private GameObject[] alternateWeapons;
        private Transform modelRoot;
        private Transform remoteHead;
        private Transform remoteRightArm;
        private Transform remoteChest;
        private Quaternion modelBaseLocalRotation = Quaternion.identity;
        private float landingLayerWeight;
        private TextMesh nameplate;
        private TextMesh nameplateShadow;
        private uint lastReceivedTick;
        private bool hasReceivedSnapshot;
        private Transform recoilingWeapon;
        private Vector3 weaponRestPosition;
        private Quaternion weaponRestRotation;
        private float recoilUntil;
        private int lastDeadState = -1;
        private float nextAnimatorDiagnosticAt;
        private byte lastWeaponSlot = byte.MaxValue;
        private byte lastWeaponVariation = byte.MaxValue;
        private bool hasWeaponState;
        private bool lastWeaponAlternate;
        private Vector3 sampledPlanarVelocity;
        private bool hasRenderedPosition;

        public uint PlayerId { get; private set; }
        internal bool HasRenderableModel
        {
            get
            {
                if (visualRenderers == null || visualRenderers.Length == 0) return false;
                for (var i = 0; i < visualRenderers.Length; i++)
                {
                    var renderer = visualRenderers[i];
                    if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                    var skinned = renderer as SkinnedMeshRenderer;
                    if (skinned != null && skinned.sharedMesh != null) return true;
                    var filter = renderer.GetComponent<MeshFilter>();
                    if (filter != null && filter.sharedMesh != null) return true;
                }
                return false;
            }
        }

        public static RemotePlayerVisual Create(PlayerAnimations template, uint playerId, string playerName)
        {
            return Create(template, null, playerId, playerName);
        }

        public static RemotePlayerVisual Create(PlayerAnimations template, Transform sourcePlayerRoot, uint playerId, string playerName)
        {
            var root = new GameObject("Bloodlink Remote Player " + playerId);
            var visual = root.AddComponent<RemotePlayerVisual>();
            visual.PlayerId = playerId;

            var model = new GameObject("Remote V1 Runtime Model");
            model.transform.SetParent(root.transform, false);
            model.SetActive(false);

            // The V1 rig has renderers and its Animator on PlayerAnimations' root.
            // Clone its full transform hierarchy so those root renderers and the
            // controller's bone bindings survive. The parent stays inactive while
            // the clone is sanitized, so none of the copied singleton scripts run.
            var rig = Instantiate(template.gameObject, model.transform, false);
            rig.name = "Remote V1 Rig";
            rig.transform.localPosition = sourcePlayerRoot != null
                ? sourcePlayerRoot.InverseTransformPoint(template.transform.position)
                : template.transform.localPosition;
            // A remote V1 body follows the player's yaw. Do not inherit the local
            // camera pitch or PlayerAnimations' temporary slide-facing rotation.
            rig.transform.localRotation = Quaternion.identity;
            rig.transform.localScale = GetRelativeScale(template.transform, sourcePlayerRoot);
            foreach (var behaviour in rig.GetComponentsInChildren<Behaviour>(true))
            {
                var animator = behaviour as Animator;
                if (animator != null)
                {
                    animator.enabled = true;
                    animator.applyRootMotion = false;
                    animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                    animator.updateMode = AnimatorUpdateMode.UnscaledTime;
                    if (!visual.animators.Contains(animator)) visual.animators.Add(animator);
                }
                else behaviour.enabled = false;
            }
            foreach (var collider in rig.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
            foreach (var body in rig.GetComponentsInChildren<Rigidbody>(true))
            {
                body.isKinematic = true;
                body.useGravity = false;
            }
            foreach (var camera in rig.GetComponentsInChildren<Camera>(true)) camera.enabled = false;
            foreach (var audio in rig.GetComponentsInChildren<AudioSource>(true))
            {
                audio.Stop();
                audio.enabled = false;
            }
            visual.modelRoot = rig.transform;
            visual.modelBaseLocalRotation = Quaternion.identity;

            foreach (var renderer in model.GetComponentsInChildren<Renderer>(true))
            {
                renderer.enabled = true;
                renderer.gameObject.layer = 0;
                var skinned = renderer as SkinnedMeshRenderer;
                if (skinned != null) skinned.updateWhenOffscreen = true;
            }
            visual.visualRenderers = model.GetComponentsInChildren<Renderer>(true);
            visual.appearanceBlocks = new MaterialPropertyBlock[visual.visualRenderers.Length];
            for (var i = 0; i < visual.appearanceBlocks.Length; i++) visual.appearanceBlocks[i] = new MaterialPropertyBlock();

            var animationFields = typeof(PlayerAnimations);
            var sourceWeapons = (GameObject[])AccessTools.Field(animationFields, "weapons").GetValue(template);
            var sourceAlternateWeapons = (GameObject[])AccessTools.Field(animationFields, "altweapons").GetValue(template);
            visual.baseWeapons = RemapWeapons(sourceWeapons, template.transform, rig.transform);
            visual.alternateWeapons = RemapWeapons(sourceAlternateWeapons, template.transform, rig.transform);
            var copiedAnimations = rig.GetComponent<PlayerAnimations>();
            visual.remoteHead = GetTransformField(copiedAnimations, "head");
            visual.remoteRightArm = GetTransformField(copiedAnimations, "rightArm");
            visual.remoteChest = GetTransformField(copiedAnimations, "chest");
            // Disabled MonoBehaviours still receive Awake when their clone becomes
            // active. Remove local singleton/controllers before activating this rig.
            foreach (var script in rig.GetComponentsInChildren<MonoBehaviour>(true))
                DestroyImmediate(script);
            model.SetActive(true);
            rig.SetActive(true);
            foreach (var animator in visual.animators)
            {
                // The source player controller is deliberately disabled on the
                // clone. Keep the Animator itself fully live and detach its pose
                // from the source player's camera/root-motion settings.
                animator.Rebind();
                animator.enabled = true;
                animator.applyRootMotion = false;
                animator.speed = 1f;
                animator.updateMode = AnimatorUpdateMode.UnscaledTime;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.Update(0f);
                visual.CacheAnimatorParameters(animator);
            }

            var plugin = Core.BloodlinkPlugin.Instance;
            if (plugin != null)
            {
                foreach (var animator in visual.animators)
                {
                    var controller = animator.runtimeAnimatorController;
                    plugin.LogRemoteAnimatorDiagnostics("Remote V1 animator initialized for player " + playerId +
                        ": controller=" + (controller != null ? controller.name : "<none>") +
                        ", avatar=" + (animator.avatar != null ? animator.avatar.name : "<none>") +
                        ", parameters=" + GetAnimatorParameterSummary(animator) +
                        ", enabled=" + animator.isActiveAndEnabled + ".");
                }
            }

            var nameObject = new GameObject("Remote Player Name", typeof(TextMesh));
            nameObject.transform.SetParent(root.transform, false);
            nameObject.transform.localPosition = new Vector3(0f, 2.1f, 0f);
            visual.nameplate = nameObject.GetComponent<TextMesh>();
            visual.nameplate.text = playerName ?? ("Player " + playerId);
            visual.nameplate.fontSize = 64;
            visual.nameplate.characterSize = 0.075f;
            visual.nameplate.anchor = TextAnchor.MiddleCenter;
            visual.nameplate.alignment = TextAlignment.Center;
            visual.nameplate.color = new Color(1f, 0.87f, 0.82f, 1f);
            visual.nameplate.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            visual.nameplate.GetComponent<MeshRenderer>().sortingOrder = 100;
            var shadowObject = new GameObject("Remote Player Name Shadow", typeof(TextMesh));
            shadowObject.transform.SetParent(root.transform, false);
            shadowObject.transform.localPosition = new Vector3(0f, 2.1f, 0.005f);
            visual.nameplateShadow = shadowObject.GetComponent<TextMesh>();
            visual.nameplateShadow.font = visual.nameplate.font;
            visual.nameplateShadow.fontSize = visual.nameplate.fontSize;
            visual.nameplateShadow.characterSize = visual.nameplate.characterSize;
            visual.nameplateShadow.anchor = TextAnchor.MiddleCenter;
            visual.nameplateShadow.alignment = TextAlignment.Center;
            visual.nameplateShadow.color = new Color(0f, 0f, 0f, 0.92f);
            visual.nameplateShadow.GetComponent<MeshRenderer>().sortingOrder = 99;
            visual.nameplateShadow.text = visual.nameplate.text;
            return visual;
        }

        private static Transform GetTransformField(PlayerAnimations animations, string fieldName)
        {
            if (animations == null) return null;
            var field = AccessTools.Field(typeof(PlayerAnimations), fieldName);
            return field != null ? field.GetValue(animations) as Transform : null;
        }

        private static Vector3 GetRelativeScale(Transform source, Transform sourceRoot)
        {
            if (sourceRoot == null) return source.localScale;
            var sourceScale = source.lossyScale;
            var rootScale = sourceRoot.lossyScale;
            return new Vector3(
                Mathf.Abs(rootScale.x) > 0.0001f ? sourceScale.x / rootScale.x : sourceScale.x,
                Mathf.Abs(rootScale.y) > 0.0001f ? sourceScale.y / rootScale.y : sourceScale.y,
                Mathf.Abs(rootScale.z) > 0.0001f ? sourceScale.z / rootScale.z : sourceScale.z);
        }

        private static Transform RemapTransform(Transform source, Transform sourceRoot, Transform cloneRoot)
        {
            if (source == null) return null;
            var path = GetRelativePath(source, sourceRoot);
            return string.IsNullOrEmpty(path) ? null : cloneRoot.Find(path);
        }

        private static GameObject[] RemapWeapons(GameObject[] source, Transform sourceRoot, Transform cloneRoot)
        {
            if (source == null) return Array.Empty<GameObject>();
            var result = new GameObject[source.Length];
            for (var i = 0; i < source.Length; i++)
            {
                if (source[i] == null) continue;
                var path = GetRelativePath(source[i].transform, sourceRoot);
                var mapped = string.IsNullOrEmpty(path) ? null : cloneRoot.Find(path);
                result[i] = mapped != null ? mapped.gameObject : null;
            }
            return result;
        }

        private static string GetRelativePath(Transform target, Transform root)
        {
            var names = new List<string>();
            var current = target;
            while (current != null && current != root)
            {
                names.Insert(0, current.name);
                current = current.parent;
            }
            return current == root ? string.Join("/", names.ToArray()) : string.Empty;
        }

        public void Push(PlayerSnapshot state)
        {
            if (state == null) return;
            if (!hasReceivedSnapshot || state.Tick != lastReceivedTick)
            {
                if (buffer.Count > 0)
                {
                    var previous = buffer[buffer.Count - 1];
                    var elapsed = Time.unscaledTime - previous.ReceivedAt;
                    if (elapsed > 0.005f && elapsed < 0.5f)
                    {
                        var observed = Vector3.ProjectOnPlane((state.Position - previous.State.Position) / elapsed, Vector3.up);
                        if (observed.sqrMagnitude < 260f * 260f) sampledPlanarVelocity = observed;
                    }
                }
                buffer.Add(new TimedSnapshot { State = state, ReceivedAt = Time.unscaledTime });
                lastReceivedTick = state.Tick;
                hasReceivedSnapshot = true;
                while (buffer.Count > 12) buffer.RemoveAt(0);
            }
            if (nameplate != null)
            {
                var isDead = (state.MovementFlags & 4) != 0;
                nameplate.text = isDead
                    ? (state.PlayerName ?? ("Player " + PlayerId)) + "\nDEAD"
                    : (state.PlayerName ?? ("Player " + PlayerId)) + "\n" + state.Health + " HP";
                if (nameplateShadow != null) nameplateShadow.text = nameplate.text;
                nameplate.color = isDead ? new Color(0.72f, 0.72f, 0.72f, 1f) : new Color(1f, 0.87f, 0.82f, 1f);
            }
            if ((state.MovementFlags & 4) != 0) SetWeapon(0, 0);
            else SetWeapon(state.WeaponSlot, state.WeaponVariation, state.WeaponAlternate);
            ApplyAppearance(state.PrimaryColor, state.SecondaryColor, state.LightColor, state.GlowIntensity);
        }

        private void LateUpdate()
        {
            UpdateWeaponRecoil();
            UpdateAnimationLayers();
            if (buffer.Count == 0) return;
            var renderTime = Time.unscaledTime - 0.1f;
            var first = buffer[0];
            var last = buffer[buffer.Count - 1];
            if (renderTime <= first.ReceivedAt)
            {
                Apply(first.State.Position, first.State.Rotation);
                Animate(first.State);
                return;
            }
            for (var i = 1; i < buffer.Count; i++)
            {
                var next = buffer[i];
                if (renderTime <= next.ReceivedAt)
                {
                    var previous = buffer[i - 1];
                    var duration = Mathf.Max(0.001f, next.ReceivedAt - previous.ReceivedAt);
                    var t = Mathf.Clamp01((renderTime - previous.ReceivedAt) / duration);
                    Apply(Vector3.Lerp(previous.State.Position, next.State.Position, t), Quaternion.Slerp(previous.State.Rotation, next.State.Rotation, t));
                    Animate(next.State);
                    return;
                }
            }
            var extrapolation = Mathf.Clamp(renderTime - last.ReceivedAt, 0f, 0.1f);
            Apply(last.State.Position + last.State.Velocity * extrapolation, last.State.Rotation);
            Animate(last.State);
        }

        public void PlayWeaponFire(byte slot, byte variation, byte fireKind, Quaternion aimRotation, bool weaponAlternate = false)
        {
            SetWeapon(slot, variation, weaponAlternate);
            var selected = slot > 0 ? slot - 1 : byte.MaxValue;
            var weapons = lastWeaponAlternate ? alternateWeapons : baseWeapons;
            if (selected >= weapons.Length || weapons[selected] == null) return;
            var weapon = weapons[selected].transform;
            PlayMuzzleFlash(weapon.gameObject, slot, fireKind, aimRotation);
            foreach (var animator in animators)
            {
                SetFloatIfPresent(animator, "ShootSpeed", 1f);
                SetTriggerIfPresent(animator, slot == 5 && fireKind == 9 ? "Spray" : "Shoot");
            }
            if (recoilingWeapon != weapon)
            {
                RestoreWeaponRecoil();
                recoilingWeapon = weapon;
                weaponRestPosition = weapon.localPosition;
                weaponRestRotation = weapon.localRotation;
            }
            recoilUntil = Time.unscaledTime + 0.075f;
            weapon.localPosition = weaponRestPosition + Vector3.back * 0.055f;
            weapon.localRotation = weaponRestRotation * Quaternion.Euler(-5f, 0f, 0f);
        }

        public void PlayPlayerAnimation(PlayerAnimationEvent animation)
        {
            if (animation == null) return;
            foreach (var animator in animators)
            {
                if (animation.Action == PlayerAnimationAction.Punch)
                {
                    SetFloatIfPresent(animator, "PunchSpeed", animation.Speed);
                    SetTriggerIfPresent(animator, "Punch");
                }
                else if (animation.Action == PlayerAnimationAction.CoinToss)
                {
                    SetTriggerIfPresent(animator, "CoinToss");
                }
                else if (animation.Action == PlayerAnimationAction.Parry)
                {
                    SetTriggerIfPresent(animator, "Parry");
                }
                else if (animation.Action == PlayerAnimationAction.Jump)
                {
                    SetTriggerIfPresent(animator, "Jump");
                }
                else if (animation.Action == PlayerAnimationAction.Land)
                {
                    landingLayerWeight = Mathf.Clamp01(animation.Speed);
                    if (animator.layerCount > 1) animator.SetLayerWeight(1, landingLayerWeight);
                }
            }
        }

        public void PlayCoinProjectile(CoinProjectileEvent coin)
        {
            if (coin == null || coin.OwnerPlayerId != PlayerId) return;
            var weaponSet = lastWeaponAlternate ? alternateWeapons : baseWeapons;
            if (weaponSet == null || weaponSet.Length == 0 || weaponSet[0] == null) return;
            GameObject coinPrefab = null;
            var behaviours = weaponSet[0].GetComponentsInChildren<Behaviour>(true);
            for (var i = 0; i < behaviours.Length; i++)
            {
                var behaviour = behaviours[i];
                if (behaviour == null || behaviour.GetType().Name != "Revolver") continue;
                var field = AccessTools.Field(behaviour.GetType(), "coin");
                if (field != null) coinPrefab = field.GetValue(behaviour) as GameObject;
                break;
            }
            if (coinPrefab == null) return;
            PlayPrefabOneShots(coinPrefab, coin.Position);

            for (var i = remoteCoinInstances.Count - 1; i >= 0; i--)
                if (remoteCoinInstances[i] == null) remoteCoinInstances.RemoveAt(i);
            while (remoteCoinInstances.Count >= 8)
            {
                if (remoteCoinInstances[0] != null) Destroy(remoteCoinInstances[0]);
                remoteCoinInstances.RemoveAt(0);
            }

            var instance = InstantiateCosmeticProjectile(coinPrefab, coin.Position, coin.Rotation, coin.Velocity,
                coin.Gravity, coin.AngularVelocity, 5f, "Bloodlink Remote Coin " + PlayerId);
            if (instance == null) return;
            remoteCoinInstances.Add(instance);
        }

        public void PlayNailProjectile(NailProjectileEvent projectile)
        {
            if (projectile == null || projectile.OwnerPlayerId != PlayerId) return;
            var weaponSet = lastWeaponAlternate ? alternateWeapons : baseWeapons;
            if (weaponSet == null || weaponSet.Length <= 2 || weaponSet[2] == null) return;
            GameObject projectilePrefab = null;
            var behaviours = weaponSet[2].GetComponentsInChildren<Behaviour>(true);
            for (var i = 0; i < behaviours.Length; i++)
            {
                var behaviour = behaviours[i];
                if (behaviour == null || behaviour.GetType().Name != "Nailgun") continue;
                var fieldName = projectile.VisualKind == 1 || projectile.VisualKind == 3 ? "heatedNail" : "nail";
                var field = AccessTools.Field(behaviour.GetType(), fieldName);
                if (field != null) projectilePrefab = field.GetValue(behaviour) as GameObject;
                break;
            }
            if (projectilePrefab == null) return;

            for (var i = remoteNailInstances.Count - 1; i >= 0; i--)
                if (remoteNailInstances[i] == null) remoteNailInstances.RemoveAt(i);
            while (remoteNailInstances.Count >= 40)
            {
                if (remoteNailInstances[0] != null) Destroy(remoteNailInstances[0]);
                remoteNailInstances.RemoveAt(0);
            }

            var instance = InstantiateCosmeticProjectile(projectilePrefab, projectile.Position, projectile.Rotation,
                projectile.Velocity, projectile.Gravity, projectile.AngularVelocity, 3f,
                "Bloodlink Remote Nail Projectile " + PlayerId);
            if (instance == null) return;
            remoteNailInstances.Add(instance);
        }

        public void PlayRocketProjectile(RocketProjectileEvent projectile)
        {
            if (projectile == null || projectile.OwnerPlayerId != PlayerId) return;
            var weaponSet = lastWeaponAlternate ? alternateWeapons : baseWeapons;
            if (weaponSet == null || weaponSet.Length <= 4 || weaponSet[4] == null) return;
            GameObject projectilePrefab = null;
            var behaviours = weaponSet[4].GetComponentsInChildren<Behaviour>(true);
            for (var i = 0; i < behaviours.Length; i++)
            {
                var behaviour = behaviours[i];
                if (behaviour == null || behaviour.GetType().Name != "RocketLauncher") continue;
                var fieldName = projectile.VisualKind == 1 ? "cannonBall"
                    : projectile.VisualKind == 2 ? "napalmProjectile" : "rocket";
                var field = AccessTools.Field(behaviour.GetType(), fieldName);
                var value = field != null ? field.GetValue(behaviour) : null;
                var body = value as Rigidbody;
                projectilePrefab = body != null ? body.gameObject : value as GameObject;
                break;
            }
            if (projectilePrefab == null) return;

            for (var i = remoteRocketInstances.Count - 1; i >= 0; i--)
                if (remoteRocketInstances[i] == null) remoteRocketInstances.RemoveAt(i);
            while (remoteRocketInstances.Count >= 12)
            {
                if (remoteRocketInstances[0] != null) Destroy(remoteRocketInstances[0]);
                remoteRocketInstances.RemoveAt(0);
            }

            var instance = InstantiateCosmeticProjectile(projectilePrefab, projectile.Position, projectile.Rotation,
                projectile.Velocity, projectile.Gravity, projectile.AngularVelocity, 4f,
                "Bloodlink Remote Rocket Projectile " + PlayerId);
            if (instance == null) return;
            remoteRocketInstances.Add(instance);
        }

        private static GameObject InstantiateCosmeticProjectile(GameObject prefab, Vector3 position, Quaternion rotation,
            Vector3 velocity, Vector3 gravity, Vector3 angularVelocity, float lifetime, string objectName)
        {
            if (prefab == null) return null;
            var guard = new GameObject("Bloodlink Inactive Projectile Guard");
            guard.SetActive(false);
            var instance = Instantiate(prefab, position, rotation, guard.transform);
            if (instance == null)
            {
                Destroy(guard);
                return null;
            }
            instance.name = objectName;
            instance.SetActive(false);
            foreach (var behaviour in instance.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled = false;
            foreach (var collider in instance.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
            foreach (var body in instance.GetComponentsInChildren<Rigidbody>(true))
            {
                body.isKinematic = true;
                body.useGravity = false;
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
            foreach (var audio in instance.GetComponentsInChildren<AudioSource>(true))
            {
                audio.Stop();
                audio.enabled = false;
            }
            foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true)) renderer.enabled = true;
            var flight = instance.AddComponent<CosmeticProjectileFlight>();
            flight.Initialize(velocity, gravity, angularVelocity, lifetime);
            instance.transform.SetParent(null, true);
            Destroy(guard);
            instance.SetActive(true);
            return instance;
        }

        private static void PlayMuzzleFlash(GameObject weapon, byte slot, byte fireKind, Quaternion aimRotation)
        {
            if (weapon == null) return;
            var behaviours = weapon.GetComponentsInChildren<Behaviour>(true);
            GameObject flashPrefab = null;
            Transform muzzle = null;
            for (var i = 0; i < behaviours.Length; i++)
            {
                var behaviour = behaviours[i];
                if (behaviour == null) continue;
                var type = behaviour.GetType();
                var typeName = type.Name;
                var belongsToWeapon = (slot == 1 && typeName == "Revolver")
                    || (slot == 2 && typeName == "Shotgun")
                    || (slot == 3 && typeName == "Nailgun")
                    || (slot == 4 && typeName == "Railcannon")
                    || (slot == 5 && typeName == "RocketLauncher");
                if (!belongsToWeapon) continue;

                var flashField = AccessTools.Field(type, slot == 3 && fireKind == 7 ? "muzzleFlash2" : "muzzleFlash");
                if (flashField != null) flashPrefab = flashField.GetValue(behaviour) as GameObject;

                var pointField = AccessTools.Field(type, "shootPoint");
                if (pointField != null) muzzle = pointField.GetValue(behaviour) as Transform;
                if (muzzle == null)
                {
                    pointField = AccessTools.Field(type, "shootPoints");
                    var points = pointField != null ? pointField.GetValue(behaviour) as Transform[] : null;
                    if (points != null && points.Length > 0) muzzle = points[0];
                    if (muzzle == null)
                    {
                        var gameObjectPoints = pointField != null ? pointField.GetValue(behaviour) as GameObject[] : null;
                        if (gameObjectPoints != null && gameObjectPoints.Length > 0)
                        {
                            var barrelField = AccessTools.Field(type, "barrelNum");
                            var barrel = barrelField != null ? (int)barrelField.GetValue(behaviour) : 0;
                            barrel = Mathf.Clamp(barrel, 0, gameObjectPoints.Length - 1);
                            if (gameObjectPoints[barrel] != null) muzzle = gameObjectPoints[barrel].transform;
                        }
                    }
                }
                if (muzzle == null && slot == 1)
                {
                    var barrelField = AccessTools.Field(type, "gunBarrel");
                    var barrelObject = barrelField != null ? barrelField.GetValue(behaviour) as GameObject : null;
                    if (barrelObject != null) muzzle = barrelObject.transform;
                }
                if (slot == 5 && fireKind == 9)
                {
                    var sprayField = AccessTools.Field(type, "napalmMuzzleFlashParticles");
                    var spray = sprayField != null ? sprayField.GetValue(behaviour) as ParticleSystem : null;
                    if (spray != null)
                    {
                        if (muzzle == null) muzzle = spray.transform;
                        spray.transform.SetPositionAndRotation(muzzle.position, aimRotation);
                        spray.Play(true);
                        return;
                    }
                }
                break;
            }

            if (muzzle == null) muzzle = weapon.transform;
            if (flashPrefab != null)
            {
                PlayPrefabOneShots(flashPrefab, muzzle.position);
                var flash = Instantiate(flashPrefab, muzzle.position, aimRotation);
                foreach (var audio in flash.GetComponentsInChildren<AudioSource>(true))
                {
                    audio.Stop();
                    audio.enabled = false;
                }
                foreach (var collider in flash.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
                foreach (var body in flash.GetComponentsInChildren<Rigidbody>(true))
                {
                    body.isKinematic = true;
                    body.useGravity = false;
                }
                foreach (var particle in flash.GetComponentsInChildren<ParticleSystem>(true)) particle.Play(true);
                Destroy(flash, 1.25f);
                return;
            }

            var lightObject = new GameObject("Bloodlink Remote Muzzle Flash");
            lightObject.transform.SetPositionAndRotation(muzzle.position, aimRotation);
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = slot == 4 ? new Color(0.2f, 0.8f, 1f) : new Color(1f, 0.32f, 0.08f);
            light.intensity = slot == 4 ? 5f : 2.5f;
            light.range = slot == 4 ? 5f : 2.25f;
            Destroy(lightObject, 0.08f);
        }

        private static void PlayPrefabOneShots(GameObject prefab, Vector3 position)
        {
            if (prefab == null) return;
            var sources = prefab.GetComponentsInChildren<AudioSource>(true);
            var played = 0;
            for (var i = 0; i < sources.Length && played < 4; i++)
            {
                var template = sources[i];
                if (template == null || template.clip == null || template.loop) continue;
                var soundObject = new GameObject("Bloodlink spatial sound");
                soundObject.transform.position = position;
                var source = soundObject.AddComponent<AudioSource>();
                source.clip = template.clip;
                source.volume = Mathf.Clamp01(template.volume);
                source.pitch = Mathf.Clamp(template.pitch, 0.25f, 3f);
                source.spatialBlend = 1f;
                source.minDistance = Mathf.Max(0.1f, template.minDistance);
                source.maxDistance = Mathf.Max(source.minDistance + 0.1f, Mathf.Min(80f, template.maxDistance));
                source.rolloffMode = template.rolloffMode;
                source.dopplerLevel = 0f;
                source.Play();
                Destroy(soundObject, Mathf.Max(0.25f, template.clip.length / Mathf.Max(0.25f, Mathf.Abs(source.pitch)) + 0.2f));
                played++;
            }
        }

        private void UpdateWeaponRecoil()
        {
            if (recoilingWeapon == null) return;
            if (Time.unscaledTime >= recoilUntil)
            {
                RestoreWeaponRecoil();
                return;
            }
            var remaining = Mathf.Clamp01((recoilUntil - Time.unscaledTime) / 0.075f);
            recoilingWeapon.localPosition = weaponRestPosition + Vector3.back * (0.055f * remaining);
            recoilingWeapon.localRotation = weaponRestRotation * Quaternion.Euler(-5f * remaining, 0f, 0f);
        }

        private void RestoreWeaponRecoil()
        {
            if (recoilingWeapon != null)
            {
                recoilingWeapon.localPosition = weaponRestPosition;
                recoilingWeapon.localRotation = weaponRestRotation;
            }
            recoilingWeapon = null;
            recoilUntil = 0f;
        }

        private void Apply(Vector3 position, Quaternion rotation)
        {
            // Network body transforms can carry camera pitch/roll on some game
            // states. Only yaw belongs on a standing V1 body model.
            var yaw = rotation.eulerAngles.y;
            if (hasRenderedPosition && (position - transform.position).sqrMagnitude < 20f * 20f)
                position = Vector3.Lerp(transform.position, position, PlayerPositionCorrectionPolicy.RemoteBlend(Time.unscaledDeltaTime));
            hasRenderedPosition = true;
            transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yaw, 0f));
            var camera = Camera.main;
            if (camera != null && nameplate != null)
            {
                var facing = Quaternion.LookRotation(nameplate.transform.position - camera.transform.position);
                nameplate.transform.rotation = facing;
                if (nameplateShadow != null) nameplateShadow.transform.rotation = facing;
            }
        }

        private void Animate(PlayerSnapshot state)
        {
            if (animators.Count == 0) return;
            var planarVelocity = Vector3.ProjectOnPlane(state.Velocity, Vector3.up);
            if (planarVelocity.sqrMagnitude < 0.25f && sampledPlanarVelocity.sqrMagnitude > 0.25f)
                planarVelocity = sampledPlanarVelocity;
            var localVelocity = transform.InverseTransformDirection(planarVelocity) / 15f;
            var dodging = (state.MovementFlags & 1) != 0;
            var sliding = (state.MovementFlags & 2) != 0;
            var grounded = (state.MovementFlags & 8) != 0;
            var bigWeapon = state.WeaponSlot >= 3;
            // A stale lifecycle packet can leave the death flag set after health was
            // restored. Do not pin the V1 locomotion blend tree to idle in that case.
            var dead = (state.MovementFlags & 4) != 0 && state.Health == 0;
            if (lastDeadState != (dead ? 1 : 0))
            {
                lastDeadState = dead ? 1 : 0;
                foreach (var animator in animators)
                {
                    SetBoolIfPresent(animator, "Dead", dead);
                    SetBoolIfPresent(animator, "IsDead", dead);
                    if (dead)
                    {
                        SetTriggerIfPresent(animator, "Death");
                        SetTriggerIfPresent(animator, "Die");
                    }
                }
                if (!dead) SetWeapon(state.WeaponSlot, state.WeaponVariation, state.WeaponAlternate);
            }
            foreach (var animator in animators)
            {
                SetBoolIfPresent(animator, "Dodging", dodging && !sliding);
                SetBoolIfPresent(animator, "Sliding", sliding);
                SetBoolIfPresent(animator, "InAir", !grounded);
                SetBoolIfPresent(animator, "BigWeapon", bigWeapon);
                SetFloatIfPresent(animator, "VelX", dead ? 0f : localVelocity.x);
                SetFloatIfPresent(animator, "VelY", dead ? 0f : localVelocity.z);
                // Locomotion on ULTRAKILL's remote V1 body is a blend tree driven by
                // VelX/VelY. The host's first-person viewmodel state hash is unrelated
                // to this controller and can pin the body to its idle state.
            }
            if (Time.unscaledTime >= nextAnimatorDiagnosticAt)
            {
                nextAnimatorDiagnosticAt = Time.unscaledTime + 5f;
                var plugin = Core.BloodlinkPlugin.Instance;
                if (plugin != null)
                {
                    foreach (var animator in animators)
                    {
                        if (animator == null || !animator.isInitialized || animator.runtimeAnimatorController == null || animator.layerCount == 0) continue;
                        var current = animator.GetCurrentAnimatorStateInfo(0);
                        plugin.LogRemoteAnimatorDiagnostics("Remote V1 animation state player " + PlayerId +
                            ": state=" + current.fullPathHash +
                            ", time=" + current.normalizedTime.ToString("F2") +
                            ", SnapshotVel=" + state.Velocity.ToString("F2") +
                            ", LocalVel=" + localVelocity.ToString("F2") +
                            ", Flags=" + state.MovementFlags +
                            ", Dead=" + dead +
                            ", VelX=" + animator.GetFloat("VelX").ToString("F2") +
                            ", VelY=" + animator.GetFloat("VelY").ToString("F2") +
                            ", InAir=" + animator.GetBool("InAir") +
                            ", Sliding=" + animator.GetBool("Sliding") +
                            ", Dodging=" + animator.GetBool("Dodging") + ".");
                    }
                }
            }
            UpdateModelFacing(state, sliding);
            ApplyRemoteAim(state.ViewRotation, bigWeapon);
        }

        private void UpdateModelFacing(PlayerSnapshot state, bool sliding)
        {
            if (modelRoot == null) return;
            var bodyYaw = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
            var target = bodyYaw * modelBaseLocalRotation;
            var planarVelocity = Vector3.ProjectOnPlane(state.Velocity, Vector3.up);
            if (sliding && planarVelocity.sqrMagnitude > 1f)
                target = Quaternion.LookRotation(planarVelocity.normalized, Vector3.up) * modelBaseLocalRotation;
            modelRoot.rotation = Quaternion.Slerp(modelRoot.rotation, target, 1f - Mathf.Exp(-12f * Time.unscaledDeltaTime));
        }

        private void UpdateAnimationLayers()
        {
            if (landingLayerWeight <= 0f) return;
            landingLayerWeight = Mathf.MoveTowards(landingLayerWeight, 0f, Time.unscaledDeltaTime * 3f);
            foreach (var animator in animators)
                if (animator != null && animator.layerCount > 1) animator.SetLayerWeight(1, landingLayerWeight);
        }

        private void ApplyRemoteAim(Quaternion viewRotation, bool bigWeapon)
        {
            if (remoteHead != null) remoteHead.rotation = viewRotation;
            if (remoteChest != null)
            {
                var pitch = viewRotation.eulerAngles.x;
                if (pitch > 180f) pitch -= 360f;
                remoteChest.localRotation = Quaternion.Euler(pitch * 0.35f, 0f, 0f);
            }
            if (remoteRightArm == null) return;
            if (!bigWeapon) AimRemoteArm(remoteRightArm, viewRotation);
            if (remoteRightArm.childCount > 0) AimRemoteArm(remoteRightArm.GetChild(0), viewRotation);
        }

        private static void AimRemoteArm(Transform arm, Quaternion viewRotation)
        {
            if (arm == null) return;
            arm.rotation = viewRotation;
            arm.Rotate(Vector3.right, 90f, Space.Self);
            arm.Rotate(Vector3.up, 180f, Space.Self);
        }

        private void SetWeapon(byte slot, byte variation, bool? alternate = null)
        {
            var useAlternate = alternate ?? lastWeaponAlternate;
            if (hasWeaponState && lastWeaponSlot == slot && lastWeaponVariation == variation && lastWeaponAlternate == useAlternate) return;
            var changed = hasWeaponState;
            hasWeaponState = true;
            lastWeaponSlot = slot;
            lastWeaponVariation = variation;
            lastWeaponAlternate = useAlternate;
            var selected = slot > 0 ? slot - 1 : byte.MaxValue;
            for (var i = 0; i < baseWeapons.Length; i++)
                if (baseWeapons[i] != null) baseWeapons[i].SetActive(i == selected && !useAlternate);
            for (var i = 0; i < alternateWeapons.Length; i++)
                if (alternateWeapons[i] != null) alternateWeapons[i].SetActive(i == selected && useAlternate);
            if (changed)
                foreach (var animator in animators) SetTriggerIfPresent(animator, "ChangeWeapon");
        }

        private void ApplyAppearance(uint primary, uint secondary, uint light, byte glow)
        {
            if (primary == lastPrimaryColor && secondary == lastSecondaryColor && light == lastLightColor && glow == lastGlowIntensity) return;
            lastPrimaryColor = primary;
            lastSecondaryColor = secondary;
            lastLightColor = light;
            lastGlowIntensity = glow;
            var primaryTint = PlayerAppearanceColor.Unpack(primary);
            var secondaryTint = PlayerAppearanceColor.Unpack(secondary);
            var lightTint = PlayerAppearanceColor.Unpack(light);
            var emission = lightTint * (glow / 255f);
            var colorId = Shader.PropertyToID("_Color");
            var baseColorId = Shader.PropertyToID("_BaseColor");
            var emissionId = Shader.PropertyToID("_EmissionColor");
            for (var i = 0; i < visualRenderers.Length; i++)
            {
                var renderer = visualRenderers[i];
                if (renderer == null) continue;
                var materialName = renderer.sharedMaterial != null ? renderer.sharedMaterial.name : string.Empty;
                var hint = (renderer.gameObject.name + " " + materialName).ToLowerInvariant();
                var tint = IsLightDetail(hint) ? lightTint : IsSecondaryDetail(hint) ? secondaryTint : primaryTint;
                var block = appearanceBlocks[i];
                renderer.GetPropertyBlock(block);
                block.SetColor(colorId, tint);
                block.SetColor(baseColorId, tint);
                block.SetColor(emissionId, emission);
                renderer.SetPropertyBlock(block);
            }
        }

        private static bool IsLightDetail(string hint)
        {
            return hint.Contains("eye") || hint.Contains("light") || hint.Contains("glow") || hint.Contains("emiss") || hint.Contains("led");
        }

        private static bool IsSecondaryDetail(string hint)
        {
            return hint.Contains("secondary") || hint.Contains("accent") || hint.Contains("trim") || hint.Contains("panel") || hint.Contains("joint");
        }

        private void SetBoolIfPresent(Animator animator, string name, bool value)
        {
            if (HasParameter(animator, name, AnimatorControllerParameterType.Bool)) animator.SetBool(name, value);
        }

        private void SetFloatIfPresent(Animator animator, string name, float value)
        {
            if (HasParameter(animator, name, AnimatorControllerParameterType.Float)) animator.SetFloat(name, value);
        }

        private void SetTriggerIfPresent(Animator animator, string name)
        {
            if (HasParameter(animator, name, AnimatorControllerParameterType.Trigger)) animator.SetTrigger(name);
        }

        private void CacheAnimatorParameters(Animator animator)
        {
            if (animator == null) return;
            var hashes = new HashSet<int>();
            foreach (var parameter in animator.parameters)
                hashes.Add(ParameterKey(parameter.nameHash, parameter.type));
            animatorParameterHashes[animator] = hashes;
        }

        private static string GetAnimatorParameterSummary(Animator animator)
        {
            if (animator == null) return "<no animator>";
            var parameters = animator.parameters;
            var builder = new StringBuilder(parameters.Length * 16);
            for (var i = 0; i < parameters.Length; i++)
            {
                if (i != 0) builder.Append(", ");
                builder.Append(parameters[i].name).Append(':').Append(parameters[i].type);
            }
            return builder.ToString();
        }

        private bool HasParameter(Animator animator, string name, AnimatorControllerParameterType type)
        {
            if (animator == null) return false;
            HashSet<int> hashes;
            if (!animatorParameterHashes.TryGetValue(animator, out hashes) || hashes.Count == 0)
                CacheAnimatorParameters(animator);
            return animatorParameterHashes.TryGetValue(animator, out hashes) &&
                hashes.Contains(ParameterKey(Animator.StringToHash(name), type));
        }

        private static int ParameterKey(int nameHash, AnimatorControllerParameterType type)
        {
            unchecked { return (nameHash * 397) ^ (int)type; }
        }
    }
}
