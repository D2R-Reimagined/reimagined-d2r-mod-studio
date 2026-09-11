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
