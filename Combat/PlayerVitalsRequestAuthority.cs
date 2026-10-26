using System;
using System.Collections.Generic;
using HarmonyLib;
using ULTRAKILL.Enemy;
using ULTRAKILL.Bloodlink.Networking;
using UnityEngine;

namespace ULTRAKILL.Bloodlink.Combat
{
    /// <summary>
    /// Finds a live game-side source near the sender's authoritative snapshot before the host
    /// accepts client-reported health changes. Projectile sweeps and enemy explosions are
    /// applied directly by the host and do not use this request path.
    /// </summary>
    internal static class PlayerVitalsRequestAuthority
    {
        private const float PlayerProbeRadius = 0.72f;
        private static readonly HashSet<ulong> claimedSuperCharges = new HashSet<ulong>();

        public static int GetAuthorizedAmount(NetworkService network, PlayerSnapshot player, PlayerVitalsRequest request)
        {
            if (network == null || player == null || request == null) return 0;
            if (request.Action == PlayerVitalsAction.Damage)
            {
                PlayerSnapshot current;
                var health = network.Players.TryGetValue(player.PlayerId, out current) && current != null ? current.Health : player.Health;
                return FindDamageLimit(player.Position, health, request.Amount);
            }
            if (request.Action == PlayerVitalsAction.Heal) return FindHealLimit(player.PlayerId, player.Position, request.Amount);
            return 0;
        }

        public static void RecordAcceptedHeal(uint playerId, Vector3 position)
        {
            if (playerId == 0) return;
            var bonuses = UnityEngine.Object.FindObjectsOfType<Bonus>();
            for (var i = 0; i < bonuses.Length; i++)
            {
                var bonus = bonuses[i];
                if (bonus == null || !bonus.superCharge || !bonus.gameObject.activeInHierarchy ||
                    !TouchesPlayer(position, bonus.GetComponentsInChildren<Collider>(false))) continue;
                claimedSuperCharges.Add(ClaimKey(playerId, bonus.GetInstanceID()));
            }
        }

        public static void ForgetPlayer(uint playerId)
        {
            if (playerId == 0) return;
            claimedSuperCharges.RemoveWhere(key => (uint)(key >> 32) == playerId);
        }

        public static void Reset()
        {
            claimedSuperCharges.Clear();
        }

        private static int FindDamageLimit(Vector3 position, int currentHealth, int requested)
        {
            var maximum = 0;

            var hurtZones = UnityEngine.Object.FindObjectsOfType<HurtZone>();
            for (var i = 0; i < hurtZones.Length; i++)
            {
                var zone = hurtZones[i];
                if (zone == null || !zone.enabled || !zone.gameObject.activeInHierarchy ||
                    zone.affected == AffectedSubjects.EnemiesOnly || zone.setDamage <= 0f) continue;
                if (TouchesPlayer(position, zone.GetComponentsInChildren<Collider>(false)))
                {
                    var damage = zone.setDamage;
                    var difficulty = MonoSingleton<PrefsManager>.Instance != null
                        ? MonoSingleton<PrefsManager>.Instance.GetInt("difficulty") : 2;
                    if (difficulty < 2 && damage < 100f) damage *= difficulty == 1 ? 0.5f : 0.25f;
                    maximum = Math.Max(maximum, Mathf.Clamp(Mathf.FloorToInt(damage), 1, 200));
                }
            }

            // SwingCheck2 damage is resolved by EnemyMeleeSync on the host. Do not
            // also accept a client report for that swing and charge the hit twice.

            var fireZones = UnityEngine.Object.FindObjectsOfType<FireZone>();
            for (var i = 0; i < fireZones.Length; i++)
            {
                var zone = fireZones[i];
                if (zone == null || !zone.enabled || !zone.gameObject.activeInHierarchy || !zone.canHurtPlayer ||
                    zone.source == FlameSource.None || zone.playerDamage <= 0) continue;
                if (TouchesPlayer(position, zone.GetComponentsInChildren<Collider>(false)))
                {
                    var damage = zone.playerDamage;
                    if (zone.source == FlameSource.Streetcleaner)
                    {
                        var cleaner = zone.GetComponentInParent<Streetcleaner>();
                        if (cleaner == null || cleaner.eid == null || cleaner.eid.target == null || !cleaner.eid.target.isPlayer) continue;
                        damage = Mathf.FloorToInt(damage * cleaner.eid.totalDamageModifier);
                    }
                    maximum = Math.Max(maximum, Mathf.Clamp(damage, 1, 200));
                }
            }

            var deathZones = UnityEngine.Object.FindObjectsOfType<DeathZone>();
            for (var i = 0; i < deathZones.Length; i++)
            {
                var zone = deathZones[i];
                if (zone == null || !zone.enabled || !zone.gameObject.activeInHierarchy ||
                    zone.affected == AffectedSubjects.EnemiesOnly) continue;
                if (TouchesPlayer(position, zone.GetComponentsInChildren<Collider>(false)))
                    maximum = Math.Max(maximum, zone.notInstakill ? Mathf.Clamp(zone.damage, 1, 200) : 200);
            }

            var nails = UnityEngine.Object.FindObjectsOfType<Nail>();
            for (var i = 0; i < nails.Length; i++)
            {
                var nail = nails[i];
                if (nail == null || !nail.enabled || !nail.gameObject.activeInHierarchy || !nail.enemy || nail.hit) continue;
                var radius = Mathf.Clamp((nail.rb != null ? nail.rb.velocity.magnitude : 0f) * Time.fixedDeltaTime + 0.45f, 0.7f, 2f);
                if ((nail.transform.position - (position + Vector3.up * 0.9f)).sqrMagnitude <= radius * radius)
                    maximum = Math.Max(maximum, 8);
            }

            var massSpears = UnityEngine.Object.FindObjectsOfType<MassSpear>();
            for (var i = 0; i < massSpears.Length; i++)
            {
                var spear = massSpears[i];
                if (spear == null || !spear.enabled || !spear.gameObject.activeInHierarchy || spear.hitPlayer) continue;
                var targetsPlayer = spear.targetHandle != null && spear.targetHandle.target != null &&
                    spear.targetHandle.target.Type == TargetType.PLAYER;
                if (!targetsPlayer) continue;
                var body = spear.GetComponent<Rigidbody>();
                var velocity = body != null ? body.velocity : Vector3.zero;
                var center = position + Vector3.up * 0.9f;
                var radius = Mathf.Clamp(velocity.magnitude * Time.fixedDeltaTime + 0.65f, 0.8f, 2.5f);
                if (DistanceToSegmentSquared(center, spear.transform.position - velocity * Time.fixedDeltaTime, spear.transform.position) <= radius * radius)
                    maximum = Math.Max(maximum, Mathf.Clamp(Mathf.RoundToInt(25f * spear.damageMultiplier), 1, 200));
            }

            var blackHoles = UnityEngine.Object.FindObjectsOfType<BlackHoleProjectile>();
            for (var i = 0; i < blackHoles.Length; i++)
            {
                var hole = blackHoles[i];
                if (hole == null || !hole.enabled || !hole.gameObject.activeInHierarchy || !hole.enemy ||
                    hole.target == null || !hole.target.isPlayer) continue;
                var colliders = hole.GetComponentsInChildren<Collider>(false);
                var hasLiveCollider = false;
                for (var colliderIndex = 0; colliderIndex < colliders.Length; colliderIndex++)
                    if (colliders[colliderIndex] != null && colliders[colliderIndex].enabled) { hasLiveCollider = true; break; }
                if (!hasLiveCollider) continue;
                if (!TouchesPlayer(position, colliders)) continue;
                maximum = Math.Max(maximum, currentHealth > 10 ? Math.Max(1, currentHealth - 1) : 10);
            }

            var waters = UnityEngine.Object.FindObjectsOfType<Water>();
            var enemies = UnityEngine.Object.FindObjectsOfType<EnemyIdentifier>();
            for (var waterIndex = 0; waterIndex < waters.Length; waterIndex++)
            {
                var water = waters[waterIndex];
                if (water == null || !water.enabled || !water.gameObject.activeInHierarchy ||
                    !TouchesPlayer(position, water.GetComponentsInChildren<Collider>(false))) continue;
                for (var enemyIndex = 0; enemyIndex < enemies.Length; enemyIndex++)
                {
                    var enemy = enemies[enemyIndex];
                    if (enemy != null && !enemy.dead && enemy.gameObject.activeInHierarchy &&
                        enemy.touchingWaters != null && enemy.touchingWaters.Contains(water))
                    {
                        maximum = Math.Max(maximum, 50);
                        break;
                    }
                }
                if (maximum >= 50) break;
            }

            var wickedEnemies = UnityEngine.Object.FindObjectsOfType<Wicked>();
            for (var i = 0; i < wickedEnemies.Length; i++)
            {
                var wicked = wickedEnemies[i];
                if (wicked == null || !wicked.enabled || !wicked.gameObject.activeInHierarchy) continue;
                var identifier = wicked.GetComponent<EnemyIdentifier>();
                if (identifier == null || identifier.dead) continue;
                if (TouchesPlayer(position, wicked.GetComponentsInChildren<Collider>(false)))
                    maximum = Math.Max(maximum, 200);
            }

            var heatField = AccessTools.Field(typeof(HeatResistance), "heatResistance");
            var heatResistance = MonoSingleton<HeatResistance>.Instance;
            if (currentHealth > 100 && heatResistance != null && heatField != null &&
                heatField.GetValue(heatResistance) is float remainingHeat && remainingHeat <= 0f)
                maximum = Math.Max(maximum, currentHealth - 100);

            var continuousBeams = UnityEngine.Object.FindObjectsOfType<ContinuousBeam>();
            for (var i = 0; i < continuousBeams.Length; i++)
            {
                var beam = continuousBeams[i];
                if (beam == null || !beam.enabled || !beam.gameObject.activeInHierarchy || !beam.enemy ||
                    !beam.canHitPlayer || beam.off || beam.dying || beam.damage <= 0f) continue;
                var line = beam.GetComponent<LineRenderer>();
                if (line == null || !line.enabled || line.positionCount < 2) continue;
                var start = line.GetPosition(0);
                var end = line.GetPosition(line.positionCount - 1);
                if (DistanceToSegmentSquared(position + Vector3.up * 0.9f, start, end) <=
                    Mathf.Pow(Mathf.Clamp(beam.beamWidth + PlayerProbeRadius, 0.75f, 3f), 2f))
                    maximum = Math.Max(maximum, 200);
            }

            var beamguns = UnityEngine.Object.FindObjectsOfType<BeamgunBeam>();
            for (var i = 0; i < beamguns.Length; i++)
            {
                var beam = beamguns[i];
                if (beam == null || !beam.enabled || !beam.gameObject.activeInHierarchy || !beam.active || !beam.canHitPlayer) continue;
                var line = beam.GetComponent<LineRenderer>();
                if (line == null || !line.enabled || line.positionCount < 2) continue;
                if (DistanceToSegmentSquared(position + Vector3.up * 0.9f, line.GetPosition(0), line.GetPosition(line.positionCount - 1)) <=
                    Mathf.Pow(Mathf.Clamp(beam.beamWidth + PlayerProbeRadius, 0.75f, 3f), 2f))
                    maximum = Math.Max(maximum, 10);
            }

            var revolverBeams = UnityEngine.Object.FindObjectsOfType<RevolverBeam>();
            for (var i = 0; i < revolverBeams.Length; i++)
            {
                var beam = revolverBeams[i];
                if (beam == null || !beam.enabled || !beam.gameObject.activeInHierarchy || beam.beamType != BeamType.Enemy || beam.damage <= 0f) continue;
                var line = beam.GetComponent<LineRenderer>();
                if (line == null || !line.enabled || line.positionCount < 2) continue;
                if (DistanceToSegmentSquared(position + Vector3.up * 0.9f, line.GetPosition(0), line.GetPosition(line.positionCount - 1)) <=
                    Mathf.Pow(Mathf.Clamp(line.widthMultiplier + PlayerProbeRadius, 0.75f, 3f), 2f))
                    maximum = Math.Max(maximum, Mathf.Clamp(Mathf.RoundToInt(beam.damage * 10f), 1, 200));
            }

            var thrownSwords = UnityEngine.Object.FindObjectsOfType<ThrownSword>();
            for (var i = 0; i < thrownSwords.Length; i++)
            {
                var sword = thrownSwords[i];
                if (sword == null || !sword.enabled || !sword.gameObject.activeInHierarchy || !sword.active || sword.friendly || sword.thrownBy == null) continue;
                if ((sword.transform.position - (position + Vector3.up * 0.9f)).sqrMagnitude <= 1.25f * 1.25f)
                    maximum = Math.Max(maximum, 30);
            }

            var insignias = UnityEngine.Object.FindObjectsOfType<VirtueInsignia>();
            for (var i = 0; i < insignias.Length; i++)
            {
                var insignia = insignias[i];
                if (insignia == null || !insignia.enabled || !insignia.gameObject.activeInHierarchy ||
                    insignia.target == null || !insignia.target.isPlayer || insignia.damage <= 0) continue;
                var colliders = insignia.explosion != null
                    ? insignia.explosion.GetComponentsInChildren<Collider>(false)
                    : insignia.GetComponentsInChildren<Collider>(false);
                if (TouchesPlayer(position, colliders)) maximum = Math.Max(maximum, Mathf.Clamp(insignia.damage, 1, 200));
            }

            var shockwaves = UnityEngine.Object.FindObjectsOfType<PhysicalShockwave>();
            for (var i = 0; i < shockwaves.Length; i++)
            {
                var wave = shockwaves[i];
                if (wave == null || !wave.enabled || !wave.gameObject.activeInHierarchy || !wave.enemy || wave.fading ||
                    wave.hasHurtPlayer || wave.damage <= 0 || wave.target == null || !wave.target.isPlayer) continue;
                var colliders = wave.GetComponentsInChildren<Collider>(false);
                var overlaps = TouchesPlayer(position, colliders);
                var withinWaveRadius = (position - wave.transform.position).sqrMagnitude <=
                    Mathf.Pow(Mathf.Max(wave.maxSize, wave.transform.lossyScale.x), 2f);
                if (overlaps || withinWaveRadius)
                    maximum = Math.Max(maximum, Mathf.Clamp(wave.damage, 1, 200));
            }

            var attackTrails = UnityEngine.Object.FindObjectsOfType<AttackTrail>();
            for (var i = 0; i < attackTrails.Length; i++)
            {
                var trail = attackTrails[i];
                if (trail == null || !trail.enabled || !trail.gameObject.activeInHierarchy || trail.target == null || trail.pivot == null) continue;
                var start = trail.target.position;
                var end = trail.target.position + (trail.target.position - trail.pivot.position).normalized * Mathf.Max(1, trail.distance);
                if (DistanceToSegmentSquared(position + Vector3.up * 0.9f, start, end) <= 5.7f * 5.7f)
                    maximum = Math.Max(maximum, 30);
            }

            return requested > 0 && maximum > 0 ? Math.Min(requested, maximum) : 0;
        }

        private static int FindHealLimit(uint playerId, Vector3 position, int requested)
        {
            var maximum = 0;
            var blood = UnityEngine.Object.FindObjectsOfType<Bloodsplatter>();
            for (var i = 0; i < blood.Length; i++)
            {
                var splatter = blood[i];
                if (splatter == null || !splatter.enabled || !splatter.gameObject.activeInHierarchy || !splatter.ready) continue;
                var colliders = splatter.GetComponentsInChildren<Collider>(false);
                if (!TouchesPlayer(position, colliders)) continue;
                maximum = Math.Max(maximum, Mathf.Clamp(Math.Max(3, splatter.hpAmount), 1, 200));
            }

            var bonuses = UnityEngine.Object.FindObjectsOfType<Bonus>();
            for (var i = 0; i < bonuses.Length; i++)
            {
                var bonus = bonuses[i];
                if (bonus == null || !bonus.enabled || !bonus.gameObject.activeInHierarchy || !bonus.superCharge) continue;
                if (claimedSuperCharges.Contains(ClaimKey(playerId, bonus.GetInstanceID()))) continue;
                if (TouchesPlayer(position, bonus.GetComponentsInChildren<Collider>(false)))
                    maximum = Math.Max(maximum, 200);
            }

            return requested > 0 && maximum > 0 ? Math.Min(requested, maximum) : 0;
        }

        private static bool TouchesPlayer(Vector3 position, Collider collider)
        {
            if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy) return false;
            var samples = new[] { position + Vector3.up * 0.25f, position + Vector3.up * 0.9f, position + Vector3.up * 1.55f };
            for (var i = 0; i < samples.Length; i++)
                if ((collider.ClosestPoint(samples[i]) - samples[i]).sqrMagnitude <= PlayerProbeRadius * PlayerProbeRadius) return true;
            return false;
        }

        private static bool TouchesPlayer(Vector3 position, Collider[] colliders)
        {
            if (colliders == null) return false;
            for (var i = 0; i < colliders.Length; i++)
                if (TouchesPlayer(position, colliders[i])) return true;
            return false;
        }

        private static float DistanceToSegmentSquared(Vector3 point, Vector3 start, Vector3 end)
        {
            var segment = end - start;
            var lengthSquared = segment.sqrMagnitude;
            if (lengthSquared <= 0.000001f) return (point - start).sqrMagnitude;
            var t = Mathf.Clamp01(Vector3.Dot(point - start, segment) / lengthSquared);
            return (point - (start + segment * t)).sqrMagnitude;
        }

        private static ulong ClaimKey(uint playerId, int bonusInstanceId)
        {
            return ((ulong)playerId << 32) | (uint)bonusInstanceId;
        }
    }
}
