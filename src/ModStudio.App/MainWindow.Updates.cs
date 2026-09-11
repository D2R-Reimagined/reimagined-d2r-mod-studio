using System.Reflection;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Velopack;
using Velopack.Sources;

namespace ModStudio.App;

public partial class MainWindow
{
    private UpdateManager? studioUpdater;
    private UpdateInfo? studioUpdate;
    private bool updateBusy, updateDownloaded;

    private async void UpdateClicked(object? sender, RoutedEventArgs e)
    {
        if (updateBusy) return;
        if (studioUpdate == null) { await CheckStudioUpdateAsync(true); return; }
        updateBusy = true;
        UpdateButton.IsEnabled = false;
        try
        {
            if (!updateDownloaded)
            {
                UpdateButton.Content = "Downloading update…";
                await studioUpdater!.DownloadUpdatesAsync(studioUpdate, progress =>
                    Dispatcher.UIThread.Post(() => UpdateButton.Content = $"Downloading {progress}%"));
                updateDownloaded = true;
            }
            if (operation != null || controller.Running)
            {
                await ChooseAsync("Update ready", "Finish the current operation and stop the game before restarting Studio.", "OK");
                return;
            }
            if (await ChooseAsync("Restart Studio", $"Version {studioUpdate.TargetFullRelease.Version} is ready. Restart to install it?", "Restart", "Later") != "Restart") return;
            if (!await MayLeaveAsync()) return;
            if (operation != null || controller.Running) return;
            studioUpdater!.ApplyUpdatesAndRestart(studioUpdate);
        }
        catch (Exception ex) { ShowError(new Exception("Studio update failed: " + ex.Message, ex)); }
        finally
        {
            updateBusy = false;
            UpdateButton.IsEnabled = true;
            UpdateButton.Content = updateDownloaded ? "Restart to update" : "Download update";
        }
    }

    private async Task CheckStudioUpdateAsync(bool manual)
    {
        if (updateBusy || studioUpdate != null) return;
        updateBusy = true;
        UpdateButton.IsEnabled = false;
        UpdateButton.Content = "Checking updates…";
        try
        {
            var repository = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(a => a.Key == "UpdateRepositoryUrl").Value!;
            studioUpdater ??= new UpdateManager(new GithubSource(repository, accessToken: null, prerelease: false));
            if (!studioUpdater.IsInstalled)
            {
                if (manual) await ChooseAsync("Portable / development build",
                    "Automatic updates require the Velopack installation or AppImage. Download a release from " + repository + "/releases", "OK");
                return;
            }
            studioUpdate = await studioUpdater.CheckForUpdatesAsync();
            if (manual && studioUpdate == null) await ChooseAsync("Studio updates", "You are running the latest available version.", "OK");
        }
        catch (Exception ex)
        {
            if (manual) ShowError(new Exception("Could not check Studio updates: " + ex.Message, ex));
            else Log("Studio update check unavailable: " + ex.Message);
        }
        finally
        {
            updateBusy = false;
            UpdateButton.IsEnabled = true;
            UpdateButton.Content = studioUpdate == null ? "Check for updates" : $"Download {studioUpdate.TargetFullRelease.Version}";
        }
    }
}
