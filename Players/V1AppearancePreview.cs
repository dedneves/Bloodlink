using System;
using ULTRAKILL.Bloodlink.Networking;
using UnityEngine;

namespace ULTRAKILL.Bloodlink.Players
{
    internal sealed class V1AppearancePreview : IDisposable
    {
        private const int PreviewLayer = 29;
        private readonly RemotePlayerVisual visual;
        private readonly Transform modelRoot;
        private readonly Camera camera;
        private readonly RenderTexture texture;
        private readonly Light keyLight;
        private float yaw;
        private float distance = 4.1f;
        private uint tick;
        public bool AutoRotate = true;
        public void Rotate(float amount) { yaw = Mathf.Repeat(yaw + amount, 360f); AutoRotate = false; }
        public void Zoom(float amount) { distance = Mathf.Clamp(distance + amount, 2.5f, 6f); }
        public void ResetView() { yaw = 0f; distance = 4.1f; AutoRotate = true; }
        public void PlayAction(PlayerAnimationAction action) { visual?.PlayPlayerAnimation(new PlayerAnimationEvent { Action = action, Speed = 1f }); }

        public Texture Texture { get { return texture; } }
        public bool IsAvailable { get { return visual != null && modelRoot != null && camera != null; } }

        private V1AppearancePreview(RemotePlayerVisual visual, Transform modelRoot, Camera camera, RenderTexture texture, Light keyLight)
        {
            this.visual = visual;
            this.modelRoot = modelRoot;
            this.camera = camera;
            this.texture = texture;
            this.keyLight = keyLight;
        }

        public static V1AppearancePreview Create(PlayerAnimations template)
        {
            if (template == null) return null;
            var visual = RemotePlayerVisual.Create(template, uint.MaxValue, "APPEARANCE PREVIEW");
            visual.name = "Bloodlink V1 Appearance Preview";
            visual.transform.position = new Vector3(50000f, 50000f, 50000f);
            var model = visual.transform.Find("Remote V1 Runtime Model");
            if (model == null)
            {
                UnityEngine.Object.Destroy(visual.gameObject);
                return null;
            }

            foreach (var text in visual.GetComponentsInChildren<TextMesh>(true)) text.gameObject.SetActive(false);
            SetLayerRecursively(visual.transform, PreviewLayer);
            var cameraObject = new GameObject("Bloodlink Appearance Preview Camera");
            cameraObject.hideFlags = HideFlags.HideAndDontSave;
            SetLayerRecursively(cameraObject.transform, PreviewLayer);
            var camera = cameraObject.AddComponent<Camera>();
            camera.enabled = false;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.075f, 0.025f, 0.03f, 1f);
            camera.cullingMask = 1 << PreviewLayer;
            camera.fieldOfView = 34f;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 40f;
            camera.allowHDR = false;
            camera.allowMSAA = true;
            var texture = new RenderTexture(384, 384, 16, RenderTextureFormat.ARGB32)
            {
                name = "Bloodlink V1 Appearance Preview",
                antiAliasing = 2,
                hideFlags = HideFlags.HideAndDontSave
            };
            texture.Create();
            camera.targetTexture = texture;

            var lightObject = new GameObject("Bloodlink Appearance Preview Light");
            lightObject.hideFlags = HideFlags.HideAndDontSave;
            SetLayerRecursively(lightObject.transform, PreviewLayer);
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color(1f, 0.78f, 0.72f);
            light.intensity = 1.15f;
            light.cullingMask = 1 << PreviewLayer;
            lightObject.transform.rotation = Quaternion.Euler(28f, -32f, 0f);

            return new V1AppearancePreview(visual, model, camera, texture, light);
        }

        public void Render(uint primary, uint secondary, uint lights, byte glow, byte weaponSlot, byte weaponVariation)
        {
            if (visual == null || modelRoot == null || camera == null) return;
            if (AutoRotate) yaw = Mathf.Repeat(yaw + Time.unscaledDeltaTime * 18f, 360f);
            modelRoot.localRotation = Quaternion.Euler(0f, yaw, 0f);
            visual.Push(new PlayerSnapshot
            {
                PlayerId = uint.MaxValue,
                PlayerName = string.Empty,
                Position = visual.transform.position,
                Rotation = visual.transform.rotation,
                ViewRotation = Quaternion.identity,
                WeaponSlot = (byte)Mathf.Clamp(weaponSlot, 1, 5),
                WeaponVariation = weaponVariation,
                PrimaryColor = primary,
                SecondaryColor = secondary,
                LightColor = lights,
                GlowIntensity = glow,
                MovementFlags = 8,
                Tick = ++tick
            });
            var center = modelRoot.position + Vector3.up * 1f;
            camera.transform.position = center + new Vector3(0f, 0.16f, distance);
            camera.transform.rotation = Quaternion.LookRotation(center - camera.transform.position, Vector3.up);
            camera.Render();
        }

        public void Dispose()
        {
            if (camera != null) UnityEngine.Object.Destroy(camera.gameObject);
            if (keyLight != null) UnityEngine.Object.Destroy(keyLight.gameObject);
            if (texture != null)
            {
                texture.Release();
                UnityEngine.Object.Destroy(texture);
            }
            if (visual != null) UnityEngine.Object.Destroy(visual.gameObject);
        }

        private static void SetLayerRecursively(Transform root, int layer)
        {
            root.gameObject.layer = layer;
            for (var i = 0; i < root.childCount; i++) SetLayerRecursively(root.GetChild(i), layer);
        }
    }
}
