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
    /// The skill preview over a realistic two-skill table: one capped at 20 levels, one at 25, so the level curve is
    /// checked against the row's own maxlvl. The fixture's synthetic skills table is put back before the later smokes run.
    /// </summary>
    private async Task SmokeSkillPreviewAsync(string output)
    {
        var skillsFile = TableData.FileFor(project!, "tables", "skills");
        var descFile = TableData.FileFor(project!, "tables", "skilldesc");
        var catalogFile = TableData.FileFor(project!, "strings", "skill-preview-test");
        var originalSkills = await File.ReadAllBytesAsync(skillsFile);
        Require(!File.Exists(descFile), "Skill preview smoke needs a fresh fixture: " + descFile);
        var open = tabs.FirstOrDefault(t => t.Content is EditorPane p && p.Document.FilePath == skillsFile);
        if (open != null)
        {
            Require(!((EditorPane)open.Content!).Document.IsDirty, "Skill preview smoke found unsaved edits in the skills table.");
            await CloseTabAsync(open);
        }
        Require(!skillPreviewTab.IsVisible, "Skill preview tab is visible without a skills table.");
        void Table(string name, string text) => TableData.Write(TableData.FileFor(project!, "tables", name), TableData.FromTsv(Utf8.GetBytes(text), name, "global/excel/" + name + ".txt"));
        // Vanilla Fire Bolt values: 2.5 mana and 3–6 fire damage at level 1, 45–60 at level 20.
        Table("skills",
            "skill\tcharclass\tskilldesc\tmaxlvl\treqlevel\tmanashift\tmana\tlvlmana\tminmana\tHitShift\tEType\tEMin\tEMinLev1\tEMinLev2\tEMinLev3\tEMax\tEMaxLev1\tEMaxLev2\tEMaxLev3\tEDmgSymPerCalc\tsrvdofunc\tauralencalc\tParam1\n" +
            "Fire Bolt\tsor\tfire bolt\t20\t1\t7\t5\t0\t1\t7\tfire\t6\t3\t4\t8\t12\t3\t6\t10\tskill('Frost Nova'.blvl)*16\t2\tln12\t25\n" +
            "Frost Nova\tsor\tfrost nova\t25\t6\t8\t9\t1\t1\t8\tcold\t20\t5\t5\t5\t30\t5\t5\t5\t\t\t\t\n");
        Table("skilldesc",
            "skilldesc\tstr name\tstr long\tSkillPage\tSkillRow\tSkillColumn\tdescline1\tdesctexta1\tdesccalca1\tdesccalcb1\tdsc2line1\tdsc2texta1\tdsc2calca1\tdsc2calcb1\n" +
            "fire bolt\tskillname36\tskilllong36\t1\t1\t2\t75\tStrSkill5\tenma\texma\t36\tStrSkill3\tusmc\t256\n" +
            "frost nova\tskillname44\tskilllong44\t1\t2\t1\t\t\t\t\t\t\t\t\n");
        TableData.Write(catalogFile, new TableData(
            new JsonObject { ["schemaVersion"] = 1, ["category"] = "skill-preview-test", ["locales"] = new JsonArray("enUS"), ["target"] = "local/lng/strings/skill-preview-test.json" },
            new JsonArray(new[] { ("skillname36", "Fire Bolt"), ("skilllong36", "Hurls a bolt of fire."), ("StrSkill5", "Fire Damage: %d to %d"), ("StrSkill3", "Mana Cost: %d"), ("skillname44", "Frost Nova"), ("skilllong44", "A wave of cold.") }
                .Select(x => (JsonNode)new JsonObject { ["Key"] = x.Item1, ["translations"] = new JsonObject { ["enUS"] = x.Item2 } }).ToArray())));

        var pane = (await OpenDocumentAsync(skillsFile, true))!;
        Require(skillPreviewTab.IsVisible, "Skills table did not reveal the skill preview tab.");
        InspectorTabs.SelectedItem = skillPreviewTab;
        await PendingSkillPreview; // Filesystem notifications can invalidate the first request.
        pane.SelectCell(0, 0); await Task.Delay(200); await PendingSkillPreview;
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (LastSkillPreview?.Name != "Fire Bolt" && DateTime.UtcNow < deadline) { await Task.Delay(60); await PendingSkillPreview; }
        var bolt = LastSkillPreview;
        Require(bolt?.Name == "Fire Bolt" && bolt.CharacterClass == "Sorceress", $"Skill inspector did not resolve the selected skill: {bolt?.Name}.");
        Require(bolt!.MaxLevel == 20 && bolt.Levels.Length == 20, $"Fire Bolt previewed {bolt.Levels.Length} levels instead of its maxlvl 20.");
        Require(bolt.Levels[0].Mana == "2.5" && bolt.Levels[0].Elemental == "3–6 fire" && bolt.Levels[19].Elemental == "45–60 fire",
            $"Level curve is wrong: level 1 {bolt.Levels[0].Mana} mana / {bolt.Levels[0].Elemental}, level 20 {bolt.Levels[19].Elemental}.");
        Require(bolt.Issues.Length == 0, "Complete skill row reported an incomplete preview: " + string.Join(" ", bolt.Issues));
        await Task.Delay(150); UpdateLayout();
        var rendered = skillPreviewContent.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToArray();
        Require(rendered.Contains("Fire Bolt") && rendered.Any(t => t.Contains("Hurls a bolt of fire.")) && rendered.Contains("45–60 fire") && rendered.Contains("Lvl") && rendered.Contains("Mana"),
            $"Skill card was not laid out in the inspector with its level table: [{string.Join(" | ", rendered.Take(12))}] ({rendered.Length} blocks).");
        Require(!rendered.Contains("Damage") && !rendered.Contains("Length"), "Level table shows columns the skill does not author.");
        Require(skillPreviewContent.GetVisualDescendants().OfType<SelectableTextBlock>().Any(t => t.Text == "2.5"), "Skill preview values cannot be selected and copied.");
        var tooltip = bolt.Sections.Single(s => s.Title == "Tooltip at level 1").Lines;
        Require(tooltip.Contains("Mana Cost: 2.5") && tooltip.Contains("  Fire Damage: 3 to 6") && rendered.Contains("TOOLTIP AT LEVEL 1") && rendered.Any(t => t.Contains("Fire Damage: 4 to 7")),
            "Skill tooltip was not rendered with its calculations: " + string.Join(" | ", tooltip));
        Require(rendered.Any(t => t.Contains("srvdofunc 2 · DoApplyDamage")) && rendered.Any(t => t.Contains("auralencalc = 25 (1 sec)")), "Skill card is missing its functions or calculations.");
        // The synergy skill the calculations read becomes an input; raising it must flow through to the level table.
        var synergyBox = skillInputs.GetVisualDescendants().OfType<NumericUpDown>().FirstOrDefault(n => Avalonia.Automation.AutomationProperties.GetName(n) == "Frost Nova level");
        Require(synergyBox != null && bolt.Inputs.Any(i => i.Name == "Frost Nova"), "The synergy skill was not offered as an input.");
        synergyBox!.Value = 10;
        deadline = DateTime.UtcNow.AddSeconds(8);
        while (LastSkillPreview?.Levels.FirstOrDefault()?.Synergy != "+160%" && DateTime.UtcNow < deadline) { await Task.Delay(60); await PendingSkillPreview; }
        Require(LastSkillPreview?.Levels[0].WithSynergy == "7–15 fire", $"Setting a synergy level did not update the level table: {LastSkillPreview?.Levels.FirstOrDefault()?.WithSynergy}.");
        calcInputs.Level = 5; calcInputs.RaiseChanged();
        deadline = DateTime.UtcNow.AddSeconds(8);
        while (LastSkillPreview?.Level != 5 && DateTime.UtcNow < deadline) { await Task.Delay(60); await PendingSkillPreview; }
        Require(LastSkillPreview?.Sections.Any(s => s.Title == "Tooltip at level 5" && s.Lines.Contains("  Fire Damage: 23 to 31")) == true, "The level input did not move the tooltip.");
        await Task.Delay(150); UpdateLayout();
        using (var bitmap = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96)))
        { bitmap.Render(this); bitmap.Save(System.IO.Path.Combine(output, "skill-preview-synergy.png"), PngBitmapEncoderOptions.Default); }
        calcInputs.Level = 1; calcInputs.SkillLevels.Clear(); calcInputs.RaiseChanged(); await PendingSkillPreview;
        // The Details tab explains the selected function cell, and the Row Editor marks the fields that function reads.
        InspectorTabs.SelectedIndex = 0;
        pane.SelectCell(0, pane.Document.Table!.ColumnIndex("srvdofunc")); await Task.Delay(300);
        Require(InspectorInfo.Text?.StartsWith("srvdofunc 2 · DoApplyDamage") == true && InspectorInfo.Text.Contains("auralencalc = ln12"), "Details did not explain the selected function cell: " + InspectorInfo.Text);
        pane.SelectCell(0, pane.Document.Table!.ColumnIndex("auralencalc")); await Task.Delay(300);
        Require(InspectorInfo.Text?.Contains("Read by srvdofunc 2") == true && InspectorInfo.Text.Contains("ln12 — par1 + (lvl - 1) * par2 (Param1 = 25, Param2 = 0)"), "Details did not explain the selected formula: " + InspectorInfo.Text);
        InspectorTabs.SelectedIndex = 1; await Task.Delay(300);
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (rowEditorAllFields.Length == 0 && DateTime.UtcNow < deadline) await Task.Delay(60);
        Require(rowEditorAllFields.Single(f => f.Column == "srvdofunc").Hint == "→ DoApplyDamage" && rowEditorAllFields.Single(f => f.Column == "auralencalc").Hint == "read by srvdofunc 2",
            "Row Editor did not mark the function and the field it reads.");
        InspectorTabs.SelectedItem = skillPreviewTab;
        pane.SelectCell(0, 0); await Task.Delay(200); await PendingSkillPreview;
        // The second skill caps at 25, so the table must follow the row rather than a fixed maximum.
        pane.SelectCell(1, 0); await Task.Delay(200); await PendingSkillPreview;
        deadline = DateTime.UtcNow.AddSeconds(8);
        while (LastSkillPreview?.Name != "Frost Nova" && DateTime.UtcNow < deadline) { await Task.Delay(60); await PendingSkillPreview; }
        var nova = LastSkillPreview;
        Require(nova?.MaxLevel == 25 && nova.Levels.Length == 25 && nova.Levels[24].Level == 25, $"A maxlvl 25 skill previewed {nova?.Levels.Length} levels.");
        Require(nova!.Levels[0].Mana == "9" && nova.Levels[24].Mana == "33" && nova.Levels[0].Elemental == "20–30 cold", "Per-level mana or cold damage is wrong for the 25-level skill.");
        Require(skillLocale.ItemsSource is IEnumerable<string> locales && locales.SequenceEqual(project!.Locales()) && skillLocale.SelectedItem as string == "enUS",
            "Skill locale picker does not offer the project's catalog locales.");
        await Task.Delay(100);
        using (var bitmap = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96)))
        { bitmap.Render(this); bitmap.Save(System.IO.Path.Combine(output, "skill-preview.png"), PngBitmapEncoderOptions.Default); }
        await CloseTabAsync(tabs.First(t => t.Content == pane));
        Require(!skillPreviewTab.IsVisible && InspectorTabs.SelectedIndex == 0, "Leaving the skills table did not hide the preview and select Details.");
        // Put the fixture back so the later build and git smokes see their original source set.
        await File.WriteAllBytesAsync(skillsFile, originalSkills);
        File.Delete(descFile); File.Delete(catalogFile);
    }
}
