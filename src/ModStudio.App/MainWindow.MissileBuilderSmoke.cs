using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using ModStudio.Core;
using static ModStudio.Core.Storage;

namespace ModStudio.App;

public partial class MainWindow
{
    /// <summary>
    /// The missile Visual Builder: search, open a missile, edit its velocity and blend, aim it, follow its explosion, and
    /// capture the flight. MODSTUDIO_GAME_DATA pointing at extracted game data draws the real animations; without it the
    /// builder must say where to find them.
    /// </summary>
    private async Task SmokeMissileBuilderAsync(string output)
    {
        async Task Until(Func<bool> condition, string failure, Func<string>? state = null)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!condition()) { Require(DateTime.UtcNow < deadline, failure + (state == null ? "" : " " + state())); await Task.Delay(40); }
        }
        var file = TableData.FileFor(project!, "tables", "missiles");
        Require(!File.Exists(file), "Missile builder smoke needs a fresh fixture: " + file);
        TableData.Write(file, TableData.FromTsv(Utf8.GetBytes(
            "Missile\tCelFile\tVel\tMaxVel\tAccel\tRange\tLight\tFlicker\tRed\tGreen\tBlue\tAnimSpeed\tLoopAnim\tTrans\tExplosionMissile\tpSrvDoFunc\tParam1\tMinDamage\tMaxDamage\tHitShift\tEType\tEMin\tEMax\n"
            + "testbolt\tFirebolt\t20\t\t\t30\t7\t\t255\t178\t64\t16\t1\t1\ttestboom\t1\t\t2\t4\t8\tfire\t8\t16\n"
            + "testboom\tFireArrowExplode2\t0\t\t\t12\t13\t\t255\t178\t64\t16\t0\t1\t\t\t\t\t\t\t\t\t\n"), "missiles", "global/excel/missiles.txt"));
        bool realGameData = GameDataFolders().Count > 0;

        var pane = (await OpenDocumentAsync(file, true))!;
        var button = pane.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => Avalonia.Automation.AutomationProperties.GetName(b)?.StartsWith("Visual Builder") == true);
        Require(button != null, "The missiles table has no Visual Builder button.");
        button!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Require(pane.VisualBuilder is MissileBuilderView, "The missiles table did not open the missile builder.");
        var view = (MissileBuilderView)pane.VisualBuilder!;
        await Until(() => view.Results.Count == 2, "The missile builder did not list the missiles.", () => view.Results.Count.ToString());
        Require(view.Results[0] is { Id: "testbolt", CelFile: "Firebolt", Explosion: "testboom" }, "Missile entries lack their animation or explosion.");
        view.Search.Text = "arrowexplode";
        await Until(() => view.Results.Count == 1 && view.Results[0].Id == "testboom", "Missile search does not match animation names.");
        view.Search.Text = "";
        await Until(() => view.Results.Count == 2, "Clearing the missile search did not list both again.");
        view.Select(0);
        await Until(() => view.LastArt != null && view.LastHd != null, "The missile builder did not look for the animation.");
        if (realGameData)
        {
            Require(view.LastArt!.Animation is { Directions.Length: 16 } && view.LastExplosionArt?.Animation != null, "The real firebolt and its explosion did not decode: " + string.Join(" ", view.LastArt.Notes));
            Require(view.LastHd!.Unit != null || view.LastHd.Notes.Length > 0, "The HD definition was not looked up.");
        }
        else
        {
            Require(view.LastArt!.Animation == null && view.LastArt.Notes.Single().Contains("game data"), "A missing animation does not point at the game data folder.");
            Require(view.GetVisualDescendants().OfType<Button>().Any(b => b.Content as string == "Choose game data folder…"), "The flight offers no way to choose the game data folder.");
        }
        await Until(() => view.LastPreview != null, "The missile builder did not resolve damage.");

        // Editing velocity reshapes the flight; the blend and loop fields are a choice and a switch.
        var velocity = (TextBox)view.Editor("Vel")!; velocity.Focus(); velocity.Text = "30";
        await Until(() => pane.Document.Table!.Cell(0, "Vel") == "30" && view.Motion.Vel == 30, "Editing velocity did not reach the flight.");
        Require(view.RenderScene(2) is { Stage: SceneStage.Flight, Distance: 60 }, "The flight does not move at the edited velocity.");
        Require(view.Editor("Trans") is ComboBox { SelectedIndex: 1 } && view.Editor("LoopAnim") is CheckBox { IsChecked: true }, "Trans and LoopAnim are not shown as a blend choice and a switch.");
        ((ComboBox)view.Editor("Trans")!).SelectedIndex = 0;
        await Until(() => pane.Document.Table!.Cell(0, "Trans") == "0" && view.Motion.Trans == 0, "Choosing a blend mode did not write Trans.");
        var ending = view.RenderScene(30);
        Require(ending.Stage == (realGameData ? SceneStage.Explosion : SceneStage.Pause), $"The flight did not end in its explosion: {ending.Stage}");
        view.Facing = 16;
        var left = view.RenderScene(3); var right = view.RenderScene(4);
        Require(right.X < left.X, "Facing west does not fly left.");
        view.Facing = 44; view.RenderScene(12);
        await Task.Delay(250);
        using (var bitmap = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96)))
        { bitmap.Render(this); bitmap.Save(System.IO.Path.Combine(output, "missile-builder-fields.png"), PngBitmapEncoderOptions.Default); }
        view.ScrollToTop(); view.RenderScene(12); await Task.Delay(250);
        using (var bitmap = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96)))
        { bitmap.Render(this); bitmap.Save(System.IO.Path.Combine(output, "missile-builder.png"), PngBitmapEncoderOptions.Default); }

        // The explosion's arrow opens it in the builder.
        var open = view.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "→" && b.FindAncestorOfType<StackPanel>()?.Children.OfType<AutoCompleteBox>().Any(a => Avalonia.Automation.AutomationProperties.GetName(a) == "ExplosionMissile") == true);
        open.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Require(view.SelectedRow == 1, "Opening the explosion missile did not select it.");
        while (pane.Document.CanUndo) pane.Document.Undo();
        Require(!pane.Document.IsDirty, "Undo did not restore the missiles table.");
        await CloseTabAsync(tabs.First(t => t.Content == pane));
        File.Delete(file);
    }
}
