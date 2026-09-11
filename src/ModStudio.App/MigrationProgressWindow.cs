using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ModStudio.Core;

namespace ModStudio.App;

public sealed class MigrationProgressWindow : Window
{
    public MigrationProgressWindow(LegacyProject source, string destination, string name, CancellationTokenSource cancellation, Action<string> log, string? backup = null)
    {
        Title = "Migrating project · " + name; Width = 680; Height = 520; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new(26), Spacing = 16 };
        var heading = new TextBlock { Text = "Migrating your project", FontSize = 24, Foreground = new SolidColorBrush(Color.Parse("#D8BC86")) };
        panel.Children.Add(heading);
        panel.Children.Add(new TextBlock { Text = $"Project: {source.Root}\nData: {source.DataRoot}\nTo: {destination}" + (backup == null ? "" : "\nBackup: " + backup), TextWrapping = TextWrapping.Wrap });
        var progress = new ProgressBar { IsIndeterminate = true, Height = 8 };
        panel.Children.Add(progress);
        var stage = new TextBlock { Text = "Preparing migration…", FontSize = 16, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(stage);
        var elapsed = new TextBlock(); panel.Children.Add(elapsed);
        var detail = new TextBlock { Text = "Converting files and verifying the new project. Your original files stay unchanged.", TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(detail);
        var action = new Button { Content = "Cancel migration", HorizontalAlignment = HorizontalAlignment.Right };
        panel.Children.Add(action); Content = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var watch = Stopwatch.StartNew(); var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => elapsed.Text = $"Elapsed: {watch.Elapsed:mm\\:ss}";
        bool finished = false; ImportReport? result = null;
        void Cancel()
        {
            if (finished || cancellation.IsCancellationRequested) return;
            cancellation.Cancel(); action.IsEnabled = false;
            stage.Text = "Canceling migration…";
            detail.Text = "Waiting for the current step and cleanup to finish. The original project is unchanged.";
        }
        action.Click += (_, _) => { if (finished) Close(result); else Cancel(); };
        Closing += (_, e) => { if (!finished) { e.Cancel = true; Cancel(); } };
        Closed += (_, _) => timer.Stop();
        Opened += async (_, _) =>
        {
            timer.Start();
            try
            {
                void Progress(string message)
                {
                    log(message);
                    Dispatcher.UIThread.Post(() => { if (!finished && !cancellation.IsCancellationRequested) stage.Text = message; });
                }
                result = await Task.Run(() => backup == null ? LegacyMigration.Migrate(source, destination, name, cancellation.Token, Progress) : LegacyMigration.MigrateInPlace(source, name, backup, cancellation.Token, Progress));
                heading.Text = "Migration complete"; stage.Text = "Your project is ready";
                detail.Text = $"{result.Tables:N0} tables · {result.Catalogs:N0} string catalogs · {result.VerifiedTables:N0} verified TXT files\nA migration report is included in the new project.";
                action.Content = "Open migrated project";
            }
            catch (OperationCanceledException)
            {
                heading.Text = "Migration canceled"; stage.Text = "Cleanup finished";
                detail.Text = "No migrated project was published. Your original files are unchanged.";
                action.Content = "Close";
            }
            catch (Exception ex)
            {
                heading.Text = "Migration could not finish"; stage.Text = "Review the error below";
                detail.Text = ex.Message; log("Migration failed: " + ex.Message); action.Content = "Close";
            }
            finally
            {
                finished = true; timer.Stop(); watch.Stop();
                elapsed.Text = $"Elapsed: {watch.Elapsed:mm\\:ss}";
                progress.IsIndeterminate = false; progress.Value = result == null ? 0 : 100; action.IsEnabled = true;
            }
        };
    }
}
