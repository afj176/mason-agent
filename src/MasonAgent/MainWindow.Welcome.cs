using Mason.Code;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Mason;

// Empty and first-run states for Code and Chat. These are the only places Mortar appears at
// hero size; everything they say describes behaviour the app already has.
public sealed partial class MainWindow
{
    /// <summary>
    /// Centred hero column. The returned grid is stretched by its caller (MinHeight) so the
    /// column sits in the middle of the available space instead of floating at the top.
    /// </summary>
    static Grid WelcomeHost(double maxWidth, params UIElement[] items)
    {
        var column = new StackPanel
        {
            Spacing = 14, MaxWidth = maxWidth,
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 16, 8, 16)
        };
        foreach (var item in items) column.Children.Add(item);
        var host = new Grid();
        host.Children.Add(column);
        // A StackPanel grows to its widest child; cap it at the space available so narrow windows
        // centre the column and wrap its contents instead of pushing them off to one side.
        host.SizeChanged += (_, _) => { if (host.ActualWidth > 0) column.MaxWidth = Math.Min(maxWidth, Math.Max(200, host.ActualWidth - 16)); };
        host.Loaded += (_, _) => FadeIn(column);
        return host;
    }

    /// <summary>
    /// Centres a welcome column in the given viewport height. When the column does not fit, the mascot,
    /// and then the supporting copy, step aside so the title, actions and cards are not cut off. The
    /// column is measured at the current width, so wrapping text and display scaling are accounted for.
    /// </summary>
    static void FitWelcome(Grid host, double viewport)
    {
        host.MinHeight = Math.Max(0, viewport);
        if (host.Children.FirstOrDefault() is not StackPanel column || column.Children.Count < 3 || viewport <= 0) return;
        var mascot = (UIElement)column.Children[0];
        var body = (UIElement)column.Children[2];
        double width = host.ActualWidth > 0 ? host.ActualWidth : double.PositiveInfinity;
        bool Fits() { column.Measure(new Windows.Foundation.Size(width, double.PositiveInfinity)); return column.DesiredSize.Height <= viewport; }
        mascot.Visibility = body.Visibility = Visibility.Visible;
        if (Fits()) return;
        mascot.Visibility = Visibility.Collapsed;
        if (!Fits()) body.Visibility = Visibility.Collapsed;
    }

    static TextBlock WelcomeTitle(string text, double size = 30)
    {
        var title = Heading(text, size);
        // Fill the column and centre the text inside it. A wrapping TextBlock that is itself centred
        // draws its text offset by the centring margin, which pushed the title right in narrow windows.
        title.TextAlignment = TextAlignment.Center;
        title.TextWrapping = TextWrapping.Wrap;
        title.HorizontalAlignment = HorizontalAlignment.Stretch;
        AutomationProperties.SetHeadingLevel(title, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1);
        return title;
    }

    static TextBlock WelcomeBody(string text)
    {
        var body = Muted(text, 15);
        body.TextAlignment = TextAlignment.Center;
        body.HorizontalAlignment = HorizontalAlignment.Stretch; // same wrapping-text centring quirk as the title
        body.MaxWidth = 520;
        body.LineHeight = 23;
        return body;
    }

    static T Centered<T>(T image) where T : FrameworkElement
    {
        image.HorizontalAlignment = HorizontalAlignment.Center;
        return image;
    }

    /// <summary>A column of starter prompts. Choosing one only fills the composer; nothing is sent.</summary>
    static StackPanel PromptList(Action<string> fill, params (string Label, string Glyph, string Prompt)[] prompts)
    {
        var list = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var (label, glyph, prompt) in prompts)
        {
            var chip = PromptChip(label, glyph, () => fill(prompt));
            chip.HorizontalAlignment = HorizontalAlignment.Stretch;
            chip.HorizontalContentAlignment = HorizontalAlignment.Left;
            ToolTipService.SetToolTip(chip, "Puts this prompt in the message box. Nothing is sent until you choose Send.");
            list.Children.Add(chip);
        }
        // The chips arrive one after another, and the arrow keys move between them.
        return Fluent.ArrowKeys(Fluent.Stagger(list));
    }

    /// <summary>
    /// Turns an existing "nothing here" panel (title, explanation, buttons) into a centred empty state
    /// with Mortar above it. The panel and its children keep their identity, so callers that update
    /// the texts or toggle visibility keep working. Returns Mortar's image so the mood can follow the state.
    /// </summary>
    static Image EmptyState(StackPanel panel, string mood, string meaning)
    {
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var button in panel.Children.OfType<Button>().ToArray())
        {
            panel.Children.Remove(button);
            actions.Children.Add(button);
        }
        foreach (var text in panel.Children.OfType<TextBlock>())
        {
            text.TextAlignment = TextAlignment.Center;
            text.HorizontalAlignment = HorizontalAlignment.Center;
            if (text.FontSize < 18) text.Opacity = .8;
        }
        var mortar = MascotImage(mood, 96, meaning);
        mortar.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Children.Insert(0, mortar);
        panel.Children.Add(actions);
        panel.Spacing = 10; panel.MaxWidth = 520;
        panel.HorizontalAlignment = HorizontalAlignment.Center;
        panel.VerticalAlignment = VerticalAlignment.Center;
        panel.Margin = new Thickness(16, 28, 16, 28);
        return mortar;
    }

    /// <summary>First run: no project yet. One primary action, and what to expect after it.</summary>
    Grid CodeOnboarding(Func<Task> addProject)
    {
        var add = Button("Add project", async (_, _) => await addProject(), true);
        add.Content = Row(new FontIcon { Glyph = "", FontSize = 15 }, new TextBlock { Text = "Add a project folder", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        add.Padding = new Thickness(20, 10, 22, 10);
        add.HorizontalAlignment = HorizontalAlignment.Center;
        AutomationProperties.SetName(add, "Add project");
        ToolTipService.SetToolTip(add, "Choose an existing folder, or create one in the folder picker.");

        static FrameworkElement Fact(string glyph, string title, string detail)
        {
            var name = Text(title, 13);
            name.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            var icon = new FontIcon { Glyph = glyph, FontSize = 16, HorizontalAlignment = HorizontalAlignment.Left };
            var item = new StackPanel { Spacing = 6 };
            item.Children.Add(icon); item.Children.Add(name); item.Children.Add(Muted(detail, 12));
            return BrandSurface(item, new Thickness(14, 12, 14, 14));
        }
        var facts = Fluent.Stagger(new Grid { ColumnSpacing = 10, Margin = new Thickness(0, 14, 0, 0) });
        var items = new[]
        {
            Fact("", "Stays on this computer", "Sessions and their history are saved in your local profile."),
            Fact("", "Read-only to start", "New sessions cannot edit files until you change the permission mode."),
            Fact("", "Changes on record", "Review a session's file changes as diffs and restore originals.")
        };
        for (int i = 0; i < items.Length; i++)
        {
            facts.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(items[i], i);
            facts.Children.Add(items[i]);
        }
        var models = CodeLink("Set up a model in Models & endpoints", (_, _) => Navigate("Models and connections"));
        models.HorizontalAlignment = HorizontalAlignment.Center;
        models.Padding = new Thickness(0, 4, 0, 4);

        return WelcomeHost(640,
            Centered(MascotImage(MoodWelcome, 148, "glad you're here")),
            WelcomeTitle("Bring a project into focus."),
            WelcomeBody("Add a folder and Mason Agent keeps its conversations, files and changes together."),
            add, models, facts);
    }

    /// <summary>A project is selected but has no session yet.</summary>
    Grid CodeProjectIntro(CodeProject project, Action newSession, Func<Task> addProject)
    {
        var start = Button("New session", (_, _) => newSession(), true);
        start.Content = Row(new FontIcon { Glyph = "", FontSize = 13 }, new TextBlock { Text = "New session", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        start.Padding = new Thickness(20, 10, 22, 10);
        AutomationProperties.SetName(start, "New session");
        var another = Button("Add another project", async (_, _) => await addProject());
        var actions = Row(start, another);
        actions.HorizontalAlignment = HorizontalAlignment.Center;

        return WelcomeHost(560,
            Centered(MascotImage(MoodWorking, 124, "ready to start")),
            WelcomeTitle("Start a session in " + project.Name, 26),
            WelcomeBody("This project has no sessions yet. Its files are untouched; start a conversation when you're ready."),
            actions);
    }

    /// <summary>A new session with no messages: prompt ideas that fill the composer.</summary>
    Grid CodeSessionIntro(Action<string> fill, Action browseFiles)
    {
        var browse = CodeLink("Browse workspace files", (_, _) => browseFiles());
        browse.HorizontalAlignment = HorizontalAlignment.Center;
        return WelcomeHost(520,
            Centered(MascotImage(MoodChecking, 104, "ready to look")),
            WelcomeTitle("What would you like to change?", 26),
            WelcomeBody("Ask about this project or describe a change. Files, changes and previews open beside the conversation when there is something to show."),
            PromptList(fill,
                ("Map this project", "", "Give me a short map of this project: what it does, the main folders, and where to start reading."),
                ("Find a safe first improvement", "", "Look through this project and suggest one small, low-risk improvement. Explain what you would change before editing anything."),
                ("Explain how it builds and tests", "", "How is this project built and tested? List the commands you would use and what each one checks.")),
            browse);
    }

    /// <summary>Empty conversation on the Chat page.</summary>
    Grid ChatWelcome(Action<string> fill)
    {
        return WelcomeHost(520,
            Centered(MascotImage(MoodReady, 132, "ready")),
            WelcomeTitle("Make the next move clear."),
            WelcomeBody("Think a problem through, or attach a document and ask about it. Chat uses the model you choose; conversations are saved on this computer."),
            PromptList(fill,
                ("Think through a design decision", "", "Help me think through a design decision. Here is the situation: "),
                ("Explain an error", "", "Explain this error and its most likely causes:\n\n"),
                ("Review a snippet", "", "Review this code for bugs and unclear naming:\n\n"),
                ("Summarize an attached document", "", "Summarize the attached document in five bullet points.")));
    }
}
