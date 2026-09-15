using Avalonia.Controls;
using Avalonia.Threading;
using ModStudio.Core;

namespace ModStudio.App;

public sealed partial class EditorPane
{
    private SkillDescClassFilter? skillClasses;
    private bool updatingSkillClassDropdown;
    private static readonly IReadOnlyDictionary<string, string> SkillClassNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ama"] = "Amazon", ["ass"] = "Assassin", ["bar"] = "Barbarian", ["dru"] = "Druid",
        ["nec"] = "Necromancer", ["pal"] = "Paladin", ["sor"] = "Sorceress", ["war"] = "Warlock"
    };

    private void InitializeSkillClassDropdown(Panel toolbar)
    {
        if (Document.Table?.Name != "skilldesc") return;
        ToolTip.SetTip(skillClassDropdown, "All shows every description. Any shows descriptions linked to class-neutral skills or no skill.");
        toolbar.Children.Add(skillClassDropdown);
        Document.Changed += () =>
        {
            if (SelectedSkillClassCode().Length == 0 || Document.LastChangedColumns?.Contains("skilldesc") != true) return;
            var revision = Document.Revision;
            Dispatcher.UIThread.Post(() => { if (Document.Revision == revision && !Document.PendingSource) Refresh(); }, DispatcherPriority.Background);
        };
        skillClassDropdown.SelectionChanged += (_, _) =>
        {
            if (updatingSkillClassDropdown) return;
            try { UpdateSkillClassDropdown(updateOptions: false); Refresh(); }
            catch (Exception ex) { error(ex); updatingSkillClassDropdown = true; skillClassDropdown.SelectedIndex = 0; updatingSkillClassDropdown = false; skillClasses = null; Refresh(); }
        };
        skillClassDropdown.DropDownOpened += (_, _) =>
        {
            try { UpdateSkillClassDropdown(); }
            catch (Exception ex) { error(ex); }
        };
        try { UpdateSkillClassDropdown(); }
        catch (Exception ex)
        {
            error(ex);
            skillClassDropdown.Items.Add(new ComboBoxItem { Content = "All", Tag = "" });
            skillClassDropdown.SelectedIndex = 0;
        }
    }

    private string SelectedSkillClassCode() => (skillClassDropdown.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
    private SkillDescClassFilter? SelectedSkillClassFilter() => SelectedSkillClassCode().Length == 0 ? null : skillClasses;
    private bool SkillClassMatches(int row, SkillDescClassFilter? classFilter) => classFilter == null ||
        classFilter.Matches(Document.Table!.Cell(row, "skilldesc"), SelectedSkillClassCode());

    private void UpdateSkillClassDropdown(bool updateOptions = true)
    {
        var code = SelectedSkillClassCode();
        var extension = Path.GetExtension(Document.FilePath);
        var skillsPath = Path.Combine(Path.GetDirectoryName(Document.FilePath)!, "skills" + extension);
        var skills = findOpenDocument?.Invoke(skillsPath);
        if (skills?.PendingSource == true) throw new InvalidOperationException("Apply valid skills source before filtering skilldesc by class.");
        if (skills == null)
        {
            if (!File.Exists(skillsPath)) throw new FileNotFoundException("Class filtering needs the skills table beside skilldesc.", skillsPath);
            skills = new Document(skillsPath);
        }
        if (skills.Table == null || skills.PendingSource) throw new InvalidOperationException("Class filtering needs a valid skills.txt table.");
        skillClasses = new SkillDescClassFilter(skills.Table);
        if (!updateOptions) return;
        var options = new List<ComboBoxItem>
        {
            new() { Content = "All", Tag = "" },
            new() { Content = "Any", Tag = "*" }
        };
        foreach (var characterClass in skillClasses.Classes)
            options.Add(new ComboBoxItem { Content = SkillClassNames.GetValueOrDefault(characterClass, characterClass), Tag = characterClass });
        if (skillClassDropdown.Items.OfType<ComboBoxItem>().Select(i => (string?)i.Tag)
            .SequenceEqual(options.Select(i => (string?)i.Tag))) return;
        updatingSkillClassDropdown = true;
        try
        {
            skillClassDropdown.ItemsSource = options;
            skillClassDropdown.SelectedItem = options.FirstOrDefault(i => (string?)i.Tag == code) ?? options[0];
        }
        finally { updatingSkillClassDropdown = false; }
    }
}
