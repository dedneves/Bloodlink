using System.Collections.Generic;
using ULTRAKILL.Bloodlink.Core;
using UnityEngine;
using UnityEngine.AI;

namespace ULTRAKILL.Bloodlink.Enemies
{
    /// <summary>
    /// Suppresses local enemy decision-making on clients while leaving collision,
    /// health, renderers, and animation available for host snapshots.
    /// </summary>
    internal sealed class EnemyReplicaAuthority : MonoBehaviour
    {
        private readonly Dictionary<Behaviour, bool> originalBehaviours = new Dictionary<Behaviour, bool>();
        private readonly Dictionary<NavMeshAgent, bool> originalAgents = new Dictionary<NavMeshAgent, bool>();
        private readonly Dictionary<Animator, bool> originalRootMotion = new Dictionary<Animator, bool>();
        private bool activeReplica;
        private EnemyScript[] scripts;
        private NavMeshAgent[] agents;
        private Animator[] animators;
        private Rigidbody rootBody;
        private bool originalKinematic;
        private bool originalGravity;
        private bool bodyCaptured;
        private bool releasedForDeath;
        private float nextComponentRefresh;

        public void Activate()
        {
            if (activeReplica) return;
            activeReplica = true;
            rootBody = GetComponent<Rigidbody>();
            if (rootBody != null)
            {
                bodyCaptured = true;
                originalKinematic = rootBody.isKinematic;
                originalGravity = rootBody.useGravity;
            }
            // Replica visuals must remain drawable even when the host owns all AI.
            // Some enemy controllers toggle renderers as part of local decision
            // states; the host snapshot owns the authoritative alive/dead state.
            var renderers = GetComponentsInChildren<Renderer>(true);
            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;
                renderer.enabled = true;
                var skinned = renderer as SkinnedMeshRenderer;
                if (skinned != null) skinned.updateWhenOffscreen = true;
            }
            SuppressControllers();
        }

        internal void ReleaseBodyForDeath()
        {
            if (releasedForDeath) return;
            releasedForDeath = true;
            RestoreBody();
        }

        private void Update()
        {
            var plugin = BloodlinkPlugin.Instance;
            if (!activeReplica || plugin == null || !plugin.IsConnected || plugin.IsHost)
            {
                RestoreControllers();
                Destroy(this);
                return;
            }
            var enemy = GetComponent<EnemyIdentifier>();
            if (enemy != null && enemy.dead) ReleaseBodyForDeath();
            if (!releasedForDeath) SuppressControllers();
        }

        private void SuppressControllers()
        {
            if (scripts == null || Time.unscaledTime >= nextComponentRefresh)
            {
                scripts = GetComponentsInChildren<EnemyScript>(true);
                agents = GetComponentsInChildren<NavMeshAgent>(true);
                animators = GetComponentsInChildren<Animator>(true);
                nextComponentRefresh = Time.unscaledTime + 1f;
            }
            foreach (var script in scripts)
            {
                if (script == null) continue;
                RememberAndDisable(script, originalBehaviours);
            }

            foreach (var agent in agents)
            {
                if (agent == null) continue;
                var wasEnabled = agent.enabled;
                if (!originalAgents.ContainsKey(agent)) originalAgents.Add(agent, wasEnabled);
                if (agent.enabled) agent.enabled = false;
            }

            foreach (var animator in animators)
            {
                if (animator == null) continue;
                if (!originalRootMotion.ContainsKey(animator)) originalRootMotion.Add(animator, animator.applyRootMotion);
                animator.applyRootMotion = false;
                if (animator.runtimeAnimatorController != null)
                {
                    animator.enabled = true;
                    animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                }
            }
            // A live replica receives its root pose from the host. Simulating a
            // dynamic rigidbody as well makes gravity/collisions fight each update.
            if (rootBody != null && !releasedForDeath)
            {
                if (!rootBody.isKinematic) rootBody.velocity = Vector3.zero;
                rootBody.isKinematic = true;
                rootBody.useGravity = false;
            }
        }

        private static void RememberAndDisable(Behaviour behaviour, Dictionary<Behaviour, bool> remembered)
        {
            if (!remembered.ContainsKey(behaviour)) remembered.Add(behaviour, behaviour.enabled);
            if (behaviour.enabled) behaviour.enabled = false;
        }

        private void RestoreControllers()
        {
            RestoreBody();
            foreach (var pair in originalBehaviours)
                if (pair.Key != null) pair.Key.enabled = pair.Value;
            foreach (var pair in originalAgents)
                if (pair.Key != null) pair.Key.enabled = pair.Value;
            foreach (var pair in originalRootMotion)
                if (pair.Key != null) pair.Key.applyRootMotion = pair.Value;
            originalBehaviours.Clear();
            originalAgents.Clear();
            originalRootMotion.Clear();
        }

        private void RestoreBody()
        {
            if (!bodyCaptured || rootBody == null) return;
            rootBody.isKinematic = originalKinematic;
            rootBody.useGravity = originalGravity;
        }

        private void OnDestroy()
        {
            RestoreControllers();
        }
    }
}
