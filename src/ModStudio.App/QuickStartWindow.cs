using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace ModStudio.App;

public sealed class QuickStartWindow : Window
{
    private static readonly (string Title, string Body, string Tip)[] Steps =
    [
        ("Welcome to your JSON-source project",
         "Migration preserved your original files: either in the original project when creating a copy, or in a sibling backup when converting the existing folder. You now edit structured source data; Studio builds the native files the game needs.\n\nThe file tree shows a single entry for each table. Behind that entry are records.json (the rows) and schema.json (column order, identity rules and output paths).",
         "Single-click a table to preview it. Double-click to keep its tab open. Editing also keeps the tab open."),
        ("Where everything lives",
         "source/tables/<table>.json\nYour editable game-table rows, with the table schema (columns, output targets) in the same file. Switch to Source view to edit the schema.\n\nsource/strings/ and source/text/\nConverted translations and other supported text data.\n\ndata/\nNative assets that remain in their original format.\n\ncompatibility/<profile>/\nProfile-specific overrides, such as standard or d2rl.\n\n.studio/\nLocal recovery, settings and build output; keep this out of Git.",
         "migration-report.json explains what was converted. Extra native-project files may be retained under legacy/ for review; these are not deployed automatically."),
        ("Edit a row without horizontal scrolling",
         "Select a row, then switch the inspector to Row Editor. Every field is listed vertically. Find columns filters field names and stays active as you switch rows; clear the search to show everything. Changes apply to the shared document immediately; The Save icon above the document or Save all writes them to disk.\n\nDetails lets you edit an individual cell, inspect references and create supported profile overrides.\n\nIdentity fields and locked fields are protected. Use Undo/Redo to reverse edits. Hover the toolbar icons to see their actions. Opening images, sprites, textures, DC6 or supported DS1 files shows a read-only specialist preview with format details and a hex view.",
         "Right-click rows or column headers for actions. Drag header dividers to resize columns. Freezing keeps rows visible; locking prevents edits. Both are temporary editor settings."),
        ("Collaborate using source files",
         "Commit source data and project configuration to Git. The formatted JSON keeps each field on a separate line, making changes easier to review. Conflicts can still occur when people edit the same data.\n\nSorting and filtering in the table are view-only: they do not reorder the saved records. Preserve row identities and physical slots.\n\nThe schema action opens advanced metadata. Most everyday edits belong in the table or Row Editor.",
         "Use the bottom Terminal for Git commands and scripts, or Changes to compare the active table with its saved file. Resolve source validation errors in Problems before building."),
        ("Build, deploy, and play",
         "Choose a profile in the top-right selector. Shared edits apply across profiles; profile overrides capture intentional differences.\n\nBuild validates the source and refreshes one reusable native output, reusing unchanged conversions. Deploy copies the built mod to the destination configured in Run settings. Choose the game installation folder in Run settings. Studio detects D2R.exe and D2RLoader.exe when present; choose the launch target beside Play. Play builds and deploys before starting that target or its configured runner.\n\nKeep source and deployment directories separate. Edit the source project, then rebuild; changes made directly to deployed files are not imported back automatically.",
         "You can skip this guide and return anytime using the ? button. Run settings remains available whenever you need to configure another profile.")
    ];
    public int StepIndex { get; private set; }
    public QuickStartWindow()
    {
        Title = "Mod Studio quick start"; Width = 720; Height = 640; MinWidth = 540; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var layout = new Grid { RowDefinitions = new("Auto,*,Auto"), Margin = new(26) };
        var heading = new StackPanel { Spacing = 8, Margin = new(0,0,0,18) };
        var count = new TextBlock(); var title = new TextBlock { FontSize = 24, Foreground = new SolidColorBrush(Color.Parse("#D8BC86")), TextWrapping = TextWrapping.Wrap };
        heading.Children.Add(count); heading.Children.Add(title); layout.Children.Add(heading);
        var content = new StackPanel { Spacing = 20, Margin = new(0,0,24,0) };
        var body = new TextBlock { FontSize = 15, TextWrapping = TextWrapping.Wrap };
        var tip = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#D8BC86")) };
        content.Children.Add(body); content.Children.Add(new Border { Padding = new(14), Background = new SolidColorBrush(Color.Parse("#292727")), Child = tip });
        var scroll = new ScrollViewer { Content = content }; Grid.SetRow(scroll, 1); layout.Children.Add(scroll);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new(0,18,0,0) };
        var skip = new Button { Content = "Skip for now" }; var back = new Button { Content = "Back" }; var next = new Button { Content = "Next" };
        buttons.Children.Add(skip); buttons.Children.Add(back); buttons.Children.Add(next); Grid.SetRow(buttons, 2); layout.Children.Add(buttons); Content = layout;
        void Render()
        {
            var step = Steps[StepIndex]; count.Text = $"QUICK START · {StepIndex + 1} / {Steps.Length}";
            title.Text = step.Title; body.Text = step.Body; tip.Text = step.Tip;
            back.IsEnabled = StepIndex > 0; next.Content = StepIndex == Steps.Length - 1 ? "Start editing" : "Next";
            scroll.Offset = new Vector(0,0);
        }
        skip.Click += (_, _) => Close(); back.Click += (_, _) => { if (StepIndex > 0) StepIndex--; Render(); };
        next.Click += (_, _) => { if (StepIndex == Steps.Length - 1) Close(); else { StepIndex++; Render(); } };
        Render();
    }
}
