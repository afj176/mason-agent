using Mason.Code;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Mason;

public sealed partial class MainWindow
{
    async Task EditReviewComment(CodeSession session, CodeReviewComment comment, bool isNew, Action changed)
    {
        if (dialogOpen) return;
        dialogOpen = true;
        try
        {
            var scope = new CodeWorkspace(session.Workspace);
            CodeFile? file = null;
            string issue = "";
            try { file = scope.Read(comment.Path); }
            catch (Exception error) { issue = error.Message; }
            bool stale = file?.Sha256 != comment.Revision;
            var note = new TextBox { Text = comment.Note, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                MinHeight = 74, MaxHeight = 180, MaxLength = 16000, Header = "Feedback for Mason" };
            var line = new NumberBox { Header = "Current line", Minimum = 1, Value = double.NaN,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                Visibility = stale && file != null ? Visibility.Visible : Visibility.Collapsed };
            var quote = Text(comment.Quote, 13); quote.MaxLines = 4; quote.TextWrapping = TextWrapping.Wrap;
            var problem = CodeNote(stale ? "This file changed. Choose the current line for this comment. " + issue : "");
            problem.TextWrapping = TextWrapping.Wrap;
            var content = Stack(Text(comment.Location, 15), line, quote, note, problem);
            void UpdateLine()
            {
                if (!stale || file == null) return;
                try { quote.Text = comment.OnCurrentLine(file, checked((int)line.Value)).Quote; problem.Text = "This comment will use the current revision."; }
                catch (Exception error) { quote.Text = ""; problem.Text = error.Message; }
            }
            line.ValueChanged += (_, _) => UpdateLine();
            var dialog = new ContentDialog { XamlRoot = root.XamlRoot, RequestedTheme = root.ActualTheme,
                Title = isNew ? "Comment on diff" : "Edit review comment", Content = content,
                PrimaryButtonText = isNew ? "Add to review" : "Save comment", CloseButtonText = "Cancel",
                SecondaryButtonText = isNew ? "" : "Remove comment" };
            dialog.PrimaryButtonClick += (_, e) =>
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(note.Text)) throw new ArgumentException("Enter your feedback first.");
                    var next = comment with { Note = note.Text.Trim() };
                    if (stale)
                    {
                        if (file == null) throw new InvalidOperationException("The file must be available to select its current line.");
                        if (!double.IsFinite(line.Value) || line.Value != Math.Truncate(line.Value)) throw new ArgumentException("Choose a current line number.");
                        next = next.OnCurrentLine(file, checked((int)line.Value));
                    }
                    CodeReviewComment.Validate(scope, [next]);
                    var pending = session.ReviewComments.ToList();
                    if (isNew) pending.Add(next); else pending[pending.FindIndex(c => c.Id == next.Id)] = next;
                    SaveReview(pending);
                }
                catch (Exception error) { e.Cancel = true; problem.Text = error.Message; }
            };
            dialog.SecondaryButtonClick += (_, e) =>
            {
                try { SaveReview(session.ReviewComments.Where(c => c.Id != comment.Id).ToList()); }
                catch (Exception error) { e.Cancel = true; problem.Text = error.Message; }
            };
            void SaveReview(List<CodeReviewComment> next) => SaveReviewDrafts(session, next, changed);
            dialog.Opened += (_, _) => note.Focus(FocusState.Programmatic);
            await dialog.ShowAsync();
        }
        finally { dialogOpen = false; }
    }

    void SaveReviewDrafts(CodeSession session, List<CodeReviewComment> next, Action changed)
    {
        var previous = session.ReviewComments;
        session.ReviewComments = next;
        try { codeStore!.Save(session); }
        catch { session.ReviewComments = previous; throw; }
        changed();
    }

    void OpenLineComment(FrameworkElement anchor, CodeSession session, CodeReviewComment comment, Action changed, Func<Task> sendReview)
    {
        var note = new TextBox { Header = "Draft comment", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MinHeight = 64, MaxHeight = 150, MaxLength = 16000, PlaceholderText = "Leave feedback on this line…" };
        var feedback = Muted(comment.Location);
        var flyout = new Flyout();
        bool finished = false;
        bool SaveNote()
        {
            if (string.IsNullOrWhiteSpace(note.Text)) return false;
            try
            {
                SaveReviewDrafts(session, [..session.ReviewComments, comment with { Note = note.Text.Trim() }], changed);
                finished = true;
                return true;
            }
            catch (Exception e) { feedback.Text = e.Message; return false; }
        }
        var add = Button("Add to review", (_, _) => { if (SaveNote()) flyout.Hide(); });
        var send = Button("Send review", async (_, _) => { if (!SaveNote()) return; flyout.Hide(); await sendReview(); }, true);
        var cancel = CodeLink("Cancel", (_, _) => { finished = true; flyout.Hide(); });
        var body = Stack(Row(MascotImage("mark-this-line", 48, "marking this line"), feedback), note, Row(cancel, add, send));
        body.MaxWidth = Math.Clamp(anchor.XamlRoot.Size.Width - 72, 230, 370);
        flyout.Content = body;
        add.IsEnabled = send.IsEnabled = false;
        note.TextChanged += (_, _) => add.IsEnabled = send.IsEnabled = !string.IsNullOrWhiteSpace(note.Text);
        flyout.Closing += (_, e) =>
        {
            if (!finished && !string.IsNullOrWhiteSpace(note.Text) && !SaveNote()) e.Cancel = true;
        };
        flyout.Opened += (_, _) => note.Focus(FocusState.Programmatic);
        flyout.ShowAt(anchor);
    }

    void SetReviewMenu(Button button, CodeSession? session, Action changed)
    {
        var pending = session?.ReviewComments;
        button.Visibility = pending?.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (session == null || pending == null || pending.Count == 0) { button.Flyout = null; return; }
        button.Content = $"Review comments ({pending.Count})";
        AutomationProperties.SetName(button, $"{pending.Count} review {(pending.Count == 1 ? "comment" : "comments")} attached to the next message");
        var menu = new MenuFlyout();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            var scope = new CodeWorkspace(session.Workspace);
            foreach (var comment in session.ReviewComments)
            {
                bool stale;
                try { stale = scope.Read(comment.Path).Sha256 != comment.Revision; } catch { stale = true; }
                var item = new MenuFlyoutItem { Text = (stale ? "Choose current line · " : "") + comment.Location + " · " + comment.Note };
                item.Click += async (_, _) => await EditReviewComment(session, comment, false, changed);
                menu.Items.Add(item);
            }
        };
        button.Flyout = menu;
    }
}
