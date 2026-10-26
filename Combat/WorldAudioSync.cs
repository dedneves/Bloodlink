using System;
using System.Collections.Generic;
using ULTRAKILL.Bloodlink.Networking;
using ULTRAKILL.Bloodlink.Players;
using UnityEngine;

namespace ULTRAKILL.Bloodlink.Combat
{
    internal sealed class WorldAudioSync
    {
        private const float MaximumClipDuration = 8f;
        private const float MaximumSourceDistanceFromPlayer = 180f;
        private readonly Dictionary<int, int> emittedFrameBySourceAndClip = new Dictionary<int, int>();
        private readonly Dictionary<string, AudioClip> clipCache = new Dictionary<string, AudioClip>(StringComparer.Ordinal);
        private readonly Dictionary<uint, float> recentSoundFingerprints = new Dictionary<uint, float>();
        private readonly WeaponFireRateLimiter eventRateLimiter = new WeaponFireRateLimiter(24f, 48f);

        public void Reset()
        {
            emittedFrameBySourceAndClip.Clear();
            recentSoundFingerprints.Clear();
            eventRateLimiter.Clear();
        }

        public bool ShouldSendLocal(AudioSource source, AudioClip clip, float volumeScale, out byte[] payload, uint ownerPlayerId)
        {
            payload = null;
            if (IsApplyingRemote || source == null || clip == null || ownerPlayerId == 0 ||
                source.spatialBlend < 0.05f || source.loop || clip.length <= 0f || clip.length > MaximumClipDuration ||
                !IsFinite(volumeScale) || volumeScale <= 0f || source.volume * volumeScale < 0.01f ||
                source.volume * volumeScale > 2f || source.pitch < 0.1f || source.pitch > 3f ||
                source.GetComponentInParent<PlayerAnimations>() != null ||
                source.GetComponentInParent<NewMovement>() != null ||
                source.GetComponentInParent<RemotePlayerVisual>() != null)
                return false;

            var key = unchecked(source.GetInstanceID() * 397 ^ clip.GetInstanceID());
            var frame = Time.frameCount;
            int lastFrame;
            if (emittedFrameBySourceAndClip.TryGetValue(key, out lastFrame) && lastFrame == frame) return false;
            if (!eventRateLimiter.TryAccept(ownerPlayerId, Time.unscaledTime)) return false;
            emittedFrameBySourceAndClip[key] = frame;
            if (emittedFrameBySourceAndClip.Count > 1024) emittedFrameBySourceAndClip.Clear();

            try
            {
                var sound = new WorldAudioEvent
                {
                    OwnerPlayerId = ownerPlayerId,
                    ClipName = clip.name,
                    Position = source.transform.position,
                    Volume = source.volume * volumeScale,
                    Pitch = source.pitch,
                    SpatialBlend = source.spatialBlend
                };
                payload = WorldAudioCodec.Encode(sound);
                RememberFingerprint(sound, Time.unscaledTime);
                return true;
            }
            catch { payload = null; return false; }
        }

        public bool TryRelay(uint senderId, byte[] payload, NetworkService network, float now)
        {
            if (network == null || !network.IsHost || senderId == 0 ||
                !eventRateLimiter.TryAccept(senderId, now)) return false;
            var sound = WorldAudioCodec.Decode(payload);
            if (sound.OwnerPlayerId != senderId) return false;
            var fingerprint = GetFingerprint(sound);
            float lastHeard;
            if (recentSoundFingerprints.TryGetValue(fingerprint, out lastHeard) && now - lastHeard < 0.18f) return false;
            RememberFingerprint(sound, now);
            PlayerSnapshot sender;
            if (!network.Players.TryGetValue(senderId, out sender) || sender == null ||
                (sound.Position - sender.Position).sqrMagnitude > MaximumSourceDistanceFromPlayer * MaximumSourceDistanceFromPlayer)
                return false;
            network.BroadcastUnreliable(MessageType.WorldAudio, payload);
            return true;
        }

        public void RemovePlayer(uint playerId) { eventRateLimiter.Remove(playerId); }

        public void PlayRemote(byte[] payload, uint localPlayerId)
        {
            var sound = WorldAudioCodec.Decode(payload);
            if (sound.OwnerPlayerId == localPlayerId) return;
            var now = Time.unscaledTime;
            var fingerprint = GetFingerprint(sound);
            float lastHeard;
            if (recentSoundFingerprints.TryGetValue(fingerprint, out lastHeard) && now - lastHeard < 0.3f) return;
            RememberFingerprint(sound, now);
            var clip = ResolveClip(sound.ClipName);
            if (clip == null) return;

            var gameObject = new GameObject("Bloodlink Remote Audio - " + sound.ClipName);
            gameObject.transform.position = sound.Position;
            var source = gameObject.AddComponent<AudioSource>();
            source.clip = clip;
            source.volume = sound.Volume;
            source.pitch = sound.Pitch;
            source.spatialBlend = sound.SpatialBlend;
            source.rolloffMode = AudioRolloffMode.Logarithmic;
            source.minDistance = 1f;
            source.maxDistance = 65f;
            IsApplyingRemote = true;
            try { source.Play(); }
            finally { IsApplyingRemote = false; }
            UnityEngine.Object.Destroy(gameObject, Mathf.Max(0.1f, clip.length / Mathf.Max(0.1f, Mathf.Abs(sound.Pitch)) + 0.15f));
        }

        private AudioClip ResolveClip(string name)
        {
            AudioClip clip;
            if (clipCache.TryGetValue(name, out clip)) return clip;
            var clips = Resources.FindObjectsOfTypeAll<AudioClip>();
            for (var i = 0; i < clips.Length; i++)
            {
                if (clips[i] == null || !string.Equals(clips[i].name, name, StringComparison.Ordinal)) continue;
                clipCache[name] = clips[i];
                return clips[i];
            }
            clipCache[name] = null;
            if (clipCache.Count > 512) clipCache.Clear();
            return null;
        }

        private void RememberFingerprint(WorldAudioEvent sound, float now)
        {
            recentSoundFingerprints[GetFingerprint(sound)] = now;
            if (recentSoundFingerprints.Count > 512) recentSoundFingerprints.Clear();
        }

        private static uint GetFingerprint(WorldAudioEvent sound)
        {
            unchecked
            {
                uint hash = 2166136261;
                var name = sound.ClipName ?? string.Empty;
                for (var i = 0; i < name.Length; i++) { hash ^= name[i]; hash *= 16777619; }
                hash ^= (uint)Mathf.RoundToInt(sound.Position.x * 2f); hash *= 16777619;
                hash ^= (uint)Mathf.RoundToInt(sound.Position.y * 2f); hash *= 16777619;
                hash ^= (uint)Mathf.RoundToInt(sound.Position.z * 2f); hash *= 16777619;
                return hash;
            }
        }

        internal static bool IsApplyingRemote { get; private set; }

        private static bool IsFinite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
    }
}
