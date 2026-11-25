using Mason.Code;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Mason;

public sealed partial class MainWindow
{
    sealed record ReviewDesk(Grid View, Action Refresh, CodeViewerEntry Diff) : IDisposable
    {
        public void Dispose() => Diff.Dispose();
    }

    ReviewDesk BuildReviewDesk(CodeSession owner, Action save, Action changed, Action<string, string> open,
        Action reviewChanged, Func<Task> sendReview, Func<string> modelName)
    {
        var diff = BuildCodeViewer(owner, new CodeTab { Kind = "changes" }, save, changed, open, reviewChanged, sendReview);
        var diffContent = (FrameworkElement)diff.Tab.Content;
        diff.Tab.Content = null;

        var runTitle = Heading("", 25);
        var checks = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var model = Muted("");
        var permissions = Muted("");
        var network = Muted("");
        var comments = Button("Review comments", (_, _) => {});
        var submit = Button("Send review", async (_, _) => await sendReview(), true);
        var inspector = Stack(runTitle, Text(owner.Title, 16), comments, submit, checks, Heading("Model & run", 18), model, permissions, network);
        inspector.Padding = new Thickness(22, 20, 16, 16);
        inspector.Spacing = 14;
        var scroll = CodeScroll(inspector);
        var desk = new Grid { ColumnSpacing = 12 };
        desk.ColumnDefinitions.Add(new() { Width = new GridLength(2, GridUnitType.Star) });
        desk.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        desk.RowDefinitions.Add(new() { Height = GridLength.Auto });
        desk.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var section = new ComboBox { ItemsSource = new[] { "Diff", "Checks and review" }, SelectedIndex = 0 };
        AutomationProperties.SetName(section, "Review pane");
        desk.Children.Add(section);
        Grid.SetRow(diffContent, 1); desk.Children.Add(diffContent);
        Grid.SetRow(scroll, 1); desk.Children.Add(scroll);
        void ArrangeDesk()
        {
            bool narrow = desk.ActualWidth < 840;
            section.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
            desk.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(scroll, narrow ? 0 : 1);
            diffContent.Visibility = narrow && section.SelectedIndex == 1 ? Visibility.Collapsed : Visibility.Visible;
            scroll.Visibility = narrow && section.SelectedIndex == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        desk.SizeChanged += (_, _) => ArrangeDesk();
        section.SelectionChanged += (_, _) => ArrangeDesk();
        CodeAcceptancePlan? displayed = null;
        string? revision = null;
        AcceptanceCheck[] criteria = [];
        AcceptanceResult[] observations = [];
        CodeReviewComment[]? pending = null;
        void Refresh()
        {
            diff.Refresh?.Invoke();
            runTitle.Text = owner.State == "Completed" ? "Finished turn" : owner.State == "Ready" ? "Session review" : owner.State;
            model.Text = "Selected model    " + modelName();
            permissions.Text = "Permissions    " + owner.Permission;
            network.Text = "Network    " + owner.Network;
            var plan = owner.Turns.LastOrDefault(t => t.Role == "user")?.Acceptance;
            if (checks.Content == null || displayed != plan || revision != plan?.VerifiedRevision ||
                !criteria.SequenceEqual(plan?.Checks ?? []) || !observations.SequenceEqual(plan?.Results ?? []))
            {
                checks.Content = plan is { Checks.Count: > 0 } ? CheckEvidence(plan, true, CodeWorkspaceView.Review).Layout
                    : Muted("This turn has no recorded feature checks.");
                displayed = plan; revision = plan?.VerifiedRevision;
                criteria = plan?.Checks.ToArray() ?? []; observations = plan?.Results.ToArray() ?? [];
            }
            if (pending == null || !pending.SequenceEqual(owner.ReviewComments))
            {
                pending = owner.ReviewComments.ToArray();
                SetReviewMenu(comments, owner, reviewChanged);
            }
            submit.Visibility = owner.ReviewComments.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        Refresh();
        return new(desk, Refresh, diff);
    }
}
