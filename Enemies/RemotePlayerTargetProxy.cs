using System.Threading;
using System.Collections.Generic;
using ULTRAKILL.Bloodlink.Networking;
using ULTRAKILL.Enemy;
using ULTRAKILL.Portal;
using UnityEngine;

namespace ULTRAKILL.Bloodlink.Enemies
{
    /// <summary>
    /// Registers a network teammate with ULTRAKILL's native enemy vision target tracker.
    /// The host's AI can then select remote players using the same targeting queries as the local player.
    /// </summary>
    internal sealed class RemotePlayerTargetProxy : MonoBehaviour, ITarget
    {
        private static readonly List<RemotePlayerTargetProxy> activeTargets = new List<RemotePlayerTargetProxy>(4);
        private static int legacySelectionCursor;
        private CancellationTokenSource registrationLifetime;
        private Transform headTarget;
        private uint playerId;
        private Vector3 snapshotPosition;
        private Quaternion snapshotRotation = Quaternion.identity;
        private Vector3 snapshotVelocity;
        private float snapshotReceivedAt;
        private bool hasSnapshot;
        private bool snapshotIsFresh;
        private bool isDead;

        public int Id => unchecked((int)(0x80000000u | (playerId & 0x7fffffffu)));
        public TargetType Type => hasSnapshot && snapshotIsFresh && !isDead ? TargetType.PLAYER : TargetType.GLASS;
        public EnemyIdentifier EID => null;
        public GameObject GameObject => gameObject;
        public Rigidbody Rigidbody => null;
        public Transform Transform => transform;
        public Vector3 Position { get; private set; }
        public Vector3 HeadPosition { get; private set; }
        public Vector3 Velocity => snapshotVelocity;
        public bool IsGrounded => (movementFlags & 8) != 0;

        private byte movementFlags;

        private void Awake()
        {
            var head = new GameObject("Bloodlink Remote Player Aim Point");
            head.transform.SetParent(transform, false);
            head.transform.localPosition = Vector3.up * 1.35f;
            headTarget = head.transform;
        }

        private void OnEnable()
        {
            if (!activeTargets.Contains(this)) activeTargets.Add(this);
        }

        public void SetState(PlayerSnapshot state, bool isFresh)
        {
            if (state == null || state.PlayerId == 0) return;
            playerId = state.PlayerId;
            snapshotPosition = state.Position;
            snapshotRotation = state.Rotation;
            snapshotVelocity = state.Velocity;
            movementFlags = state.MovementFlags;
            snapshotReceivedAt = Time.unscaledTime;
            snapshotIsFresh = isFresh;
            isDead = (state.MovementFlags & 4) != 0 || state.Health == 0;
            hasSnapshot = true;
            UpdateCachedTransformData();
        }

        private void Update()
        {
            if (registrationLifetime == null && MonoSingleton<PortalManagerV2>.TryGetInstance(out var portalManager))
            {
                registrationLifetime = new CancellationTokenSource();
                portalManager.TargetTracker.RegisterTarget(this, registrationLifetime.Token);
            }
            UpdateCachedTransformData();
        }

        public void SetData(ref TargetData data)
        {
            data.position = Position;
            data.headPosition = HeadPosition;
            data.realPosition = Position;
            data.realHeadPosition = HeadPosition;
            data.rotation = snapshotRotation;
            data.velocity = snapshotVelocity;
        }

        public void UpdateCachedTransformData()
        {
            if (!hasSnapshot)
            {
                Position = transform.position;
                HeadPosition = Position + Vector3.up * 1.35f;
                return;
            }

            var elapsed = Mathf.Clamp(Time.unscaledTime - snapshotReceivedAt, 0f, 0.1f);
            Position = snapshotPosition + snapshotVelocity * elapsed;
            HeadPosition = Position + Vector3.up * 1.35f;
        }

        public Transform LegacyHeadTransform => headTarget != null ? headTarget : transform;

        public static bool TryGetNextLegacyTarget(out RemotePlayerTargetProxy target)
        {
            target = null;
            var count = activeTargets.Count;
            if (count == 0) return false;
            var start = legacySelectionCursor % count;
            if (start < 0) start = 0;
            for (var i = 0; i < count; i++)
            {
                var index = (start + i) % count;
                var candidate = activeTargets[index];
                if (candidate == null || !candidate.hasSnapshot || !candidate.snapshotIsFresh || candidate.isDead) continue;
                target = candidate;
                legacySelectionCursor = (index + 1) % count;
                return true;
            }
            return false;
        }

        private void OnDisable()
        {
            activeTargets.Remove(this);
        }

        private void OnDestroy()
        {
            activeTargets.Remove(this);
            if (registrationLifetime == null) return;
            registrationLifetime.Cancel();
            registrationLifetime.Dispose();
            registrationLifetime = null;
        }
    }
}
