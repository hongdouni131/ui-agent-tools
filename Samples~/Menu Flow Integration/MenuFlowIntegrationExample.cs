using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.Events;

namespace UIAgent.Tools.Samples
{
    /// <summary>Consumer-owned page flow; generated Prefabs remain generic.</summary>
    public sealed class MenuFlowIntegrationExample : MonoBehaviour
    {
        [SerializeField] private GameObject menuPage;
        [SerializeField] private GameObject settingsPage;
        [SerializeField] private Button settingsButton;
        [SerializeField] private Button backButton;
        [SerializeField] private Button actionButton;
        [SerializeField] private Button signalButton;
        [SerializeField] private Selectable settingsFocus;
        [SerializeField] private ConfirmationDialog dialog;
        [SerializeField] private TMP_Text statusLabel;
        [SerializeField] private EventSystem eventSystem;
        [SerializeField] private UnityEvent onTestSignal = new UnityEvent();
        public UnityEvent OnTestSignal => onTestSignal;
        public int ConfirmCount { get; private set; }
        public int CancelCount { get; private set; }
        public int SignalCount { get; private set; }

        private void OnEnable()
        {
            if (!ValidateReferences()) { enabled = false; return; }
            settingsButton.onClick.AddListener(OpenSettings);
            backButton.onClick.AddListener(ReturnToMenu);
            actionButton.onClick.AddListener(OpenConfirmation);
            signalButton.onClick.AddListener(TestSignal);
            dialog.OnConfirmed.AddListener(ShowConfirmed);
            dialog.OnCancelled.AddListener(ShowCancelled);
        }

        private void Start() => ReturnToMenu();

        private void OnDisable()
        {
            if (settingsButton != null) settingsButton.onClick.RemoveListener(OpenSettings);
            if (backButton != null) backButton.onClick.RemoveListener(ReturnToMenu);
            if (actionButton != null) actionButton.onClick.RemoveListener(OpenConfirmation);
            if (signalButton != null) signalButton.onClick.RemoveListener(TestSignal);
            if (dialog != null)
            {
                dialog.OnConfirmed.RemoveListener(ShowConfirmed);
                dialog.OnCancelled.RemoveListener(ShowCancelled);
            }
        }

        public void OpenSettings()
        {
            if (dialog.gameObject.activeSelf) return;
            eventSystem.SetSelectedGameObject(null);
            menuPage.SetActive(false);
            settingsPage.SetActive(true);
            eventSystem.SetSelectedGameObject(settingsFocus.gameObject);
        }

        public void ReturnToMenu()
        {
            if (dialog.gameObject.activeSelf) return;
            eventSystem.SetSelectedGameObject(null);
            settingsPage.SetActive(false);
            menuPage.SetActive(true);
            eventSystem.SetSelectedGameObject(settingsButton.gameObject);
        }

        public void OpenConfirmation()
        {
            if (!menuPage.activeInHierarchy || dialog.gameObject.activeSelf) return;
            eventSystem.SetSelectedGameObject(actionButton.gameObject);
            if (!dialog.Open(eventSystem)) Debug.LogError("Menu flow: dialog could not open.", this);
        }

        private void TestSignal()
        {
            SignalCount++;
            onTestSignal.Invoke();
            statusLabel.text = "TEST SIGNAL RECEIVED / " + SignalCount;
        }
        private void ShowConfirmed()
        {
            ConfirmCount++;
            statusLabel.text = "DEMO CONFIRMED / " + ConfirmCount;
        }
        private void ShowCancelled()
        {
            CancelCount++;
            statusLabel.text = "DEMO CANCELLED / " + CancelCount;
        }
        private bool ValidateReferences()
        {
            bool valid = menuPage != null && settingsPage != null && settingsButton != null &&
                backButton != null && actionButton != null && signalButton != null &&
                settingsFocus != null && dialog != null && statusLabel != null && eventSystem != null;
            if (!valid) Debug.LogError("Menu flow: assign all pages, buttons, focus, dialog, status and EventSystem references.", this);
            return valid;
        }
    }
}
