using System.Collections.Generic;
using ULTRAKILL.Bloodlink.Networking;
using ULTRAKILL.Bloodlink.Core;
using UnityEngine;

namespace ULTRAKILL.Bloodlink.Players
{
    internal sealed class SpectatorController
    {
        private bool wasDead;
        private float deathStartedAt;
        private uint followedPlayerId;
        private readonly List<PlayerSnapshot> livingPlayers = new List<PlayerSnapshot>(4);
        private CameraController detachedCameraController;
        private Transform detachedCamera;
        private Transform originalCameraParent;
        private Vector3 originalCameraLocalPosition;
        private Quaternion originalCameraLocalRotation;
        private bool originalControllerEnabled;
        private bool cameraDetached;
        private Rigidbody frozenBody;
        private bool bodyWasKinematic;
        private bool bodyUsedGravity;
        private Vector3 cameraVelocity;

        public bool IsActive { get; private set; }
        public bool ShowHud { get; private set; }
        public string Status { get; private set; } = "Spectator";
        private bool respawnRequested;

        public bool ConsumeRespawnRequest()
        {
            if (!respawnRequested) return false;
            respawnRequested = false;
            return true;
        }

        public void PrepareForRespawn()
        {
            RestoreCamera();
            wasDead = false;
            IsActive = ShowHud = false;
            followedPlayerId = 0;
            respawnRequested = false;
        }

        public void Update(NewMovement localPlayer, NetworkService network)
        {
            if (localPlayer == null || network == null || !network.IsConnected || !localPlayer.dead)
            {
                RestoreCamera();
                wasDead = false;
                IsActive = false;
                ShowHud = false;
                followedPlayerId = 0;
                respawnRequested = false;
                return;
            }

            if (!wasDead)
            {
                wasDead = true;
                deathStartedAt = Time.unscaledTime;
            }

            if (Time.unscaledTime - deathStartedAt < 2.1f)
            {
                ShowHud = false;
                Status = "ELIMINATED  /  spectator starts after the death sequence";
                return;
            }
            ShowHud = true;
            if (Input.GetKeyDown(KeyCode.R)) respawnRequested = true;

            if (localPlayer.deathSequence != null && localPlayer.deathSequence.gameObject.activeSelf)
                localPlayer.deathSequence.gameObject.SetActive(false);

            livingPlayers.Clear();
            foreach (var player in network.Players.Values)
            {
                if (player.PlayerId != network.LocalPlayerId && (player.MovementFlags & 4) == 0 &&
                    network.HasFreshPlayerSnapshot(player.PlayerId, 3f))
                    livingPlayers.Add(player);
            }
            livingPlayers.Sort(ComparePlayerIds);
            if (livingPlayers.Count == 0)
            {
                IsActive = false;
                Status = "WAITING  /  no fresh living teammate snapshot";
                return;
            }

            var index = livingPlayers.FindIndex(player => player.PlayerId == followedPlayerId);
            if (index < 0) index = 0;
            if (Input.GetKeyDown(KeyCode.RightBracket)) index = (index + 1) % livingPlayers.Count;
            else if (Input.GetKeyDown(KeyCode.LeftBracket)) index = (index + livingPlayers.Count - 1) % livingPlayers.Count;
            var target = livingPlayers[index];
            if (followedPlayerId != target.PlayerId) cameraVelocity = Vector3.zero;
            followedPlayerId = target.PlayerId;
            IsActive = true;
            Status = "SPECTATING  /  " + target.PlayerName + "  /  [ and ] switch  /  R respawn  /  " + target.Health + " HP";

            var cameraController = localPlayer.cc;
            if (cameraController == null || cameraController.cam == null) return;
            DetachCamera(cameraController);
        }

        private static int ComparePlayerIds(PlayerSnapshot left, PlayerSnapshot right)
        {
            return left.PlayerId.CompareTo(right.PlayerId);
        }

        public void LateUpdate(NewMovement localPlayer, NetworkService network)
        {
            if (!ShowHud || !IsActive || localPlayer == null || network == null || !network.IsConnected || !localPlayer.dead) return;
            PlayerSnapshot target = null;
            foreach (var player in network.Players.Values)
                if (player.PlayerId == followedPlayerId && (player.MovementFlags & 4) == 0 &&
                    network.HasFreshPlayerSnapshot(player.PlayerId, 3f)) { target = player; break; }
            if (target == null || localPlayer.cc == null || localPlayer.cc.cam == null) return;

            var cameraTransform = localPlayer.cc.cam.transform;
            if (!cameraDetached) DetachCamera(localPlayer.cc);
            var position = target.Position;
            if (BloodlinkPlugin.Instance != null) BloodlinkPlugin.Instance.TryGetSpectatorPose(target.PlayerId, out position);
            var dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            var viewRotation = Quaternion.Slerp(cameraTransform.rotation, target.ViewRotation, 1f - Mathf.Exp(-10f * dt));
            var head = position + Vector3.up * 0.65f;
            var backward = -(viewRotation * Vector3.forward);
            var distance = 3.25f;
            foreach (var hit in Physics.SphereCastAll(head, 0.18f, backward, distance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                if (hit.collider == null || hit.collider.GetComponentInParent<NewMovement>() != null ||
                    hit.collider.GetComponentInParent<RemotePlayerVisual>() != null || hit.collider.GetComponentInParent<EnemyIdentifier>() != null) continue;
                distance = Mathf.Min(distance, Mathf.Max(0.25f, hit.distance - 0.15f));
            }
            cameraTransform.position = Vector3.SmoothDamp(cameraTransform.position, head + backward * distance, ref cameraVelocity, 0.13f, 120f, dt);
            cameraTransform.rotation = viewRotation;
        }

        private void DetachCamera(CameraController controller)
        {
            if (cameraDetached || controller == null || controller.cam == null) return;
            detachedCameraController = controller;
            originalControllerEnabled = controller.enabled;
            detachedCamera = controller.cam.transform;
            originalCameraParent = detachedCamera.parent;
            originalCameraLocalPosition = detachedCamera.localPosition;
            originalCameraLocalRotation = detachedCamera.localRotation;
            controller.enabled = false;
            var player = controller.GetComponentInParent<NewMovement>();
            if (player == null) player = MonoSingleton<NewMovement>.Instance;
            if (player != null && player.rb != null)
            {
                frozenBody = player.rb;
                bodyWasKinematic = frozenBody.isKinematic;
                bodyUsedGravity = frozenBody.useGravity;
                if (!frozenBody.isKinematic) { frozenBody.velocity = Vector3.zero; frozenBody.angularVelocity = Vector3.zero; }
                frozenBody.isKinematic = true;
                frozenBody.useGravity = false;
            }
            cameraVelocity = Vector3.zero;
            detachedCamera.SetParent(null, true);
            cameraDetached = true;
        }

        private void RestoreCamera()
        {
            if (frozenBody != null)
            {
                frozenBody.isKinematic = bodyWasKinematic;
                frozenBody.useGravity = bodyUsedGravity;
                frozenBody = null;
            }
            if (!cameraDetached) return;
            if (detachedCamera != null)
            {
                detachedCamera.SetParent(originalCameraParent, false);
                detachedCamera.localPosition = originalCameraLocalPosition;
                detachedCamera.localRotation = originalCameraLocalRotation;
            }
            if (detachedCameraController != null) detachedCameraController.enabled = originalControllerEnabled;
            detachedCameraController = null;
            detachedCamera = null;
            originalCameraParent = null;
            cameraDetached = false;
            cameraVelocity = Vector3.zero;
        }
    }
}
