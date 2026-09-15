using Avalonia.Controls;
using Avalonia.VisualTree;
using ModStudio.Core;
using static ModStudio.Core.Storage;

namespace ModStudio.App;

public partial class MainWindow
{
    private async Task SmokeSkillClassFilterAsync()
    {
        void Table(string name, string text) => TableData.Write(Semantics.TableFile(project!, name),
            TableData.FromTsv(Utf8.GetBytes(text), name, "global/excel/" + name + ".txt"));
        Table("skills", "skill\tcharclass\tskilldesc\nAmazon\tama\tamaskill\nSorceress\tsor\tsorskill\nGeneric\t\tanyskill\nShared generic\t\tamaskill\n");
        Table("skilldesc", "skilldesc\tstr name\namaskill\tAmazon\nsorskill\tSorceress\nanyskill\tAny\nunlinked\tUnlinked\n");
        var pane = await OpenDocumentAsync(Semantics.TableFile(project!, "skilldesc"));
        Require(pane != null, "Skilldesc class smoke could not open its table.");
        var editor = pane!;
        var dropdown = editor.GetVisualDescendants().OfType<ComboBox>().Single();
        int[] Rows() => ((IEnumerable<RowView>)editor.TableGrid.ItemsSource!).Where(r => !r.IsPlaceholder).Select(r => r.Row).ToArray();
        Require(dropdown.Items.OfType<ComboBoxItem>().Select(i => i.Content as string).SequenceEqual(["All", "Any", "Amazon", "Sorceress"])
            && dropdown.SelectedIndex == 0 && Rows().SequenceEqual([0, 1, 2, 3]), "Skilldesc did not start with the complete class dropdown and all rows.");
        dropdown.SelectedIndex = 1;
        Require(Rows().SequenceEqual([0, 2, 3]), "Any did not include neutral and unlinked descriptions.");
        dropdown.SelectedIndex = 2;
        Require(Rows().SequenceEqual([0]), "Amazon did not isolate its linked description.");
        dropdown.SelectedIndex = 3;
        Require(Rows().SequenceEqual([1]), "Sorceress did not isolate its linked description.");
        dropdown.SelectedIndex = 0;
        Require(Rows().SequenceEqual([0, 1, 2, 3]), "All did not restore every description.");
        editor.Jump(0, "skilldesc"); editor.ToggleFrozenRows();
        dropdown.SelectedIndex = 3;
        Require(!editor.FrozenGrid.IsVisible && Rows().SequenceEqual([1]), "A frozen Amazon row leaked into the Sorceress filter.");
        dropdown.SelectedIndex = 2;
        Require(editor.FrozenGrid.IsVisible && ((IEnumerable<RowView>)editor.FrozenGrid.ItemsSource!).Single().Row == 0,
            "The frozen Amazon row did not return under its class filter.");
    }
}
