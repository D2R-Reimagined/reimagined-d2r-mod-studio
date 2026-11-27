using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using ModStudio.Core;
using static ModStudio.Core.Storage;

namespace ModStudio.App;

/// <summary>Profile management from the toolbar, and keeping every table pane's profile view in step with the profile files.</summary>
public partial class MainWindow
{
    private DispatcherTimer? profileFilesTimer;
    private readonly Dictionary<string, string?> profileColorSettings = new(StringComparer.Ordinal);

    private void AttachProfiles(EditorPane pane)
    {
        pane.ProfileProject = () => project;
        pane.ProfileWritten += (sender, write) =>
        {
            buildDiagnostics.Clear(); RefreshStatus();
            Status.Text = $"{write.Profile}: wrote {string.Join(", ", write.Files.Select(f => System.IO.Path.GetFileName(f.Path)).Distinct())}";
            RefreshProfileDependents(except: sender);
        };
        pane.OpenFileRequested += async file => { try { await OpenDocumentAsync(file); } catch (Exception e) { ShowError(e); } };
        pane.ProfileLensChanged += sender =>
        {
            if (Active != sender) return;
            UpdateInspector(sender);
            Status.Text = sender.ProfileLens is { } lens ? $"Viewing {sender.Document.Table?.Name} as {lens}: edits write compatibility/{lens}/ overrides, not shared source." : "Viewing shared source.";
        };
        pane.ProfilesChanged();
    }

    /// <summary>Profile files changed on disk (Studio's own writes included): previews and every pane's profile view catch up once things settle.</summary>
    private void QueueProfileFilesChanged()
    {
        profileFilesTimer ??= CreateProfileFilesTimer();
        profileFilesTimer.Stop(); profileFilesTimer.Start();
    }
    private DispatcherTimer CreateProfileFilesTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        timer.Tick += (_, _) => { timer.Stop(); RefreshProfileColors(); foreach (var pane in tabs.Select(t => t.Content).OfType<EditorPane>()) pane.ProfilesChanged(); };
        return timer;
    }
    private void RefreshProfileDependents(EditorPane? except = null)
    {
        _ = RefreshSemanticInspectorAsync();
        RefreshItemPreview(); RefreshSkillPreview(); RefreshMissilePreview(); RefreshStatPreview(); RefreshDropPreview(); RefreshMonsterPreview(); RefreshAffixPreview(); RefreshRecipePreview();
        foreach (var pane in tabs.Select(t => t.Content).OfType<EditorPane>()) if (pane != except) pane.ProfilesChanged();
    }

    /// <summary>The toolbar picker shows each profile in its color.</summary>
    private void RefreshProfileColors()
    {
        profileColorSettings.Clear();
        if (project != null) foreach (var id in project.Profiles) { try { profileColorSettings[id] = ProfileManager.Load(project, id).Color; } catch (Exception) { profileColorSettings[id] = null; } }
        ProfilePicker.ItemTemplate ??= new Avalonia.Controls.Templates.FuncDataTemplate<string>((id, _) =>
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            if (id != null) row.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new(2), Background = ProfileColors.Brush(ProfileColors.For(id, profileColorSettings.GetValueOrDefault(id))), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = id ?? "", VerticalAlignment = VerticalAlignment.Center });
            return row;
        });
    }

    /// <summary>Why a profile's folder cannot be renamed or deleted now, or null.</summary>
    private string? ProfileFolderBlocker(string id)
    {
        if (project == null) return "Open a project first.";
        var folder = System.IO.Path.Combine(project.Root, "compatibility", id);
        if (tabs.Select(t => t.Content).OfType<EditorPane>().FirstOrDefault(p => Contains(folder, p.Document.FilePath)) is { } open)
            return $"Close {System.IO.Path.GetFileName(open.Document.FilePath)} (a file of {id}) first.";
        try { if (ExternalEditorSync.Load(project)?.Profile == id) return $"End external editing of {id} first."; } catch (Exception) { }
        return null;
    }

    private async void ManageProfilesClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            Require(project != null && operation == null && !externalBusy, "Open a project and wait for active work.");
            var current = project!;
            var select = await new ProfileManagerWindow(current, Profile, ProfileFolderBlocker).ShowDialog<string?>(this);
            if (project != current) return;
            var profiles = current.Profiles.ToArray();
            RefreshProfileColors();
            restoringProfile = true;
            try
            {
                // Rebinding the items drops the selection; take the closing choice, else keep the current profile if it still exists.
                var keep = select ?? Profile;
                ProfilePicker.ItemsSource = profiles; ProfilePicker.SelectedItem = profiles.Contains(keep) ? keep : InitialProfile(current);
            }
            finally { restoringProfile = false; }
            RememberProfile(); RefreshLaunchTargets();
            RefreshProfileDependents();
            if (select != null) Log("Profiles updated: " + string.Join(", ", profiles));
        }
        catch (Exception ex) { ShowError(ex); }
    }
}
