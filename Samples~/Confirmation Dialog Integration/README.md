# Confirmation Dialog Integration

This sample provides Inspector-callable methods for a generated `ConfirmationDialog`. It logs outcomes only; replace the sample methods with the consuming game's own behavior.

1. Create one active `EventSystem` in the consuming scene and add the input module appropriate for that project's input handling.
2. Add the generated dialog Prefab to the scene. It starts inactive.
3. Add `ConfirmationDialogIntegrationExample` to an active scene object and assign its `Dialog` reference in the Inspector.
4. On the UI Button that should open the modal, add an `On Click()` entry for that scene object and choose `ConfirmationDialogIntegrationExample.OpenDialog`.
5. On the dialog's `ConfirmationDialog` component, add the same scene object to `On Confirmed` and select `OnConfirmed`; add it to `On Cancelled` and select `OnCancelled`.
6. In Play Mode, open the dialog. Navigate with the configured keyboard/gamepad UI actions and press Submit. Confirm and Cancel each close the modal and restore focus to the previously selected active Selectable.

The component does not create an EventSystem, bind game behavior in the generated Prefab, or map Escape to Cancel.
