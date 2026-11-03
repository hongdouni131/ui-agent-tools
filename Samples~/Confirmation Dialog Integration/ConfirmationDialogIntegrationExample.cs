using UIAgent.Tools;
using UnityEngine;

namespace UIAgent.Tools.Samples
{
    /// <summary>Inspector-friendly entry points for a consuming game's confirmation dialog.</summary>
    public sealed class ConfirmationDialogIntegrationExample : MonoBehaviour
    {
        [SerializeField] private ConfirmationDialog dialog;

        public void OpenDialog()
        {
            if (dialog == null || !dialog.Open())
                Debug.LogWarning("Confirmation dialog could not open. Check its references and EventSystem.", this);
        }

        public void OnConfirmed()
        {
            Debug.Log("Confirmation accepted. Replace this sample callback with the game's action.", this);
        }

        public void OnCancelled()
        {
            Debug.Log("Confirmation cancelled. Replace this sample callback with the game's action.", this);
        }
    }
}
