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
    /// The missile preview over a small chain: a skill fires a missile that explodes into another, which spawns the
    /// first again. The fixture tables are removed afterwards so the later smokes see their original source set.
    /// </summary>
    private async Task SmokeMissilePreviewAsync(string output)
    {
        var missilesFile = TableData.FileFor(project!, "tables", "missiles");
        var skillsFile = TableData.FileFor(project!, "tables", "skills");
        var originalSkills = await File.ReadAllBytesAsync(skillsFile);
        Require(!File.Exists(missilesFile), "Missile preview smoke needs a fresh fixture: " + missilesFile);
        var openSkills = tabs.FirstOrDefault(t => t.Content is EditorPane p && p.Document.FilePath == skillsFile);
        if (openSkills != null)
        {
            Require(!((EditorPane)openSkills.Content!).Document.IsDirty, "Missile preview smoke found unsaved edits in the skills table.");
            await CloseTabAsync(openSkills);
        }
        Require(!missilePreviewTab.IsVisible, "Missile preview tab is visible without a missiles table.");
        void Table(string name, string text) => TableData.Write(TableData.FileFor(project!, "tables", name), TableData.FromTsv(Utf8.GetBytes(text), name, "global/excel/" + name + ".txt"));
        Table("skills", "skill\tcharclass\tmaxlvl\tsrvmissile\tcltmissile\n" + "Fire Ball\tsor\t25\tfireball\tfireball\n" + "Meteor\tsor\t20\tfireball\t\n");
        Table("missiles",
            "Missile\tHitShift\tVel\tVelLev\tMaxVel\tRange\tEType\tEMin\tMinELev1\tEMax\tMaxELev1\tCollideKill\tKnockBack\tExplosionMissile\tSubMissile1\tCelFile\tAnimLen\tLoopAnim\tpSrvDoFunc\tParam1\t*param1 desc\n" +
            "fireball\t7\t18\t2\t24\t100\tfire\t12\t13\t28\t15\t1\t25\tfireballexp\t\tfireball\t8\t1\t13\t40\texplosion radius\n" +
            "fireballexp\t8\t\t\t\t\t\t\t\t\t\t0\t0\t\tfireball\texplosion\t12\t0\t\t\t\n");
        var pane = (await OpenDocumentAsync(missilesFile, true))!;
        Require(missilePreviewTab.IsVisible, "Missiles table did not reveal the missile preview tab.");
        Require(!skillPreviewTab.IsVisible, "Missiles table revealed the skill preview tab.");
        InspectorTabs.SelectedItem = missilePreviewTab;
        await PendingMissilePreview; // Filesystem notifications can invalidate the first request.
        pane.SelectCell(0, 0); await Task.Delay(200); await PendingMissilePreview;
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (LastMissilePreview?.Name != "fireball" && DateTime.UtcNow < deadline) { await Task.Delay(60); await PendingMissilePreview; }
        var ball = LastMissilePreview;
        Require(ball?.Name == "fireball", $"Missile inspector did not resolve the selected missile: {ball?.Name}.");
        // Fire Ball caps at 25, so the missile it fires is previewed to level 25.
        Require(ball!.MaxLevel == 25 && ball.Levels.Length == 25, $"Missile previewed {ball.Levels.Length} levels instead of the firing skill's 25.");
        Require(ball.Levels[0].Elemental == "6–14 fire" && ball.Levels[0].Velocity == "18" && ball.Levels[3].Velocity == "24",
            $"Level curve is wrong: {ball.Levels[0].Elemental} at {ball.Levels[0].Velocity} px/frame.");
        Require(ball.Sections.Single(s => s.Title == "Used by").Lines.Any(l => l.StartsWith("Fire Ball") && l.Contains("srvmissile, cltmissile")), "Used by did not list the firing skill.");
        Require(ball.Sections.Single(s => s.Title == "Spawns").Lines[0] == "ExplosionMissile → fireballexp", "Spawn chain did not resolve the explosion missile.");
        Require(ball.Issues.Length == 0, "Complete missile row reported an incomplete preview: " + string.Join(" ", ball.Issues));
        await Task.Delay(150); UpdateLayout();
        var rendered = missilePreviewContent.GetVisualDescendants().OfType<TextBlock>().Select(PreviewCards.RenderedText).ToArray();
        Require(rendered.Contains("fireball") && rendered.Contains("USED BY") && rendered.Contains("SPAWNS") && rendered.Contains("MOTION") && rendered.Contains("6–14 fire") && rendered.Contains("Velocity"),
            $"Missile card was not laid out with its sections and level table: [{string.Join(" | ", rendered.Take(12))}] ({rendered.Length} blocks).");
        Require(rendered.Any(t => t.Contains("ExplosionMissile → fireballexp")) && rendered.Any(t => t.Contains("Knockback chance: 25%")), "Missile card is missing its chain or behavior lines.");
        Require(missilePreviewContent.GetVisualDescendants().OfType<SelectableTextBlock>().Any(t => PreviewCards.RenderedText(t) == "6–14 fire"), "Missile preview values cannot be selected and copied.");
        using (var bitmap = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96)))
        { bitmap.Render(this); bitmap.Save(System.IO.Path.Combine(output, "missile-preview.png"), PngBitmapEncoderOptions.Default); }
        // The explosion is fired by no skill, so it falls back to the usual level range.
        pane.SelectCell(1, 0); await Task.Delay(200); await PendingMissilePreview;
        deadline = DateTime.UtcNow.AddSeconds(8);
        while (LastMissilePreview?.Name != "fireballexp" && DateTime.UtcNow < deadline) { await Task.Delay(60); await PendingMissilePreview; }
        var explosion = LastMissilePreview;
        Require(explosion?.MaxLevel == MissilePreviewResolver.DefaultMaxLevel && explosion.Levels.Length == MissilePreviewResolver.DefaultMaxLevel,
            $"A missile no skill fires previewed {explosion?.Levels.Length} levels.");
        Require(explosion!.Sections.Single(s => s.Title == "Used by").Lines.Any(l => l.Contains("missile fireball") && l.Contains("ExplosionMissile")), "Used by did not list the missile that spawns this one.");
        Require(missileLocale.ItemsSource is IEnumerable<string> locales && locales.SequenceEqual(project!.Locales()), "Missile locale picker does not offer the project's catalog locales.");
        await CloseTabAsync(tabs.First(t => t.Content == pane));
        Require(!missilePreviewTab.IsVisible && InspectorTabs.SelectedIndex == 0, "Leaving the missiles table did not hide the preview and select Details.");
        await File.WriteAllBytesAsync(skillsFile, originalSkills);
        File.Delete(missilesFile);
    }
}
