using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using ULTRAKILL.Bloodlink.Core;
using ULTRAKILL.Bloodlink.Levels;
using ULTRAKILL.Bloodlink.Networking;
using UnityEngine;

namespace ULTRAKILL.Bloodlink.Combat
{
    internal sealed class WeaponPickupRequest
    {
        public uint Sequence;
        public uint PickupKey;
        public WeaponPickupKind Kind;
        public byte Slot;
        public byte Variation;
    }

    internal enum WeaponPickupKind : byte
    {
        Weapon = 1,
        Alternate = 2,
        Arm = 3
    }

    internal static class WeaponPickupCodec
    {
        private const int PayloadLength = 11;

        public static byte[] Encode(uint sequence, uint pickupKey, WeaponPickupKind kind, byte slot, byte variation)
        {
            if (sequence == 0 || pickupKey == 0 || !IsValidSlot(kind, slot) ||
                variation >= WeaponInventoryPolicy.VariationsPerSlot) throw new ArgumentOutOfRangeException();
            using (var stream = new MemoryStream(PayloadLength))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(sequence);
                writer.Write(pickupKey);
                writer.Write((byte)kind);
                writer.Write(slot);
                writer.Write(variation);
                return stream.ToArray();
            }
        }

        public static WeaponPickupRequest Decode(byte[] payload)
        {
            if (payload == null || payload.Length != PayloadLength) throw new InvalidDataException("Invalid weapon pickup payload length.");
            using (var stream = new MemoryStream(payload, false))
            using (var reader = new BinaryReader(stream))
            {
                var request = new WeaponPickupRequest
                {
                    Sequence = reader.ReadUInt32(),
                    PickupKey = reader.ReadUInt32(),
                    Kind = (WeaponPickupKind)reader.ReadByte(),
                    Slot = reader.ReadByte(),
                    Variation = reader.ReadByte()
                };
                if (request.Sequence == 0 || request.PickupKey == 0 || !IsValidSlot(request.Kind, request.Slot) ||
                    request.Variation >= WeaponInventoryPolicy.VariationsPerSlot ||
                    (request.Kind != WeaponPickupKind.Weapon && request.Variation != 0))
                    throw new InvalidDataException("Invalid weapon pickup values.");
                return request;
            }
        }

        private static bool IsValidSlot(WeaponPickupKind kind, byte slot)
        {
            if (kind == WeaponPickupKind.Weapon) return slot > 0 && slot <= WeaponInventoryPolicy.Slots;
            if (kind == WeaponPickupKind.Alternate) return slot > 0 && slot <= 5;
            if (kind == WeaponPickupKind.Arm) return slot <= 2;
            return false;
        }
    }

    internal sealed class WeaponPickupSync
    {
        private sealed class Descriptor { public WeaponPickupKind Kind; public byte Slot; public Vector3 Position; }
        private readonly Dictionary<uint, Descriptor> descriptors = new Dictionary<uint, Descriptor>();
        private readonly Dictionary<ulong, byte[]> grantReceipts = new Dictionary<ulong, byte[]>();

        public void CaptureScene()
        {
            descriptors.Clear();
            foreach (var pickup in Resources.FindObjectsOfTypeAll<WeaponPickUp>()) if (pickup != null && pickup.gameObject.scene.IsValid()) Remember(pickup);
            foreach (var pickup in Resources.FindObjectsOfTypeAll<AltPickUp>()) if (pickup != null && pickup.gameObject.scene.IsValid()) Remember(pickup);
        }
        private void Remember(WeaponPickUp pickup)
        {
            var key = GetStableKey(pickup); if (key == 0 || pickup.inventorySlot < 0 || pickup.inventorySlot > 5) return;
            descriptors[key] = new Descriptor { Kind = pickup.arm ? WeaponPickupKind.Arm : WeaponPickupKind.Weapon, Slot = (byte)pickup.inventorySlot, Position = pickup.transform.position };
        }
        private void Remember(AltPickUp pickup)
        {
            var key = GetStableKey(pickup); var slot = GetSlotFromPreference(pickup.pPref); if (key == 0 || slot == 0) return;
            descriptors[key] = new Descriptor { Kind = WeaponPickupKind.Alternate, Slot = slot, Position = pickup.transform.position };
        }
        private sealed class PendingRequest
        {
            public uint Sequence;
            public WeaponPickupKind Kind;
            public byte Slot;
            public byte Variation;
            public Component Pickup;
            public float RetryAt;
            public float GrantExpiresAt;
            public int Attempts;
        }

        private readonly Dictionary<uint, PendingRequest> pendingRequests = new Dictionary<uint, PendingRequest>();
        private readonly Dictionary<uint, byte[]> deferredGrants = new Dictionary<uint, byte[]>();
        private readonly HashSet<WeaponPickUp> authorizedLocalActivations = new HashSet<WeaponPickUp>();
        private readonly HashSet<AltPickUp> authorizedLocalAlternateActivations = new HashSet<AltPickUp>();
        private readonly ReliableEventSequenceTracker requestSequences = new ReliableEventSequenceTracker();
        private readonly DamageRequestRateLimiter requestRateLimiter = new DamageRequestRateLimiter(3f, 8f);
        private uint nextRequestSequence;

        public void Reset()
        {
            pendingRequests.Clear();
            deferredGrants.Clear();
            authorizedLocalActivations.Clear();
            authorizedLocalAlternateActivations.Clear();
            requestSequences.Clear();
            requestRateLimiter.Clear();
            nextRequestSequence = 0;
            grantReceipts.Clear();
        }

        public void RemovePlayer(uint playerId)
        {
            requestSequences.Remove(playerId);
            requestRateLimiter.Remove(playerId);
        }

        public bool ShouldActivate(NetworkService network, WeaponPickUp pickup)
        {
            if (pickup == null || network == null || !network.IsConnected) return true;
            if (network.IsHost) { Remember(pickup); return true; }
            if (authorizedLocalActivations.Remove(pickup)) return true;

            var key = GetStableKey(pickup);
            if (key == 0) return false;
            if (pendingRequests.ContainsKey(key)) return false;

            if (pickup.arm)
            {
                if (pickup.inventorySlot < 0 || pickup.inventorySlot > 2) return false;
                return SendRequest(network, key, WeaponPickupKind.Arm, (byte)pickup.inventorySlot, 0, pickup);
            }
            if (pickup.inventorySlot < 1 || pickup.inventorySlot > WeaponInventoryPolicy.Slots) return false;

            var slotIndex = pickup.inventorySlot - 1;
            var guns = MonoSingleton<GunControl>.Instance;
            if (guns == null || guns.slots == null || slotIndex < 0 || slotIndex >= guns.slots.Count) return false;
            var variations = guns.slots[slotIndex];
            if (variations == null) return false;
            var variation = -1;
            var prefabName = pickup.weapon != null ? pickup.weapon.name + "(Clone)" : string.Empty;
            for (var i = 0; i < variations.Count && i < WeaponInventoryPolicy.VariationsPerSlot; i++)
            {
                if (variations[i] != null && variations[i].name == prefabName) { variation = i; break; }
            }
            if (variation < 0) variation = variations.Count;
            if (variation < 0 || variation >= WeaponInventoryPolicy.VariationsPerSlot) return false;

            return SendRequest(network, key, WeaponPickupKind.Weapon, (byte)pickup.inventorySlot, (byte)variation, pickup);
        }

        public bool ShouldActivate(NetworkService network, AltPickUp pickup)
        {
            if (pickup == null || network == null || !network.IsConnected) return true;
            if (network.IsHost) { Remember(pickup); return true; }
            if (authorizedLocalAlternateActivations.Remove(pickup)) return true;
            var slot = GetSlotFromPreference(pickup.pPref);
            var key = GetStableKey(pickup);
            if (slot == 0 || key == 0 || pendingRequests.ContainsKey(key)) return false;
            return SendRequest(network, key, WeaponPickupKind.Alternate, slot, 0, pickup);
        }

        private bool SendRequest(NetworkService network, uint key, WeaponPickupKind kind, byte slot, byte variation, Component pickup)
        {
            unchecked { nextRequestSequence++; }
            if (nextRequestSequence == 0) nextRequestSequence = 1;
            var pending = new PendingRequest
            {
                Sequence = nextRequestSequence,
                Kind = kind,
                Slot = slot,
                Variation = variation,
                Pickup = pickup,
                RetryAt = Time.unscaledTime + 1.25f,
                Attempts = 1
            };
            pendingRequests[key] = pending;
            if (!network.SendToHost(MessageType.WeaponPickupRequest,
                WeaponPickupCodec.Encode(nextRequestSequence, key, kind, slot, variation)))
                pendingRequests.Remove(key);
            return false;
        }

        public void Update(NetworkService network)
        {
            if (network == null || !network.IsConnected || network.IsHost || pendingRequests.Count == 0) return;
            var now = Time.unscaledTime;
            var grantKeys = new List<uint>(deferredGrants.Keys);
            for (var i = 0; i < grantKeys.Count; i++)
            {
                PendingRequest waiting;
                if (!pendingRequests.TryGetValue(grantKeys[i], out waiting)) { deferredGrants.Remove(grantKeys[i]); continue; }
                if (now >= waiting.GrantExpiresAt)
                {
                    deferredGrants.Remove(grantKeys[i]);
                    pendingRequests.Remove(grantKeys[i]);
                    continue;
                }
                if (now >= waiting.RetryAt)
                {
                    waiting.RetryAt = now + 0.25f;
                    ReceiveGrant(deferredGrants[grantKeys[i]]);
                }
            }
            var due = new List<uint>();
            foreach (var pair in pendingRequests)
                if (!deferredGrants.ContainsKey(pair.Key) && now >= pair.Value.RetryAt) due.Add(pair.Key);

            for (var i = 0; i < due.Count; i++)
            {
                PendingRequest pending;
                var key = due[i];
                if (!pendingRequests.TryGetValue(key, out pending)) continue;
                if (pending.Attempts >= 8 || pending.Pickup == null)
                {
                    pendingRequests.Remove(key);
                    continue;
                }
                pending.Attempts++;
                pending.RetryAt = now + 1.25f;
                network.SendToHost(MessageType.WeaponPickupRequest,
                    WeaponPickupCodec.Encode(pending.Sequence, key, pending.Kind, pending.Slot, pending.Variation));
            }
        }

        public bool TryAuthorizeRequest(uint senderId, byte[] payload, NetworkService network, out byte[] grant)
        {
            grant = null;
            if (network == null || !network.IsHost || !network.Players.ContainsKey(senderId)) return false;
            var request = WeaponPickupCodec.Decode(payload);
            var receipt = ((ulong)senderId << 32) | request.Sequence;
            if (grantReceipts.TryGetValue(receipt, out grant)) return true;
            if (!requestSequences.TryAccept(senderId, request.Sequence) || !requestRateLimiter.TryAccept(senderId, Time.unscaledTime)) return false;
            var player = network.Players[senderId];
            if (player == null || (player.MovementFlags & 4) != 0) return false;
            Descriptor descriptor;
            if (!descriptors.TryGetValue(request.PickupKey, out descriptor))
            {
                if (request.Kind == WeaponPickupKind.Alternate)
                { var pickup = FindAlternatePickup(request.PickupKey); if (pickup != null) Remember(pickup); }
                else { var pickup = FindWeaponPickup(request.PickupKey); if (pickup != null) Remember(pickup); }
                if (!descriptors.TryGetValue(request.PickupKey, out descriptor)) return false;
            }
            if (descriptor.Kind != request.Kind || descriptor.Slot != request.Slot ||
                (descriptor.Position - player.Position).sqrMagnitude > 25f * 25f) return false;
            if (request.Kind == WeaponPickupKind.Weapon) network.AuthorizeWeaponUnlock(senderId, request.Slot, request.Variation);
            grant = WeaponPickupCodec.Encode(request.Sequence, request.PickupKey, request.Kind, request.Slot, request.Variation);
            grantReceipts[receipt] = grant;
            return true;
        }

        public void ReceiveGrant(byte[] payload)
        {
            var grant = WeaponPickupCodec.Decode(payload);
            PendingRequest expected;
            if (!pendingRequests.TryGetValue(grant.PickupKey, out expected) || expected.Sequence != grant.Sequence || expected.Kind != grant.Kind) return;

            Component pickup;
            MethodInfo activate;
            if (grant.Kind == WeaponPickupKind.Weapon || grant.Kind == WeaponPickupKind.Arm)
            {
                var weaponPickup = FindWeaponPickup(grant.PickupKey);
                if (weaponPickup == null || weaponPickup.arm != (grant.Kind == WeaponPickupKind.Arm) ||
                    weaponPickup.inventorySlot != grant.Slot)
                {
                    DeferGrant(grant.PickupKey, expected, payload);
                    return;
                }
                authorizedLocalActivations.Add(weaponPickup);
                pickup = weaponPickup;
                activate = AccessTools.Method(typeof(WeaponPickUp), "GotActivated");
            }
            else
            {
                var alternatePickup = FindAlternatePickup(grant.PickupKey);
                if (alternatePickup == null || GetSlotFromPreference(alternatePickup.pPref) != grant.Slot || grant.Variation != 0)
                {
                    DeferGrant(grant.PickupKey, expected, payload);
                    return;
                }
                authorizedLocalAlternateActivations.Add(alternatePickup);
                pickup = alternatePickup;
                activate = AccessTools.Method(typeof(AltPickUp), "GotActivated");
            }
            pendingRequests.Remove(grant.PickupKey);
            deferredGrants.Remove(grant.PickupKey);
            try
            {
                if (activate == null)
                {
                    if (pickup is WeaponPickUp) authorizedLocalActivations.Remove((WeaponPickUp)pickup);
                    else if (pickup is AltPickUp) authorizedLocalAlternateActivations.Remove((AltPickUp)pickup);
                    return;
                }
                activate.Invoke(pickup, null);
            }
            catch
            {
                if (pickup is WeaponPickUp) authorizedLocalActivations.Remove((WeaponPickUp)pickup);
                else if (pickup is AltPickUp) authorizedLocalAlternateActivations.Remove((AltPickUp)pickup);
            }
        }

        private void DeferGrant(uint key, PendingRequest expected, byte[] payload)
        {
            if (expected.GrantExpiresAt <= 0f) expected.GrantExpiresAt = Time.unscaledTime + 15f;
            expected.RetryAt = Time.unscaledTime + 0.25f;
            deferredGrants[key] = (byte[])payload.Clone();
        }

        private static WeaponPickUp FindWeaponPickup(uint key)
        {
            var pickups = Resources.FindObjectsOfTypeAll<WeaponPickUp>();
            for (var i = 0; i < pickups.Length; i++)
            {
                var pickup = pickups[i];
                if (pickup != null && pickup.gameObject.scene.IsValid() && GetStableKey(pickup) == key) return pickup;
            }
            return null;
        }

        private static AltPickUp FindAlternatePickup(uint key)
        {
            var pickups = Resources.FindObjectsOfTypeAll<AltPickUp>();
            for (var i = 0; i < pickups.Length; i++)
            {
                var pickup = pickups[i];
                if (pickup != null && pickup.gameObject.scene.IsValid() && GetStableKey(pickup) == key) return pickup;
            }
            return null;
        }

        private static byte GetSlotFromPreference(string preference)
        {
            if (string.IsNullOrEmpty(preference)) return 0;
            var value = preference.Trim().ToLowerInvariant();
            if (value.StartsWith("weapon.")) value = value.Substring(7);
            if (value.StartsWith("rev") || value.StartsWith("revolver")) return 1;
            if (value.StartsWith("sho") || value.StartsWith("shotgun")) return 2;
            if (value.StartsWith("nai") || value.StartsWith("nail")) return 3;
            if (value.StartsWith("rai") || value.StartsWith("rail")) return 4;
            if (value.StartsWith("rock") || value.StartsWith("rocket")) return 5;
            return 0;
        }

        private static uint GetStableKey(Component component)
        {
            return SceneObjectIdentity.GetStableKey(component);
        }
    }

    [HarmonyPatch(typeof(WeaponPickUp), "GotActivated")]
    internal static class WeaponPickupSyncPatch
    {
        private static bool Prefix(WeaponPickUp __instance)
        {
            return BloodlinkPlugin.Instance == null || BloodlinkPlugin.Instance.ShouldActivateWeaponPickup(__instance);
        }
    }

    [HarmonyPatch(typeof(AltPickUp), "GotActivated")]
    internal static class AlternatePickupSyncPatch
    {
        private static bool Prefix(AltPickUp __instance)
        {
            return BloodlinkPlugin.Instance == null || BloodlinkPlugin.Instance.ShouldActivateAlternatePickup(__instance);
        }
    }
}
