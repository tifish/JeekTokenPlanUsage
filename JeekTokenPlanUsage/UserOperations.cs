using JeekTokenPlanUsage.Resources;
using JeekTools;

namespace JeekTokenPlanUsage;

public sealed partial class TrayApplicationContext
{
    private McpOperationState? _operation;
    private ConfirmationForm? _confirmationForm;
    private bool UserOperationBusy => _operation?.Status is "checking" or "downloading" or "awaiting_user" or "installing";

    private void SetOperationStatus(string status, string message) =>
        _operation = _operation! with { Status = status, Message = message };

    private async Task<DialogResult> AskUserAsync(string title, string message, string yes, string no, string? cancel = null)
    {
        SetOperationStatus("awaiting_user", message);
        using var form = new ConfirmationForm(title, message, yes, no, cancel);
        _confirmationForm = form;
        form.Show();
        form.Activate();
        try { return await form.Completion; }
        finally { _confirmationForm = null; }
    }

    private void SwitchStorageMode(SettingsStorageMode mode, string? customRoot = null)
    {
        if (UserOperationBusy)
        {
            _confirmationForm?.Activate();
            return;
        }
        _ = SwitchStorageModeAsync(mode, customRoot);
    }

    private async Task SwitchStorageModeAsync(SettingsStorageMode mode, string? customRoot)
    {
        _operation = new(Guid.NewGuid().ToString("N"), "set_storage", "checking", "Preparing storage change.");
        try
        {
            string oldRoot = Path.GetFullPath(_settings.RoamingConfigDirectory);
            string newRoot = Path.GetFullPath(_settings.PreviewConfigRoot(mode, customRoot));
            if (mode == SettingsStorageMode.Custom && string.IsNullOrWhiteSpace(customRoot ?? _settings.CustomStorageRoot))
                throw new ArgumentException("A custom storage directory is required.");
            if (string.Equals(oldRoot, newRoot, StringComparison.OrdinalIgnoreCase))
            {
                SetOperationStatus("completed", "Storage location is unchanged.");
                return;
            }
            DialogResult choice = await AskUserAsync(Strings.Storage_MoveConfigTitle,
                string.Format(Strings.Storage_MoveConfigFormat, oldRoot, newRoot),
                Strings.Action_Move, Strings.Action_UseLocation, Strings.Proxy_Cancel);
            if (_disposed || choice == DialogResult.Cancel)
            {
                SetOperationStatus("cancelled", "Storage change cancelled.");
                return;
            }
            bool moveFiles = choice == DialogResult.Yes;
            if (!moveFiles && _settings.StorageMode == SettingsStorageMode.Portable && mode != SettingsStorageMode.Portable)
            {
                choice = await AskUserAsync(Strings.Storage_MoveConfigTitle, Strings.Storage_PortableMustMove,
                    Strings.Action_Move, Strings.Proxy_Cancel);
                if (_disposed || choice != DialogResult.Yes)
                {
                    SetOperationStatus("cancelled", "Storage change cancelled.");
                    return;
                }
                moveFiles = true;
            }
            string previousLanguage = _settings.Language;
            _settings.SwitchStorageMode(mode, customRoot, moveFiles);
            StartSettingsWatcher();
            ApplySettingsFromDisk(previousLanguage);
            UpdateStorageMenuChecks();
            SetOperationStatus("completed", $"Storage set to {FormatStorageMode(_settings.StorageMode)}.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Storage change failed: {ex.Message}");
            SetOperationStatus("failed", ex.Message);
            if (!_disposed)
                ShowUpdateToast(Strings.Storage_MoveConfigTitle, string.Format(Strings.Storage_SwitchFailedFormat, ex.Message));
        }
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_disposed || _updateInProgress || UserOperationBusy || (!manual && _paused))
        {
            if (manual) _confirmationForm?.Activate();
            return;
        }
        _updateInProgress = true;
        _operation = new(Guid.NewGuid().ToString("N"), "check_update", "checking", "Checking for updates.");
        try
        {
            UpdateCheckOutcome outcome = await AutoUpdate.HasUpdateAsync();
            if (_disposed) return;
            if (outcome == UpdateCheckOutcome.Available)
            {
                SetOperationStatus("downloading", "Preparing the update package.");
                string? package = await AutoUpdate.DownloadAsync(_settings.DisableMirrorDownload);
                if (_disposed) return;
                if (package is null) throw new InvalidOperationException(AutoUpdate.FailureReason);
                await ConfirmUpdateInstallAsync(package);
            }
            else if (outcome == UpdateCheckOutcome.Failed)
                throw new InvalidOperationException(AutoUpdate.FailureReason);
            else
            {
                SetOperationStatus("completed", "No update is available (or updates are disabled for this build).");
                if (manual) ShowUpdateToast(Strings.Update_NoneTitle, Strings.Update_NoneBody);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Update failed: {ex.Message}");
            SetOperationStatus("failed", ex.Message);
            if (!_disposed) ShowUpdateToast(Strings.Update_NoneTitle, string.Format(Strings.Update_FailedFormat, ex.Message));
        }
        finally { _updateInProgress = false; }
    }

    private async Task ConfirmUpdateInstallAsync(string package)
    {
        DialogResult choice = await AskUserAsync(Strings.Update_FoundTitle,
            Strings.Update_Ready, Strings.Update_Install, Strings.Update_Later);
        if (_disposed || choice != DialogResult.Yes)
        {
            // Keep the package and version file for the next check, including across restarts.
            SetOperationStatus("postponed", "Update retained for later installation.");
            return;
        }
        SetOperationStatus("installing", "Saving settings and starting the updater.");
        if (!_settings.Save())
            throw new IOException("Could not save settings; update installation cancelled.");
        if (!AutoUpdate.LaunchInstall(package))
            throw new InvalidOperationException(AutoUpdate.FailureReason);
        Application.Exit();
    }

#if DEBUG
    // Drive the real confirmation UI without networking or launching an updater.
    // Debug AutoUpdater refuses installation even when Confirm is pressed.
    private void PreviewUpdateConfirmation()
    {
        if (UserOperationBusy) throw new InvalidOperationException("An operation is already active.");
        _operation = new(Guid.NewGuid().ToString("N"), "check_update", "checking", "Debug confirmation preview.");
        _ = PreviewAsync();
        async Task PreviewAsync()
        {
            try { await ConfirmUpdateInstallAsync(Path.Combine(AutoUpdate.UpdateRoot, "package")); }
            catch (Exception ex) { SetOperationStatus("failed", ex.Message); }
        }
    }
#endif
}
