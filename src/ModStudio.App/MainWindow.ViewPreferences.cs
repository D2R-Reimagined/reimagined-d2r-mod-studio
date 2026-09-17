using ModStudio.Core;

namespace ModStudio.App;

/// <summary>Loads the view preferences shared by every editor pane (fonts, column letters) and writes them back when a pane changes them.</summary>
public partial class MainWindow
{
    private void InitializeViewPreferences()
    {
        if (!Program.Arguments.Contains("--smoke"))
            try { var prefs = StudioPreferences.Load(StudioPreferences.DefaultFile); EditorPane.ColumnLetters = prefs.ColumnLetters; ViewSettings.Load(prefs); } catch (Exception) { }
        EditorPane.ColumnLettersChanged += () => { RefreshRowEditor(Active); SaveViewPreferences(); };
        // Zoom steps arrive in bursts; the preferences file is written once the wheel has stopped.
        var saveTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); SaveViewPreferences(); };
        ViewSettings.Changed += () => { saveTimer.Stop(); saveTimer.Start(); };
        Closed += (_, _) => { if (saveTimer.IsEnabled) { saveTimer.Stop(); SaveViewPreferences(); } };
        ViewSettingsButton.Click += async (_, _) => { try { await ViewSettings.ShowDialogAsync(this); } catch (Exception ex) { ShowError(ex); } };
        // Where the colour-transform picker looks for an act palette before asking: the project, its deployment and the game's mods.
        EditorPane.PaletteRoots = () =>
        {
            if (project == null) return [];
            var roots = new List<string> { project.Root };
            try { var settings = RunSettings.Load(project, Profile); if (settings.DeploymentDirectory.Length > 0) { roots.Add(settings.DeploymentDirectory); roots.Add(System.IO.Path.Combine(settings.DeploymentDirectory, settings.ModName(project) + ".mpq")); } if (settings.InstallationDirectory.Length > 0) roots.Add(settings.InstallationDirectory); } catch (Exception) { }
            return roots;
        };
    }
    private void SaveViewPreferences()
    {
        if (Program.Arguments.Contains("--smoke")) return;
        try
        {
            var prefs = StudioPreferences.Load(StudioPreferences.DefaultFile);
            prefs.ColumnLetters = EditorPane.ColumnLetters; ViewSettings.Store(prefs);
            prefs.Save(StudioPreferences.DefaultFile);
        }
        catch (Exception ex) { ShowError(ex); }
    }
}
