using System.Collections.Generic;
using System.Linq;
using ScenarioRuleLibrary;
using Script.GUI.SMNavigation.Utils;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace GloomhavenPartyAI
{
    internal static class AutomationToggleUi
    {
        private const string ControlName = "GloomhavenPartyAI.Toggle";
        private static readonly Color AutomatedColor = new Color(0.12f, 0.36f, 0.24f, 0.96f);
        private static readonly Color ManualColor = new Color(0.25f, 0.22f, 0.19f, 0.96f);
        private static readonly Color WaitingColor = new Color(0.48f, 0.30f, 0.09f, 0.96f);
        private static readonly Color BorderColor = new Color(0.78f, 0.63f, 0.34f, 1f);
        private static readonly HashSet<InitiativeTrackPlayerBehaviour> ExistingPlayers = new HashSet<InitiativeTrackPlayerBehaviour>();

        internal static void Refresh(InitiativeTrackPlayerBehaviour playerUi)
        {
            Refresh(playerUi, create: true);
        }

        // Poll only controls encountered by the existing initiative-track patch, including hidden ones.
        internal static void RefreshAll()
        {
            foreach (InitiativeTrackPlayerBehaviour playerUi in ExistingPlayers.ToArray())
            {
                if (playerUi == null) ExistingPlayers.Remove(playerUi);
                else Refresh(playerUi, create: false);
            }
        }

        private static void Refresh(InitiativeTrackPlayerBehaviour playerUi, bool create)
        {
            CPlayerActor actor = playerUi?.Actor as CPlayerActor;
            if (actor == null)
            {
                return;
            }

            Transform existing = playerUi.transform.Find(ControlName);
            if (existing == null && !create) return;
            GameObject control = existing == null ? Create(playerUi) : existing.gameObject;
            ExistingPlayers.Add(playerUi);
            bool visible = !FFSNetwork.IsOnline && !actor.IsDead;
            if (!visible && EventSystem.current != null &&
                EventSystem.current.currentSelectedGameObject == control)
            {
                GameObject fallback = playerUi.avatarButton.gameObject.activeInHierarchy
                    ? playerUi.avatarButton.gameObject
                    : null;
                EventSystem.current.SetSelectedGameObject(fallback);
            }
            control.SetActive(visible);
            if (!visible)
            {
                return;
            }

            Button button = control.GetComponent<Button>();
            TextMeshProUGUI label = control.GetComponentInChildren<TextMeshProUGUI>(includeInactive: true);
            bool automated = AutomationController.IsAutomated(actor);
            bool waiting = automated && AutomationController.NeedsInput(actor);
            Image image = control.GetComponent<Image>();
            image.color = waiting ? WaitingColor : automated ? AutomatedColor : ManualColor;
            label.text = waiting ? "AI WAIT" : automated ? "AI ON" : "AI OFF";
            label.color = automated ? Color.white : new Color(0.88f, 0.82f, 0.7f, 1f);
            button.interactable = AutomationController.CanToggleAutomation() && !actor.IsDead;

            if (!create) return;

            string actorGuid = actor.ActorGuid;
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(delegate
            {
                if (InteractabilityManager.ShouldTryPreventControl())
                {
                    return;
                }
                CPlayerActor currentActor = playerUi.Actor as CPlayerActor;
                if (currentActor == null || currentActor.ActorGuid != actorGuid)
                {
                    return;
                }
                AutomationController.ToggleAutomation(currentActor);
                Refresh(playerUi);
            });
        }

        internal static void EnableNavigation(InitiativeTrackActorBehaviour actorUi,
            Selectable left, Selectable right)
        {
            InitiativeTrackPlayerBehaviour playerUi = actorUi as InitiativeTrackPlayerBehaviour;
            if (playerUi == null)
            {
                return;
            }
            Transform controlTransform = playerUi.transform.Find(ControlName);
            Button button = controlTransform == null ? null : controlTransform.GetComponent<Button>();
            if (button == null || !button.gameObject.activeInHierarchy || !button.interactable)
            {
                return;
            }

            playerUi.avatarButton.SetNavigation(new NavigationCalculator
            {
                left = () => left,
                right = () => right,
                up = playerUi.Avatar.GetFirstBonus,
                down = () => button.gameObject.activeInHierarchy && button.interactable
                    ? button
                    : playerUi.Avatar.GetFirstBonus()
            });
            Navigation navigation = button.navigation;
            navigation.mode = Navigation.Mode.Explicit;
            navigation.selectOnLeft = left;
            navigation.selectOnRight = right;
            navigation.selectOnUp = playerUi.avatarButton;
            navigation.selectOnDown = playerUi.avatarButton;
            button.navigation = navigation;
        }

        internal static void DisableNavigation(InitiativeTrackActorBehaviour actorUi)
        {
            Transform controlTransform = actorUi?.transform.Find(ControlName);
            Button button = controlTransform == null ? null : controlTransform.GetComponent<Button>();
            if (button == null)
            {
                return;
            }
            Navigation navigation = button.navigation;
            navigation.mode = Navigation.Mode.None;
            button.navigation = navigation;
        }

        private static GameObject Create(InitiativeTrackPlayerBehaviour playerUi)
        {
            GameObject control = new GameObject(ControlName, typeof(RectTransform), typeof(CanvasRenderer),
                typeof(Image), typeof(Outline), typeof(Button));
            control.transform.SetParent(playerUi.transform, worldPositionStays: false);
            control.layer = playerUi.gameObject.layer;

            RectTransform rect = control.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(1f, 0f);
            rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = new Vector2(1f, 0f);
            rect.anchoredPosition = new Vector2(-4f, 4f);
            rect.sizeDelta = new Vector2(56f, 22f);

            Image image = control.GetComponent<Image>();
            image.color = ManualColor;

            Outline outline = control.GetComponent<Outline>();
            outline.effectColor = BorderColor;
            outline.effectDistance = new Vector2(1f, -1f);

            Button button = control.GetComponent<Button>();
            button.targetGraphic = image;
            Navigation navigation = button.navigation;
            navigation.mode = Navigation.Mode.None;
            button.navigation = navigation;
            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.14f, 1.14f, 1.14f, 1f);
            colors.pressedColor = new Color(0.75f, 0.75f, 0.75f, 1f);
            colors.selectedColor = colors.highlightedColor;
            colors.disabledColor = new Color(0.55f, 0.55f, 0.55f, 0.7f);
            colors.colorMultiplier = 1f;
            button.colors = colors;

            GameObject textObject = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer),
                typeof(TextMeshProUGUI));
            textObject.transform.SetParent(control.transform, worldPositionStays: false);
            textObject.layer = playerUi.gameObject.layer;
            RectTransform textRect = textObject.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(2f, 1f);
            textRect.offsetMax = new Vector2(-2f, -1f);

            TextMeshProUGUI label = textObject.GetComponent<TextMeshProUGUI>();
            TextMeshProUGUI source = playerUi.Avatar?.m_InitiativeText;
            if (source != null)
            {
                label.font = source.font;
            }
            label.text = "AI OFF";
            label.alignment = TextAlignmentOptions.Center;
            label.fontStyle = FontStyles.Bold;
            label.enableAutoSizing = true;
            label.fontSizeMin = 8f;
            label.fontSizeMax = 12f;
            label.raycastTarget = false;

            control.transform.SetAsLastSibling();
            return control;
        }
    }
}
