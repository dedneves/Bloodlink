using System;
using ULTRAKILL.Bloodlink.Enemies;
using ULTRAKILL.Bloodlink.Levels;
using ULTRAKILL.Bloodlink.Networking;
using UnityEngine;

internal static partial class Program
{
    private static void SynchronizationDomainRegressionChecks()
    {
        const string asset = "b3e7f2f8052488a45b35549efb98d902";
        Assert(LevelSceneReadinessPolicy.MatchesCurrentScene(asset, "Level 0-1", asset),
            "glass, items and scene object messages accept the physical Addressables scene identity");
        Assert(LevelSceneReadinessPolicy.MatchesCurrentScene("Level 0-1", "Level 0-1", asset),
            "logical campaign scene identity remains accepted");
        Assert(!LevelSceneReadinessPolicy.MatchesCurrentScene("Level 0-2", "Level 0-1", asset),
            "world events from another map are still rejected");
        Assert(!LevelSceneReadinessPolicy.MatchesCurrentScene(null, null, null),
            "missing scene identities are never accepted");
        foreach (var operation in new[] { SceneObjectOperation.SetActive, SceneObjectOperation.Destroy, SceneObjectOperation.Pose })
        {
            var state = new SceneObjectState { Sequence = 2, Revision = 4, OwnerId = 2,
                Operation = operation, Scene = asset, Path = "0:World/0:Glass", Position = new Vector3(1, 2, 3),
                Rotation = Quaternion.identity, Velocity = new Vector3(2, 0, 1), Active = false, Delay = 0.25f };
            var decoded = SceneObjectStateCodec.Decode(SceneObjectStateCodec.Encode(state));
            Assert(decoded.Operation == operation && decoded.Scene == asset && decoded.Revision == 4 && decoded.OwnerId == 2,
                "generic world state retains operation, scene, authority and ordering: " + operation);
            Assert(decoded.Active == false && decoded.Position.x == 1 && decoded.Delay == 0.25f,
                "generic world state retains activation, pose and delayed destruction: " + operation);
        }
        Assert(!PlayerPositionCorrectionPolicy.ShouldCorrect(0.01f * 0.01f),
            "centimeter-scale prediction noise does not correct the local player");
        Assert(PlayerPositionCorrectionPolicy.ShouldCorrect(1f), "meaningful authoritative drift still corrects the player");
        Assert(!PlayerPositionCorrectionPolicy.ShouldCorrect(81f), "an out-of-map correction cannot teleport the player");
        Assert(!PlayerPositionCorrectionPolicy.ShouldCorrect(float.NaN), "invalid corrections are rejected");
        Assert(PlayerPositionCorrectionPolicy.MaximumStep(1f / 60f) <= 0.201f,
            "normal reconciliation is spread across frames instead of applying a full teleport");
        Assert(PlayerPositionCorrectionPolicy.MaximumStep(1f) <= 0.601f,
            "a slow frame cannot multiply the position correction into a large jump");
        Assert(PlayerPositionCorrectionPolicy.MaximumStep(float.NaN) == 0,
            "invalid frame time cannot produce invalid position changes");
        var oneFrame = PlayerPositionCorrectionPolicy.RemoteBlend(1f / 30f);
        var halfFrame = PlayerPositionCorrectionPolicy.RemoteBlend(1f / 60f);
        Assert(Math.Abs(oneFrame - (1f - (1f - halfFrame) * (1f - halfFrame))) < 0.00001f,
            "remote smoothing has the same response at 30 and 60 FPS");

        var hits = new EnemyMeleeHitLedger();
        hits.BeginAttack(10);
        hits.BeginAttack(11);
        Assert(hits.TryAccept(10, 2, 1f, 30, false), "a host melee attack can damage the remote player");
        Assert(!hits.TryAccept(10, 2, 2f, 30, false), "a single-hit attack cannot repeatedly damage the same player");
        Assert(hits.TryAccept(10, 3, 1f, 30, false), "the same attack can independently hit another client");
        Assert(!hits.TryAccept(11, 2, 1.1f, 30, false), "simultaneous enemy swings respect hurt invulnerability");
        Assert(hits.TryAccept(11, 2, 1.51f, 60, false), "a later swing can damage the player after invulnerability ends");
        hits.BeginAttack(12);
        Assert(!hits.TryAccept(12, 2, 2f, 30, true), "a heavy hit retains its longer invulnerability window");
        Assert(hits.TryAccept(12, 2, 2.32f, 30, true), "a repeatable attack can hit after the heavy-hit window");
        Assert(!hits.TryAccept(12, 2, 2.4f, 30, true), "repeatable attacks are not applied every frame");
        Assert(hits.TryAccept(12, 2, 2.83f, 30, true), "repeatable attacks can hit again after their cooldown");
        hits.EndAttack(12);
        Assert(!hits.TryAccept(12, 2, 4f, 30, true), "stopped attacks cannot deal damage");
        hits.ForgetPlayer(2);
        hits.BeginAttack(13);
        Assert(hits.TryAccept(13, 2, 3f, 30, false), "a reconnect does not inherit stale attack cooldown state");
        hits.Reset();
        Assert(!hits.TryAccept(13, 2, 5f, 30, false), "level reset removes every active attack");
        Assert(typeof(SwingCheck2).GetMethod("DamageStart", Type.EmptyTypes) != null &&
            typeof(SwingCheck2).GetMethod("DamageStop", Type.EmptyTypes) != null,
            "melee authority hooks match the installed game's native attack lifecycle");
    }
}
