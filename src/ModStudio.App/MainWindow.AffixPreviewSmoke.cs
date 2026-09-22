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
    /// The Affix and Recipe previews: a cap's affix pool with the rare toggle, a runeword's bases, and the fixture's large
    /// synthetic cubemain table read as recipes. The tables created here are removed afterwards.
    /// </summary>
    private async Task SmokeAffixAndRecipePreviewAsync(string output)
    {
        string[] names = ["itemtypes", "armor", "weapons", "misc", "magicprefix", "magicsuffix", "properties", "itemstatcost", "runes"];
        foreach (var name in names) Require(!File.Exists(TableData.FileFor(project!, "tables", name)), "Affix preview smoke needs a fresh fixture: " + name);
        void Table(string name, string text) => TableData.Write(TableData.FileFor(project!, "tables", name), TableData.FromTsv(Utf8.GetBytes(text), name, "global/excel/" + name + ".txt"));
        Table("itemtypes", "ItemType\tCode\tEquiv1\tMaxSockets1\tMaxSocketsLevelThreshold1\tMaxSockets2\tMaxSocketsLevelThreshold2\tMaxSockets3\n" +
            "Any Armor\tarmo\t\t\t\t\t\t\nHelm\thelm\tarmo\t\t\t\t\t\nWeapon\tweap\t\t\t\t\t\t\nSword\tswor\tweap\t3\t25\t4\t40\t6\nRune\trune\t\t\t\t\t\t\n");
        Table("armor", "name\tcode\ttype\tlevel\nCap\tcap\thelm\t1\n");
        Table("weapons", "name\tcode\ttype\tlevel\tgemsockets\nShort Sword\tssd\tswor\t1\t2\n");
        Table("misc", "name\tcode\ttype\tlevelreq\nEl Rune\tr01\trune\t11\nTir Rune\tr03\trune\t13\n");
        Table("magicprefix", "Name\tspawnable\trare\tlevel\tfrequency\tgroup\tmod1code\tmod1min\tmod1max\titype1\n" +
            "Sturdy\t1\t1\t1\t3\t101\tac%\t10\t20\tarmo\nPlain\t1\t0\t1\t1\t102\thp\t1\t2\tarmo\n");
        Table("magicsuffix", "Name\tspawnable\trare\tlevel\tfrequency\tmod1code\tmod1min\tmod1max\titype1\nof Life\t1\t1\t1\t1\thp\t3\t5\tarmo\n");
        Table("properties", "code\tfunc1\tstat1\nac%\t2\titem_armor_percent\nhp\t1\tmaxhp\n");
        Table("itemstatcost", "Stat\tdescfunc\tdescval\tdescstrpos\nitem_armor_percent\t4\t2\tEnhanced Defense\nmaxhp\t19\t\t%+d to Life\n");
        Table("runes", "Name\t*Rune Name\tcomplete\titype1\tRune1\tRune2\tT1Code1\tT1Min1\tT1Max1\nRuneword1\tSteel\t1\tswor\tr03\tr01\thp\t10\t20\n");

        var armorPane = (await OpenDocumentAsync(TableData.FileFor(project!, "tables", "armor"), true))!;
        Require(affixPreviewTab.IsVisible && dropPreviewTab.IsVisible && !recipePreviewTab.IsVisible, "The armor table did not offer the affix and drop previews.");
        InspectorTabs.SelectedItem = affixPreviewTab;
        await PendingAffixPreview; // Filesystem notifications can invalidate the first request.
        armorPane.SelectCell(0, 0); await Task.Delay(250); await PendingAffixPreview;
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (LastAffixPreview?.Name != "Cap" && DateTime.UtcNow < deadline) { await Task.Delay(60); await PendingAffixPreview; }
        var cap = LastAffixPreview;
        Require(cap?.Name == "Cap" && cap.Prefixes.Select(p => p.Name).SequenceEqual(["Sturdy", "Plain"]) && cap.Suffixes.Single().Name == "of Life",
            $"Affix preview did not list the cap's pool: {cap?.Name} {string.Join(", ", cap?.Prefixes.Select(p => p.Name) ?? [])}.");
        Require(cap!.Prefixes[0].Chance == "75.0%" && cap.Prefixes[0].Effect == "Enhanced Defense +10–20%", $"Affix line is wrong: {cap.Prefixes[0].Chance} {cap.Prefixes[0].Effect}.");
        await Task.Delay(150); UpdateLayout();
        var rendered = affixPreviewContent.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToArray();
        Require(rendered.Contains("PREFIXES") && rendered.Contains("SUFFIXES") && rendered.Contains("Chance") && rendered.Contains("Effect"), $"Affix card was not laid out: [{string.Join(" | ", rendered.Take(14))}].");
        using (var bitmap = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96)))
        { bitmap.Render(this); bitmap.Save(System.IO.Path.Combine(output, "affix-preview.png"), PngBitmapEncoderOptions.Default); }
        affixRare.IsChecked = true;
        deadline = DateTime.UtcNow.AddSeconds(8);
        while (LastAffixPreview?.Prefixes.Length != 1 && DateTime.UtcNow < deadline) { await Task.Delay(60); await PendingAffixPreview; }
        Require(LastAffixPreview?.Prefixes.Single().Name == "Sturdy", "The rare toggle did not drop affixes rares cannot take.");
        affixRare.IsChecked = false;

        var runesPane = (await OpenDocumentAsync(TableData.FileFor(project!, "tables", "runes"), true))!;
        Require(recipePreviewTab.IsVisible && !affixPreviewTab.IsVisible, "The runes table did not reveal only the recipe preview.");
        InspectorTabs.SelectedItem = recipePreviewTab;
        await PendingRecipePreview;
        runesPane.SelectCell(0, 0); await Task.Delay(250); await PendingRecipePreview;
        deadline = DateTime.UtcNow.AddSeconds(8);
        while (LastRecipePreview?.Name != "Steel" && DateTime.UtcNow < deadline) { await Task.Delay(60); await PendingRecipePreview; }
        var steel = LastRecipePreview;
        Require(steel?.Name == "Steel" && steel.Lines[0].StartsWith("Tir Rune + El Rune · 2 sockets") && steel.Sections.Single(s => s.Title == "Bases").Lines.Any(l => l.Contains("Short Sword (ssd)"))
            && steel.Sections.Single(s => s.Title == "Properties").Lines.Contains("+10–20 to Life"), $"Runeword card is wrong: {steel?.Name} {string.Join(" | ", steel?.Lines ?? [])}.");
        await Task.Delay(150); UpdateLayout();
        using (var bitmap = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96)))
        { bitmap.Render(this); bitmap.Save(System.IO.Path.Combine(output, "recipe-preview.png"), PngBitmapEncoderOptions.Default); }

        // The fixture's cubemain has 1,600 synthetic rows: the card must still resolve, shadow checks included.
        var cubePane = (await OpenDocumentAsync(TableData.FileFor(project!, "tables", "cubemain"), true))!;
        InspectorTabs.SelectedItem = recipePreviewTab;
        cubePane.SelectCell(3, 0); await Task.Delay(250); await PendingRecipePreview;
        deadline = DateTime.UtcNow.AddSeconds(15);
        while ((LastRecipePreview == null || LastRecipePreview.Name == "Steel") && DateTime.UtcNow < deadline) { await Task.Delay(60); await PendingRecipePreview; }
        Require(LastRecipePreview != null && LastRecipePreview.Name != "Preview unavailable" && LastRecipePreview.Name != "Steel",
            "A synthetic cube recipe did not resolve: " + string.Join(" | ", LastRecipePreview?.Issues ?? []));

        foreach (var pane in new[] { cubePane, runesPane, armorPane })
            if (tabs.FirstOrDefault(t => t.Content == pane) is { } tab) await CloseTabAsync(tab);
        Require(!affixPreviewTab.IsVisible && !recipePreviewTab.IsVisible && InspectorTabs.SelectedIndex == 0, "Leaving the tables did not hide the previews and select Details.");
        foreach (var name in names) File.Delete(TableData.FileFor(project!, "tables", name));
    }
}
