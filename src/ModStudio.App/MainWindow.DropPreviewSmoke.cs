using System.Text.Json.Nodes;
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
    /// The Drop and Monster previews over a small world: one zombie in two areas, a TC group it upgrades through, a cap
    /// with a unique. Both directions of the drop lookup and the shared magic find are exercised; the tables are removed after.
    /// </summary>
    private async Task SmokeDropAndMonsterPreviewAsync(string output)
    {
        string[] names = ["itemtypes", "armor", "itemratio", "uniqueitems", "treasureclassex", "monstats", "monlvl", "levels"];
        foreach (var name in names) Require(!File.Exists(TableData.FileFor(project!, "tables", name)), "Drop preview smoke needs a fresh fixture: " + name);
        Require(!dropPreviewTab.IsVisible && !monsterPreviewTab.IsVisible, "Drop or monster preview tab is visible without their tables.");
        void Table(string name, string text) => TableData.Write(TableData.FileFor(project!, "tables", name), TableData.FromTsv(Utf8.GetBytes(text), name, "global/excel/" + name + ".txt"));
        Table("itemtypes", "ItemType\tCode\tEquiv1\tTreasureClass\nAny Armor\tarmo\t\t1\nHelm\thelm\tarmo\t0\n");
        Table("armor", "name\tcode\tnamestr\ttype\tlevel\trarity\tspawnable\tnormcode\nCap\tcap\tcap\thelm\t1\t1\t1\tcap\n");
        Table("itemratio", "Function\tVersion\tUber\tClass Specific\tUnique\tUniqueDivisor\tUniqueMin\tRare\tRareDivisor\tRareMin\tSet\tSetDivisor\tSetMin\tMagic\tMagicDivisor\tMagicMin\n" +
            "Ratio\t1\t0\t0\t400\t1\t6400\t100\t2\t3200\t160\t2\t5600\t34\t3\t192\n");
        Table("uniqueitems", "index\tcode\tlvl\trarity\nBiggin's Bonnet\tcap\t3\t1\n");
        Table("treasureclassex", "Treasure Class\tgroup\tlevel\tPicks\tNoDrop\tItem1\tProb1\tItem2\tProb2\n" +
            "G A\t7\t5\t1\t4\tcap\t1\tarmo3\t1\n" + "G B\t7\t10\t1\t2\tcap\t1\t\t\n");
        Table("monstats", "Id\tNameStr\tLevel\tLevel(N)\tLevel(H)\tminHP\tmaxHP\tAC\tExp\tA1MinD\tA1MaxD\tA1TH\tMinHP(N)\tMaxHP(N)\tTreasureClass\tTreasureClassChamp\tTreasureClass(N)\n" +
            "zom\tzom\t1\t36\t67\t100\t200\t100\t100\t100\t200\t100\t100\t100\tG A\tG A\tG A\n");
        Table("monlvl", "Level\tL-HP\tL-AC\tL-TH\tL-DM\tL-XP\tL-HP(N)\n1\t7\t6\t8\t2\t30\t\n12\t\t\t\t\t\t200\n");
        Table("levels", "Name\tLevelName\tMonLvlEx\tMonLvlEx(N)\tmon1\tnmon1\nAct 1 - One\tone\t2\t12\tzom\tzom\n");

        var tcPane = (await OpenDocumentAsync(TableData.FileFor(project!, "tables", "treasureclassex"), true))!;
        Require(dropPreviewTab.IsVisible && !monsterPreviewTab.IsVisible, "The treasureclassex table did not reveal only the drop preview.");
        InspectorTabs.SelectedItem = dropPreviewTab;
        await PendingDropPreview; // Filesystem notifications can invalidate the first request.
        tcPane.SelectCell(0, 0); await Task.Delay(250); await PendingDropPreview;
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (LastDropPreview?.Name != "G A" && DateTime.UtcNow < deadline) { await Task.Delay(60); await PendingDropPreview; }
        var ga = LastDropPreview;
        Require(ga?.Name == "G A" && ga.ItemLevel == 5 && ga.Drops.Any(d => d.Code == "cap" && d.PerKill == "1 in 3"), $"Drop preview did not resolve G A: {ga?.Name} {string.Join(" | ", ga?.Drops.Select(d => d.Item + " " + d.PerKill) ?? [])}.");
        Require(ga!.Issues.Length == 0, "Complete TC reported problems: " + string.Join(" ", ga.Issues));
        await Task.Delay(150); UpdateLayout();
        var rendered = dropPreviewContent.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToArray();
        Require(rendered.Contains("ENTRIES") && rendered.Contains("Per kill") && rendered.Contains("Unique") && rendered.Contains("USED BY"), $"Drop card was not laid out: [{string.Join(" | ", rendered.Take(14))}].");
        using (var bitmap = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96)))
        { bitmap.Render(this); bitmap.Save(System.IO.Path.Combine(output, "drop-preview.png"), PngBitmapEncoderOptions.Default); }
        // Magic find typed into one tab applies to both.
        var magicFind = ((Control)dropPreviewTab.Content!).GetVisualDescendants().OfType<NumericUpDown>().First(n => ToolTip.GetTip(n) is string tip && tip.StartsWith("Magic find"));
        magicFind.Value = 500;
        deadline = DateTime.UtcNow.AddSeconds(8);
        while (LastDropPreview?.Lines.Any(l => l.Contains("500% magic find")) != true && DateTime.UtcNow < deadline) { await Task.Delay(60); await PendingDropPreview; }
        Require(dropInputs.MagicFind == 500 && LastDropPreview?.Lines.Any(l => l.Contains("500% magic find")) == true, "Magic find did not re-roll the drop preview.");

        var itemPane = (await OpenDocumentAsync(TableData.FileFor(project!, "tables", "uniqueitems"), true))!;
        Require(dropPreviewTab.IsVisible, "The uniqueitems table did not offer the drop preview.");
        InspectorTabs.SelectedItem = dropPreviewTab;
        itemPane.SelectCell(0, 0); await Task.Delay(250); await PendingDropPreview;
        deadline = DateTime.UtcNow.AddSeconds(8);
        while (LastDropPreview?.Name != "Biggin's Bonnet" && DateTime.UtcNow < deadline) { await Task.Delay(60); await PendingDropPreview; }
        var from = LastDropPreview?.Sections.FirstOrDefault(s => s.Title == "Drops from")?.Lines ?? [];
        Require(from.Any(l => l.Contains("Normal champion · zom · level 3 · G A")) && from.Any(l => l.Contains("Nightmare regular · zom · level 12 · G B")),
            "Reverse drop lookup did not find the monsters: " + string.Join(" | ", from));

        var monsterPane = (await OpenDocumentAsync(TableData.FileFor(project!, "tables", "monstats"), true))!;
        Require(monsterPreviewTab.IsVisible && !dropPreviewTab.IsVisible, "The monstats table did not reveal only the monster preview.");
        InspectorTabs.SelectedItem = monsterPreviewTab;
        await PendingMonsterPreview;
        monsterPane.SelectCell(0, 0); await Task.Delay(250); await PendingMonsterPreview;
        deadline = DateTime.UtcNow.AddSeconds(8);
        while (LastMonsterPreview?.Name != "zom" && DateTime.UtcNow < deadline) { await Task.Delay(60); await PendingMonsterPreview; }
        var zombie = LastMonsterPreview;
        Require(zombie?.Levels.FirstOrDefault()?.Life == "7–14" && zombie.Levels.Any(l => l.Difficulty == "Nightmare" && l.Level == "12" && l.Life == "200"),
            "Monster stats were not scaled by monlvl: " + string.Join(" | ", zombie?.Levels.Select(l => $"{l.Difficulty} {l.Level} {l.Life}") ?? []));
        Require(zombie!.Sections.Single(s => s.Title == "Drops").Lines.Any(l => l.StartsWith("Nightmare regular · level 12 · G B (from G A)")), "Monster drops did not upgrade their TC.");
        await Task.Delay(150); UpdateLayout();
        rendered = monsterPreviewContent.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToArray();
        Require(rendered.Contains("Life") && !rendered.Contains("Resist") && rendered.Contains("SPAWNS") && rendered.Contains("DROPS"), $"Monster card was not laid out: [{string.Join(" | ", rendered.Take(14))}].");
        Require(((Control)monsterPreviewTab.Content!).GetVisualDescendants().OfType<NumericUpDown>().Any(n => n.Value == 500), "The monster tab's magic find did not follow the drop tab's.");
        using (var bitmap = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96)))
        { bitmap.Render(this); bitmap.Save(System.IO.Path.Combine(output, "monster-preview.png"), PngBitmapEncoderOptions.Default); }
        // Editing the row's fields re-resolves the preview, whether it is showing or is returned to afterwards.
        async Task<string?> NormalLife(string expected)
        {
            var until = DateTime.UtcNow.AddSeconds(8);
            while (LastMonsterPreview?.Levels.FirstOrDefault()?.Life != expected && DateTime.UtcNow < until) { await Task.Delay(60); await PendingMonsterPreview; }
            return LastMonsterPreview?.Levels.FirstOrDefault()?.Life;
        }
        monsterPane.Document.SetCells([(0, "minHP", "200"), (0, "maxHP", "400")]);
        var life = await NormalLife("14–28"); Require(life == "14–28", "Monster preview did not follow an edit while showing: " + life);
        InspectorTabs.SelectedIndex = 0; await Task.Delay(100);
        monsterPane.Document.SetCells([(0, "minHP", "300")]); await Task.Delay(100);
        InspectorTabs.SelectedItem = monsterPreviewTab;
        life = await NormalLife("21–28"); Require(life == "21–28", "Monster preview did not follow an edit made on another tab: " + life);
        monsterPane.Document.Undo(); monsterPane.Document.Undo(); Require(!monsterPane.Document.IsDirty, "Monster edits were not undone.");
        life = await NormalLife("7–14"); Require(life == "7–14", "Monster preview did not follow undo: " + life);
        // The same through the Row Editor: type into a field, then return to the preview tab.
        InspectorTabs.SelectedIndex = 1; await Task.Delay(400); UpdateLayout();
        var minHp = RowEditorFields.GetVisualDescendants().OfType<TextBox>().First(t => Avalonia.Automation.AutomationProperties.GetName(t) == "minHP");
        minHp.Focus(); await Task.Delay(50); minHp.Text = "400"; await Task.Delay(100);
        InspectorTabs.SelectedItem = monsterPreviewTab;
        life = await NormalLife("28–14"); Require(life == "28–14","Monster preview did not follow a Row Editor edit: " + life + " cell " + monsterPane.Document.Table!.Cell(0, "minHP"));
        monsterPane.Document.Undo(); Require(!monsterPane.Document.IsDirty, "Row Editor edit was not undone.");

        dropInputs.MagicFind = 0; dropInputs.RaiseChanged();
        // Each table opened as the temporary preview tab, which replaces the one before it.
        foreach (var pane in new[] { monsterPane, itemPane, tcPane })
            if (tabs.FirstOrDefault(t => t.Content == pane) is { } tab) await CloseTabAsync(tab);
        Require(!dropPreviewTab.IsVisible && !monsterPreviewTab.IsVisible && InspectorTabs.SelectedIndex == 0, "Leaving the tables did not hide the previews and select Details.");
        foreach (var name in names) File.Delete(TableData.FileFor(project!, "tables", name));
    }
}
