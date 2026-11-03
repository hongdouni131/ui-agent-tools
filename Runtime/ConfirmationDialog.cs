using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace UIAgent.Tools
{
    /// <summary>Reusable modal lifecycle for a generated confirmation dialog Prefab.</summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("UI Agent/Confirmation Dialog")]
    public sealed class ConfirmationDialog : MonoBehaviour
    {
        [SerializeField] private Button backgroundBlocker;
        [SerializeField] private Button cancelButton;
        [SerializeField] private Button confirmButton;
        [SerializeField] private Selectable initialFocus;
        [SerializeField] private UnityEvent onCancelled = new UnityEvent();
        [SerializeField] private UnityEvent onConfirmed = new UnityEvent();

        private GameObject previousSelection;
        private Canvas dialogCanvas;
        private int previousSortingLayerId;
        private int previousSortingOrder;
        private bool previousOverrideSorting;
        private bool sortingCaptured;
        private Navigation previousCancelNavigation;
        private Navigation previousConfirmNavigation;
        private bool navigationCaptured;
        private EventSystem dialogEventSystem;

        public UnityEvent OnCancelled => onCancelled;
        public UnityEvent OnConfirmed => onConfirmed;
        public Button BackgroundBlocker => backgroundBlocker;
        public Button CancelButton => cancelButton;
        public Button ConfirmButton => confirmButton;
        public Selectable InitialFocus => initialFocus;

        private void OnEnable()
        {
            if (backgroundBlocker != null) backgroundBlocker.onClick.AddListener(ConsumeBackgroundClick);
            if (cancelButton != null) cancelButton.onClick.AddListener(Cancel);
            if (confirmButton != null) confirmButton.onClick.AddListener(Confirm);
        }

        private void OnDisable()
        {
            if (backgroundBlocker != null) backgroundBlocker.onClick.RemoveListener(ConsumeBackgroundClick);
            if (cancelButton != null) cancelButton.onClick.RemoveListener(Cancel);
            if (confirmButton != null) confirmButton.onClick.RemoveListener(Confirm);
            RestoreNavigation();
            RestoreCanvasSorting();
        }

        public bool Open() => Open(EventSystem.current);

        public bool Open(EventSystem eventSystem)
        {
            if (backgroundBlocker == null || cancelButton == null || confirmButton == null || initialFocus == null ||
                !backgroundBlocker.interactable || !cancelButton.interactable || !confirmButton.interactable || !initialFocus.IsInteractable())
                return false;

            if (!gameObject.activeSelf)
            {
                previousSelection = eventSystem != null && !IsInsideDialog(eventSystem.currentSelectedGameObject)
                    ? eventSystem.currentSelectedGameObject
                    : null;

                ConstrainNavigation();
                gameObject.SetActive(true);
            }
            dialogEventSystem = eventSystem;
            RaiseCanvasAboveExistingUi();
            if (eventSystem != null) eventSystem.SetSelectedGameObject(initialFocus.gameObject);
            return true;
        }

        public void Cancel()
        {
            onCancelled?.Invoke();
            Close();
        }

        public void Confirm()
        {
            onConfirmed?.Invoke();
            Close();
        }

        private void ConsumeBackgroundClick() { }

        private void ConstrainNavigation()
        {
            if (cancelButton == null || confirmButton == null) return;
            if (!navigationCaptured)
            {
                previousCancelNavigation = cancelButton.navigation;
                previousConfirmNavigation = confirmButton.navigation;
                navigationCaptured = true;
            }

            Navigation cancel = previousCancelNavigation;
            cancel.mode = Navigation.Mode.Explicit;
            cancel.selectOnLeft = confirmButton;
            cancel.selectOnRight = confirmButton;
            cancel.selectOnUp = cancelButton;
            cancel.selectOnDown = cancelButton;
            cancelButton.navigation = cancel;

            Navigation confirm = previousConfirmNavigation;
            confirm.mode = Navigation.Mode.Explicit;
            confirm.selectOnLeft = cancelButton;
            confirm.selectOnRight = cancelButton;
            confirm.selectOnUp = confirmButton;
            confirm.selectOnDown = confirmButton;
            confirmButton.navigation = confirm;
        }

        private void RestoreNavigation()
        {
            if (!navigationCaptured) return;
            if (cancelButton != null) cancelButton.navigation = previousCancelNavigation;
            if (confirmButton != null) confirmButton.navigation = previousConfirmNavigation;
            navigationCaptured = false;
        }

        private void Close()
        {
            GameObject restore = previousSelection;
            EventSystem eventSystem = dialogEventSystem != null ? dialogEventSystem : EventSystem.current;
            previousSelection = null;
            dialogEventSystem = null;
            RestoreNavigation();
            RestoreCanvasSorting();
            gameObject.SetActive(false);

            if (eventSystem == null) return;
            if (restore != null && restore.activeInHierarchy && IsSelectableInteractable(restore))
                eventSystem.SetSelectedGameObject(restore);
            else if (IsInsideDialog(eventSystem.currentSelectedGameObject))
                eventSystem.SetSelectedGameObject(null);
        }

        private void RaiseCanvasAboveExistingUi()
        {
            if (dialogCanvas == null) dialogCanvas = GetComponent<Canvas>();
            if (dialogCanvas == null) return;
            if (!sortingCaptured)
            {
                previousSortingLayerId = dialogCanvas.sortingLayerID;
                previousSortingOrder = dialogCanvas.sortingOrder;
                previousOverrideSorting = dialogCanvas.overrideSorting;
                sortingCaptured = true;
            }
            SortingLayer[] layers = SortingLayer.layers;
            if (layers.Length > 0) dialogCanvas.sortingLayerID = layers[layers.Length - 1].id;
            dialogCanvas.overrideSorting = true;
            dialogCanvas.sortingOrder = short.MaxValue;
        }

        private void RestoreCanvasSorting()
        {
            if (!sortingCaptured) return;
            if (dialogCanvas == null) dialogCanvas = GetComponent<Canvas>();
            if (dialogCanvas != null)
            {
                dialogCanvas.sortingLayerID = previousSortingLayerId;
                dialogCanvas.sortingOrder = previousSortingOrder;
                dialogCanvas.overrideSorting = previousOverrideSorting;
            }
            sortingCaptured = false;
        }

        private bool IsInsideDialog(GameObject candidate)
        {
            return candidate != null && candidate.transform.IsChildOf(transform);
        }

        private static bool IsSelectableInteractable(GameObject candidate)
        {
            Selectable selectable = candidate.GetComponent<Selectable>();
            return selectable != null && selectable.IsInteractable();
        }
    }
}
