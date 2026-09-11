using System.Diagnostics;
using System.Windows.Input;

namespace ModStudio.App;

internal sealed class MarkdownLinkCommand(Action<Exception> error) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => Uri.TryCreate(parameter?.ToString(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
    public void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        try { Process.Start(new ProcessStartInfo(parameter!.ToString()!) { UseShellExecute = true }); }
        catch (Exception ex) { error(ex); }
    }
}
