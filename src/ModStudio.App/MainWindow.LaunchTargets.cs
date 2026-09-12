using ModStudio.Core;

namespace ModStudio.App;

public partial class MainWindow
{
    private bool refreshingLaunchTargets;
    private void InitializeLaunchTargets()
    {
        LaunchTargetPicker.SelectionChanged += (_, _) => {
            if (refreshingLaunchTargets || project == null || LaunchTargetPicker.SelectedItem is not string target) return;
            try { var settings = RunSettings.Load(project, Profile); (settings with { LaunchTarget = target }).Save(project, Profile); }
            catch (Exception ex) { ShowError(ex); }
        };
    }
    /// <summary>First-run convenience: when the profile has no game folder yet, look in the usual Battle.net/Steam places and pre-fill the run settings.</summary>
    private async Task ApplyDetectedGameDefaultsAsync()
    {
        if (project == null || Program.Arguments.Contains("--smoke")) return;
        var current = project; var profile = Profile;
        try
        {
            var settings = RunSettings.Load(current, profile);
            if (!string.IsNullOrWhiteSpace(settings.InstallationDirectory)) return;
            var installations = await Task.Run(() => GameInstallDetector.Detect());
            if (project != current || Profile != profile || installations.Count == 0) return;
            var detected = settings.WithDetectedDefaults(current, installations);
            if (detected == settings) return;
            detected.Save(current, profile); RefreshLaunchTargets();
            Log($"Detected Diablo II: Resurrected at {installations[0].Directory} ({installations[0].Source}). Run settings for {profile} were pre-filled; open Run settings to change them.");
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private void RefreshLaunchTargets()
    {
        if (project == null) return;
        refreshingLaunchTargets = true;
        try
        {
            var settings = RunSettings.Load(project, Profile);
            var detected = RunSettings.DetectExecutables(settings.InstallationDirectory);
            LaunchTargetPicker.ItemsSource = detected;
            LaunchTargetPicker.SelectedItem = detected.Contains(settings.LaunchTarget) ? settings.LaunchTarget : null;
            LaunchTargetPicker.IsEnabled = detected.Length > 0;
        }
        catch (Exception ex) { ShowError(ex); }
        finally { refreshingLaunchTargets = false; }
    }
}
