using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace ULTRAKILL.Bloodlink.UI
{
    [HarmonyPatch(typeof(MainMenuAgony), "Start")]
    internal static class MainMenuMultiplayerButtonPatch
    {
        private static void Postfix(MainMenuAgony __instance)
        {
            if (__instance.GetComponent<MainMenuMultiplayerButton>() == null)
            {
                __instance.gameObject.AddComponent<MainMenuMultiplayerButton>();
                Core.BloodlinkPlugin.Instance?.LogMainMenuInjected();
            }
        }
    }

    internal sealed class MainMenuMultiplayerButton : MonoBehaviour
    {
        internal static bool UiPresent { get; private set; }
        private GameObject canvasObject;

        private void Start()
        {
            canvasObject = new GameObject("Bloodlink Multiplayer Menu Button", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var menuOwner = GetComponent<MainMenuAgony>();
            var menuField = AccessTools.Field(typeof(MainMenuAgony), "mainMenu");
            var visibleMenu = menuField != null && menuOwner != null ? menuField.GetValue(menuOwner) as GameObject : null;
            canvasObject.transform.SetParent(visibleMenu != null ? visibleMenu.transform : transform, false);
            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 1000;
            canvas.overrideSorting = true;

            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            var buttonObject = new GameObject("Multiplayer", typeof(RectTransform), typeof(Image), typeof(Button));
            buttonObject.transform.SetParent(canvasObject.transform, false);
            var rect = buttonObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0, 0);
            rect.anchorMax = new Vector2(0, 0);
            rect.pivot = new Vector2(0, 0);
            rect.anchoredPosition = new Vector2(56, 48);
            rect.sizeDelta = new Vector2(300, 74);

            var image = buttonObject.GetComponent<Image>();
            image.color = new Color(0.12f, 0.035f, 0.045f, 0.98f);
            var button = buttonObject.GetComponent<Button>();
            button.targetGraphic = image;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            button.transition = Selectable.Transition.ColorTint;
            button.colors = new ColorBlock
            {
                normalColor = Color.white,
                highlightedColor = new Color(1f, 0.76f, 0.76f, 1f),
                pressedColor = new Color(0.78f, 0.52f, 0.52f, 1f),
                selectedColor = Color.white,
                disabledColor = new Color(0.45f, 0.45f, 0.45f, 0.6f),
                colorMultiplier = 1f,
                fadeDuration = 0.08f
            };
            var outline = buttonObject.AddComponent<Outline>();
            outline.effectColor = new Color(0.42f, 0.025f, 0.04f, 0.95f);
            outline.effectDistance = new Vector2(1.5f, -1.5f);
            button.onClick.AddListener(() => Core.BloodlinkPlugin.Instance?.OpenMultiplayerPanel());

            var accentObject = new GameObject("Accent", typeof(RectTransform), typeof(Image));
            accentObject.transform.SetParent(buttonObject.transform, false);
            var accentRect = accentObject.GetComponent<RectTransform>();
            accentRect.anchorMin = new Vector2(0, 0);
            accentRect.anchorMax = new Vector2(0, 1);
            accentRect.pivot = new Vector2(0, 0.5f);
            accentRect.anchoredPosition = Vector2.zero;
            accentRect.sizeDelta = new Vector2(5, 0);
            accentObject.GetComponent<Image>().color = new Color(0.9f, 0.045f, 0.065f, 1f);
            accentObject.GetComponent<Image>().raycastTarget = false;

            var title = CreateLabel(buttonObject.transform, "Title", "MULTIPLAYER", 25,
                FontStyle.Bold, TextAnchor.LowerLeft, new Vector2(28, 0.45f), new Vector2(-12, -8));
            var subtitle = CreateLabel(buttonObject.transform, "Subtitle", "HOST   /   JOIN   /   CO-OP", 12,
                FontStyle.Bold, TextAnchor.UpperLeft, new Vector2(28, 8), new Vector2(-12, -42));
            subtitle.color = new Color(0.84f, 0.72f, 0.72f, 1f);
            UiPresent = true;
        }

        private static Text CreateLabel(Transform parent, string objectName, string value, int fontSize,
            FontStyle fontStyle, TextAnchor alignment, Vector2 offsetMin, Vector2 offsetMax)
        {
            var labelObject = new GameObject(objectName, typeof(RectTransform), typeof(Text));
            labelObject.transform.SetParent(parent, false);
            var rect = labelObject.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
            var label = labelObject.GetComponent<Text>();
            label.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            if (label.font == null) label.font = Font.CreateDynamicFontFromOSFont("Arial", fontSize);
            label.text = value;
            label.fontSize = fontSize;
            label.fontStyle = fontStyle;
            label.alignment = alignment;
            label.color = Color.white;
            label.raycastTarget = false;
            return label;
        }

        private void OnEnable()
        {
            if (canvasObject != null) UiPresent = true;
        }

        private void OnDisable()
        {
            UiPresent = false;
        }

        private void OnDestroy()
        {
            UiPresent = false;
            if (canvasObject != null) Destroy(canvasObject);
        }
    }
}
