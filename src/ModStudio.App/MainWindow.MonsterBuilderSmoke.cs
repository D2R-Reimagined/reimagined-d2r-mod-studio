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
    /// The monster Visual Builder: search, open a monster, read its infobox, edit a stat and see it there, switch modes,
    /// walk at its velocity, open monstats2 and edit the look, and follow an infobox number to its field.
    /// MODSTUDIO_GAME_DATA pointing at extracted game data draws the real zombie; without it the builder must say where to find it.
    /// </summary>
    private async Task SmokeMonsterBuilderAsync(string output)
    {
        async Task Until(Func<bool> condition, string failure, Func<string>? state = null)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!condition()) { Require(DateTime.UtcNow < deadline, failure + (state == null ? "" : " " + state())); await Task.Delay(40); }
        }
        var created = new List<string>();
        void Table(string name, string text)
        {
            var file = TableData.FileFor(project!, "tables", name); Require(!File.Exists(file), "Monster builder smoke needs a fresh fixture: " + file);
            TableData.Write(file, TableData.FromTsv(Utf8.GetBytes(text), name, "global/excel/" + name + ".txt")); created.Add(file);
        }
        Table("monstats", "Id\tNameStr\tCode\tMonStatsEx\tMonType\tAI\tTransLvl\tenabled\tkillable\tnoRatio\tVelocity\tRun\tLevel\tLevel(N)\tLevel(H)\tminHP\tmaxHP\tAC\tExp\tA1MinD\tA1MaxD\tA1TH\tMinHP(N)\tMaxHP(N)\tMinHP(H)\tMaxHP(H)\tResFi\tResFi(N)\tResFi(H)\tResPo\tTreasureClass\n"
            + "testzombie\ttestzombie\tZM\ttestlook\tzombie\tZombie\t2\t1\t1\t1\t2\t4\t5\t40\t70\t100\t150\t50\t80\t4\t8\t30\t400\t500\t900\t1100\t0\t20\t50\t100\tAct 1 H2H A\n"
            + "testghoul\ttestghoul\tZM\ttestlook\tzombie\tZombie\t3\t1\t1\t1\t2\t4\t8\t43\t73\t200\t250\t60\t90\t5\t9\t35\t\t\t\t\t\t\t\t\t\n");
        Table("monstats2", "Id\tBaseW\tHDv\tTRv\tLGv\tRav\tLav\tS1v\tS2v\tS3v\tHD\tTR\tLG\tRA\tLA\tS1\tS2\tS3\tmNU\tmWL\tmA1\tmA2\tmDT\tmGH\tmRN\tdNU\tdWL\tdA1\tShadow\tLight\tlight-r\tlight-g\tlight-b\n"
            + "testlook\thth\tlit,med,hvy\tlit,med,hvy\tlit,med,hvy\tlit,med,hvy\tlit,med,hvy\tlit,med,hvy\tlit,med,hvy\tbld,lhr,shr\t1\t1\t1\t1\t1\t1\t1\t1\t1\t1\t1\t1\t1\t1\t1\t8\t8\t8\t1\t0\t255\t255\t255\n");
        bool realGameData = GameDataFolders().Count > 0;

        var pane = (await OpenDocumentAsync(TableData.FileFor(project!, "tables", "monstats"), true))!;
        var button = pane.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => Avalonia.Automation.AutomationProperties.GetName(b)?.StartsWith("Visual Builder") == true);
        Require(button != null, "The monstats table has no Visual Builder button.");
        button!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Require(pane.VisualBuilder is MonsterBuilderView, "The monstats table did not open the monster builder.");
        var view = (MonsterBuilderView)pane.VisualBuilder!;
        await Until(() => view.Results.Count == 2, "The monster builder did not list the monsters.");
        view.Search.Text = "ghoul";
        await Until(() => view.Results.Count == 1 && view.Results[0].Id == "testghoul", "Monster search does not narrow by name.");
        view.Search.Text = "";
        await Until(() => view.Results.Count == 2, "Clearing the monster search did not list both again.");
        view.Select(0);
        await Until(() => view.LastLook is { Token: "ZM" } && view.LastComposite != null, "The builder did not assemble the monster's look from monstats2.", () => (view.LastLook?.Token ?? "no look") + " · " + view.CompositeNote + " · " + view.Mode);
        Require(view.LastLook!.Modes.SequenceEqual(["NU", "WL", "RN", "A1", "A2", "GH", "DT"]) && view.LastLook.LookCount == 3 && view.LastLook.TransLvl == 2, "The look lost its modes, variants or colour shift.");
        if (realGameData)
        {
            await Until(() => view.LastComposite?.Drawable == true && view.LastComposite.Mode == "wl", "The real zombie did not assemble.", () => string.Join(" ", view.LastComposite?.Notes ?? []));
            Require(view.LastComposite!.Layers.Count(l => l.Animation != null) == 8 && view.LastComposite.Shift != null, "The zombie's parts or colour shift are missing.");
        }
        else Require(!view.LastComposite!.Drawable && view.GetVisualDescendants().OfType<Button>().Any(b => b.Content as string == "Choose game data folder…"), "Without game data the builder does not point at the game data folder.");
        await Until(() => view.LastPreview is { Levels.Length: > 0 }, "The monster's stats did not resolve.");
        Require(view.LastPreview!.Levels[0].Life == "100–150", "The infobox's life is not the authored range: " + view.LastPreview.Levels[0].Life);

        // An edited stat reaches the infobox.
        var minLife = (TextBox)view.Editor("minHP")!; minLife.Focus(); minLife.Text = "120";
        await Until(() => pane.Document.Table!.Cell(0, "minHP") == "120" && view.LastPreview?.Levels[0].Life == "120–150", "Editing life did not reach the infobox.", () => view.LastPreview?.Levels[0].Life ?? "");
        Require(view.Editor("MinHP(N)") is TextBox && view.Editor("ResFi(H)") is TextBox && view.Editor("TreasureClass") is AutoCompleteBox && view.Editor("killable") is CheckBox,
            "Per-difficulty fields, treasure classes or flags are missing from the editor.");
        // Walking moves the floor at the monster's velocity; attack plays the attack.
        view.Mode = "WL";
        var walked = view.RenderScene(10);
        Require(Math.Abs(walked.Distance - 2 * MonsterScene.PixelsPerVelocity * 10) < 0.01, "Walking does not move at the monster's velocity.");
        view.Mode = "A1";
        await Until(() => view.LastComposite?.Mode == "a1", "Switching mode did not assemble the attack.");
        if (realGameData) Require(view.RenderScene(8) is { Frames: 16, Frame: >= 0 }, "The attack does not animate.");
        view.Difficulty = 2;
        Require(view.Infobox.GetVisualDescendants().OfType<TextBlock>().Any(t => PreviewCards.RenderedText(t) == "900–1,100"), "The Hell tab does not show Hell's life.");
        // An infobox number opens its field.
        var life = view.Infobox.GetVisualDescendants().OfType<PreviewLinkText>().First(t => t.PlainText == "900–1,100");
        life.RaiseEvent(new PreviewLinkEventArgs(PreviewLinkText.LinkClickedEvent, [.. life.Links[0].Targets], life));
        await Until(() => view.Editor("MinHP(H)")!.IsFocused, "An infobox number did not focus its field.");

        // The look is edited through monstats2, opened in its own tab.
        Require(!view.LooksEditable && view.LinkedEditor("Light") is TextBox { IsEnabled: false }, "monstats2 fields are editable before monstats2 is opened.");
        view.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "Edit look (opens monstats2)").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await Until(() => view.LooksEditable, "Opening monstats2 did not make the look editable.");
        var looksPane = tabs.Select(t => t.Content).OfType<EditorPane>().Single(p => p.Document.Table?.Name == "monstats2");
        Require(Active == pane, "Opening monstats2 took focus from the monster.");
        var light = (TextBox)view.LinkedEditor("Light")!; light.Focus(); light.Text = "6";
        await Until(() => looksPane.Document.Table!.Cell(0, "Light") == "6" && view.LastLook?.Light == 6, "Editing the look did not reach monstats2 and the preview.");
        view.Mode = "NU"; view.Difficulty = 0; view.ScrollToTop(); view.RenderScene(12);
        await Task.Delay(300);
        using (var bitmap = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96)))
        { bitmap.Render(this); bitmap.Save(System.IO.Path.Combine(output, "monster-builder.png"), PngBitmapEncoderOptions.Default); }
        // With room to spare the infobox sits beside the monster.
        var (width, height) = (Width, Height); Width = 2000; Height = 1100; await Task.Delay(400);
        using (var wide = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96)))
        { wide.Render(this); wide.Save(System.IO.Path.Combine(output, "monster-builder-wide.png"), PngBitmapEncoderOptions.Default); }
        Width = width; Height = height; await Task.Delay(100);

        while (looksPane.Document.CanUndo) looksPane.Document.Undo();
        while (pane.Document.CanUndo) pane.Document.Undo();
        Require(!pane.Document.IsDirty && !looksPane.Document.IsDirty, "Undo did not restore monstats and monstats2.");
        await CloseTabAsync(tabs.First(t => t.Content == looksPane));
        await CloseTabAsync(tabs.First(t => t.Content == pane));
        foreach (var file in created) File.Delete(file);
    }
}
