using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace ModStudio.App;

// A persistent redirected shell, deliberately started only by an explicit user command.
public sealed class ProjectTerminal : Grid, IDisposable
{
    private readonly TextBox output = new() { IsReadOnly = true, AcceptsReturn = true, FontFamily = new("Consolas, Menlo, monospace"), TextWrapping = TextWrapping.NoWrap };
    private readonly TextBox input = new() { PlaceholderText = "Enter shell command (Enter to run; ↑/↓ for history)" };
    private readonly TextBlock location = new() { Text = "Open a project to use its shell", Margin = new(8, 4) };
    private readonly Button stop = new() { Content = "Stop shell", IsVisible = false };
    private readonly StringBuilder pending = new();
    private readonly List<string> history = [];
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private Process? process;
    private string? root;
    private int historyIndex;
    public ProjectTerminal()
    {
        RowDefinitions = new("Auto,*,Auto");
        var tools = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
        tools.Children.Add(location); tools.Children.Add(stop);
        var clear = new Button { Content = "Clear output" }; clear.Click += (_, _) => { lock (pending) pending.Clear(); output.Text = ""; }; tools.Children.Add(clear);
        ToolTip.SetTip(location, "Persistent cmd.exe / bash shell. Interactive console applications require an external terminal.");
        Children.Add(tools); SetRow(output, 1); Children.Add(output); SetRow(input, 2); Children.Add(input); input.IsEnabled = false;
        stop.Click += (_, _) => Stop();
        input.KeyDown += async (_, e) => {
            if (e.Key == Key.Enter) { e.Handled = true; var command = input.Text ?? ""; input.Text = ""; await SendAsync(command); }
            else if (e.Key is Key.Up or Key.Down && history.Count > 0) { e.Handled = true; historyIndex = Math.Clamp(historyIndex + (e.Key == Key.Up ? -1 : 1), 0, history.Count); input.Text = historyIndex == history.Count ? "" : history[historyIndex]; input.CaretIndex = input.Text.Length; }
        };
        timer.Tick += (_, _) => {
            string chunk; lock (pending) { chunk = pending.ToString(); pending.Clear(); }
            if (chunk.Length > 0) { var text = output.Text + chunk; output.Text = text.Length > 100000 ? text[^80000..] : text; output.CaretIndex = output.Text.Length; }
            stop.IsVisible = process is { HasExited: false };
        };
        timer.Start();
    }
    public void SetProject(string projectRoot)
    {
        if (root == projectRoot) return;
        Stop(); root = projectRoot; history.Clear(); historyIndex = 0; output.Text = "";
        lock (pending) pending.Clear();
        location.Text = (OperatingSystem.IsWindows() ? "cmd.exe" : "bash") + " · " + root;
        input.IsEnabled = true; input.Text = "";
    }
    internal string OutputText => output.Text ?? "";
    internal async Task SendAsync(string command)
    {
        if (root == null) return;
        try
        {
            if (process == null || process.HasExited)
            {
                process?.Dispose(); process = null;
                var start = new ProcessStartInfo { FileName = OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe" : "/bin/bash", WorkingDirectory = root,
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                if (OperatingSystem.IsWindows()) start.ArgumentList.Add("/Q");
                else { start.ArgumentList.Add("--noprofile"); start.ArgumentList.Add("--norc"); }
                var shell = new Process { StartInfo = start };
                try { shell.Start(); } catch { shell.Dispose(); throw; }
                process = shell; _ = ReadAsync(shell, shell.StandardOutput); _ = ReadAsync(shell, shell.StandardError);
            }
            history.Add(command); historyIndex = history.Count;
            Append("> " + command + Environment.NewLine);
            await process.StandardInput.WriteLineAsync(command); await process.StandardInput.FlushAsync();
        }
        catch (Exception ex) { Append("Shell error: " + ex.Message + Environment.NewLine); }
    }
    private async Task ReadAsync(Process shell, StreamReader reader)
    {
        var buffer = new char[2048];
        try { int count; while ((count = await reader.ReadAsync(buffer)) > 0) if (ReferenceEquals(process, shell)) Append(new string(buffer, 0, count)); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }
    private void Append(string text) { lock (pending) { pending.Append(text); if (pending.Length > 100000) pending.Remove(0, pending.Length - 80000); } }
    public void Stop()
    {
        var shell = process; process = null;
        if (shell != null) { try { if (!shell.HasExited) shell.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { Append("Could not stop shell: " + ex.Message + Environment.NewLine); } finally { shell.Dispose(); } }
        stop.IsVisible = false;
    }
    public void Dispose() { Stop(); timer.Stop(); }
}
