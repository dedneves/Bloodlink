using System;
using System.IO;
using ULTRAKILL.Bloodlink.Combat;
using ULTRAKILL.Bloodlink.Networking;
using UnityEngine;

internal static partial class Program
{
    private static void SynchronizationRewriteChecks()
    {
        SynchronizationDomainRegressionChecks();
        var instanceMethods = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        Assert(typeof(ItemIdentifier).GetMethod("PickUp", instanceMethods, null, Type.EmptyTypes, null) != null &&
            typeof(ItemIdentifier).GetMethod("PutDown", instanceMethods, null, Type.EmptyTypes, null) != null,
            "pickup replication hooks match the installed game's actual private native callbacks");
        var altarCallback = typeof(ItemPlaceZone).GetMethod("CheckItem", instanceMethods);
        Assert(altarCallback != null && altarCallback.GetParameters().Length == 1 && altarCallback.GetParameters()[0].Name == "prelim",
            "altar authority patch matches the installed game's callback signature");
        Assert(typeof(EnemyIdentifier).GetMethod("DeliverDamage", instanceMethods).GetParameters().Length == 9,
            "native combat patch matches all installed game damage arguments");
        Assert(typeof(WeaponIdentifier).GetField("alternateVersion") != null,
            "alternate weapon state comes from the game's actual weapon identifier");
        var path = "0:World/0:Room/1:Wood%2FPanel/0:" + new string('x', 180);
        var progression = LevelProgressionEventCodec.Decode(LevelProgressionEventCodec.Encode(LevelProgressionEventKind.ArenaActivated, "Level 1-1", path, 0));
        Assert(progression.ObjectPath == path, "progression accepts the actual stable named identity, including paths longer than 128 bytes");
        var bonus = new SecretBonusLocation("Level 1-1", path, 0, 3);
        Assert(BonusPickupCodec.DecodeRelay(BonusPickupCodec.EncodeRelay(bonus)).ObjectPath == path,
            "secret collection accepts stable named paths instead of rejecting scene object identities");

        var mutation = new WorldMutation { Kind = WorldMutationKind.Breakable, Scene = "Level 1-1", Path = path,
            RequestId = 19, OwnerId = 2, Revision = 82, Position = new Vector3(1, 2, 3), Strength = 10, Broken = false, Durability = 7 };
        var outcome = WorldMutationCodec.Decode(WorldMutationCodec.Encode(mutation));
        Assert(outcome.RequestId == 19 && outcome.OwnerId == 2 && outcome.Revision == 82 && outcome.Durability == 7,
            "world outcomes retain request receipt, owner, revision and authoritative durability");
        mutation.Broken = true; mutation.Durability = 0;
        Assert(WorldMutationCodec.Decode(WorldMutationCodec.Encode(mutation)).Broken,
            "world break state is explicit and does not depend on replaying cumulative damage");

        var native = new DamageRequestData { RequestSequence = 1, EnemyId = 0, StableKey = 123, EnemyType = 2,
            EnemyPosition = new Vector3(4, 5, 6), ShooterPosition = Vector3.zero, ShotDirection = Vector3.forward,
            WeaponName = "Fist", Hitter = "heavypunch", TargetPath = path, Force = new Vector3(150000, 0, 0), HitPoint = new Vector3(4, 5, 6),
            Multiplier = 35, CritMultiplier = 1, IgnoreDamageMultiplier = true, HitterAttributes = new[] { 0, 1 } };
        var nativePayload = DamageRequestCodec.Encode(native);
        var hit = DamageRequestCodec.Decode(nativePayload);
        Assert(hit.EnemyId == 0 && hit.StableKey == 123, "client can address a native hit before the enemy's numeric mapping arrives");
        Assert(hit.Multiplier == 35 && hit.CritMultiplier == 1 && hit.Force.x == 150000 && hit.IgnoreDamageMultiplier,
            "native fist and support item hits preserve engine damage, critical and force values without weapon-list rejection");
        Assert(hit.TargetPath == path && hit.HitterAttributes.Length == 2, "native hit keeps full weak-point path and damage attributes");
        native.WeaponName = "SkullBlue (support item)"; native.Hitter = "support";
        Assert(DamageRequestCodec.Decode(DamageRequestCodec.Encode(native)).WeaponName == native.WeaponName,
            "support-item damage is not forced to impersonate the currently selected firearm");
        native.WeaponName = "Unarmed"; native.Hitter = "";
        Assert(DamageRequestCodec.Decode(DamageRequestCodec.Encode(native)).WeaponName == "Unarmed", "native weaponless damage remains relayable");
        native.Multiplier = float.NaN;
        var rejected = false; try { DamageRequestCodec.Encode(native); } catch (InvalidDataException) { rejected = true; }
        Assert(rejected, "native damage protocol still rejects non-finite damage");

        var damage = new EnemyDamageState { NetworkId = 2, Frame = 91, StableKey = 123, EnemyType = 2,
            Rotation = Quaternion.identity, Health = 0, Dead = true, HealthBefore = 30, ImpactPayload = nativePayload };
        var replicatedHit = EnemyDamageStateCodec.Decode(EnemyDamageStateCodec.Encode(damage));
        Assert(replicatedHit.HealthBefore == 30 && Equal(replicatedHit.ImpactPayload, nativePayload),
            "authoritative damage delivers the native impact needed to replay blood, reactions and death");

        var item = new ItemState { Sequence = 8, Revision = 17, ActorId = 2, OwnerId = 2, Action = ItemAction.PickUp,
            Scene = "Level 1-1", Path = path, SourcePath = "0:World/0:InfiniteSkulls", Position = new Vector3(1, 2, 3) };
        var held = ItemStateCodec.Decode(ItemStateCodec.Encode(item));
        Assert(held.ActorId == 2 && held.OwnerId == 2 && held.SourcePath == item.SourcePath, "item pickup carries its owner and infinite-source identity");
        item.Action = ItemAction.PutDown; item.OwnerId = 0; item.ParentPath = "0:World/0:BlueAltar";
        var placed = ItemStateCodec.Decode(ItemStateCodec.Encode(item));
        Assert(placed.OwnerId == 0 && placed.ActorId == 2 && placed.ParentPath == item.ParentPath,
            "placing an item preserves the acting client and altar so every peer can run its scenario events");

        foreach (var alternate in new[] { false, true })
        foreach (byte variation in new byte[] { 0, 1, 2 })
        {
            var player = new PlayerSnapshot { PlayerId = 2, WeaponSlot = 1, WeaponVariation = variation, WeaponAlternate = alternate };
            var state = PlayerSnapshot.Decode(player.Encode());
            Assert(state.WeaponVariation == variation && state.WeaponAlternate == alternate && player.Clone().WeaponAlternate == alternate,
                "firearm colour variation and alternate form are independent on the wire and in roster clones");
        }
        var fire = WeaponFireCodec.DecodeRelay(WeaponFireCodec.EncodeRelay(2, 1, 2, 0, Quaternion.identity, true));
        Assert(fire.OwnerPlayerId == 2 && fire.WeaponVariation == 2 && fire.WeaponAlternate,
            "firing immediately after a weapon switch carries the actual alternate model independently of the next movement snapshot");
    }
}
