using System.Data;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
namespace Mason;

public sealed class ResultGrid : Grid
{
    readonly ListView list = new() { SelectionMode = ListViewSelectionMode.Extended };
    readonly StackPanel headers = new() { Orientation = Orientation.Horizontal };
    readonly TextBox search = new() { PlaceholderText = "Find in loaded rows…", Width = 230 };
    readonly TextBlock summary = new() { FontSize = 11, Opacity = .7, Margin = new Thickness(8, 2, 8, 2) };
    readonly TextBlock empty = new() { Text = "No table to show.", Opacity = .55, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly Dictionary<int, double> widths = [];
    readonly Dictionary<int, string> columnFilters = [];
    int sortColumn = -1; bool descending;
    DataTable? Table;
    IReadOnlyList<DataRow> SelectedRows => list.SelectedItems.OfType<RowModel>().Select(x => x.Row).ToList();
    [Microsoft.UI.Xaml.Data.Bindable]
    public sealed class RowModel(DataRow row)
    {
        public DataRow Row { get; } = row; public string[] Values { get; } = row.ItemArray.Select(v => v == DBNull.Value ? "∅" : Convert.ToString(v) ?? "").ToArray();
        // Screen readers announce list items by ToString; give them "Column: value" pairs instead of the type name.
        public override string ToString() => string.Join(", ", Row.Table.Columns.Cast<DataColumn>().Select((c, i) => c.ColumnName + ": " + (i < Values.Length ? Values[i] : "")));
    }
    public ResultGrid()
    {
        RowSpacing = 2;
        RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); RowDefinitions.Add(new() { Height = GridLength.Auto });
        list.ItemContainerStyle = new Style(typeof(ListViewItem)) { Setters = { new Setter(Control.MinHeightProperty, 28d), new Setter(Control.PaddingProperty, new Thickness(0)), new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(2, 2, 2, 4) };
        var clear = new Button { Content = "Clear filters", Padding = new Thickness(10, 6, 10, 6) }; clear.Click += (_, _) => { columnFilters.Clear(); search.Text = ""; Apply(); };
        var copy = new Button { Content = "Copy selection", Padding = new Thickness(10, 6, 10, 6) }; copy.Click += (_, _) => CopySelection();
        tools.Children.Add(search); tools.Children.Add(clear); tools.Children.Add(copy);
        Children.Add(new ScrollViewer { Content=tools,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled });
        AutomationProperties.SetName(search, "Search loaded result rows");
        var head = new ScrollViewer { Content = headers, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled }; SetRow(head, 1); Children.Add(head);
        var body = new ScrollViewer { Content = list, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled }; SetRow(body, 2); Children.Add(body); SetRow(empty, 2); Children.Add(empty);
        body.ViewChanged += (_, _) => head.ChangeView(body.HorizontalOffset, null, null, true); SetRow(summary, 3); Children.Add(summary);
        search.TextChanged += (_, _) => Apply();
        list.RightTapped += (_, e) =>
        {
            if (CellAt(e.OriginalSource as DependencyObject) is not { } cell) return;
            var menu = Fluent.Acrylic(new MenuFlyout());
            void Item(string label, Action action) { var item = new MenuFlyoutItem { Text = label }; item.Click += (_, _) => action(); menu.Items.Add(item); }
            Item("Copy cell", () => ClipboardText(cell.Row[cell.Column] == DBNull.Value ? "NULL" : (string)cell.Row[cell.Column]));
            Item("Filter to this value", () => { columnFilters[cell.Column] = cell.Row[cell.Column] == DBNull.Value ? "@NULL" : "=" + (string)cell.Row[cell.Column]; Apply(); });
            menu.ShowAt(list, e.GetPosition(list)); e.Handled = true;
        };
        var copyKey = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.C, Modifiers = Windows.System.VirtualKeyModifiers.Control }; copyKey.Invoked += (_, e) => { CopySelection(); e.Handled = true; }; list.KeyboardAccelerators.Add(copyKey);
    }
    (DataRow Row, int Column)? CellAt(DependencyObject? element)
    {
        int? column = null;
        while (element != null)
        {
            if (element is TextBlock { Tag: string tag } && int.TryParse(tag, out int index)) column = index;
            if (element is ListViewItem { Content: RowModel row } && column.HasValue) return (row.Row, column.Value);
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }
    public void Show(DataTable data)
    {
        bool sameColumns = Table != null && Table.Columns.Cast<DataColumn>().Select(c => c.ColumnName).SequenceEqual(data.Columns.Cast<DataColumn>().Select(c => c.ColumnName));
        if (!sameColumns) { widths.Clear(); columnFilters.Clear(); sortColumn = -1; search.Text = ""; }
        Table = data; BuildColumns(); Apply();
    }
    void BuildColumns()
    {
        if (Table == null) return; headers.Children.Clear();
        for (int i = 0; i < Table.Columns.Count; i++)
        {
            int col = i; double width = widths.GetValueOrDefault(i, Math.Clamp(Table.Columns[i].ColumnName.Length * 8 + 70, 140, 260)); widths[i] = width;
            var header = new Grid { Width = width, Background = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast ? null : new SolidColorBrush(Windows.UI.Color.FromArgb(24, 119, 125, 142)) };
            var button = new Button { Content = Table.Columns[i].ColumnName + (sortColumn == i ? descending ? " ↓" : " ↑" : ""), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), Padding = new Thickness(12, 6, 12, 6) };
            ToolTipService.SetToolTip(button, "Click to sort · right-click to filter · drag the right edge to resize");
            button.Click += (_, _) => { descending = sortColumn == col && !descending; sortColumn = col; BuildColumns(); Apply(); };
            button.RightTapped += (_, e) => { var input = new TextBox { Header = "Contains (or =exact / @NULL)", Text = columnFilters.GetValueOrDefault(col, ""), MinWidth = 240 }; var apply = new Button { Content = "Apply column filter", Margin = new Thickness(0, 10, 0, 0) }; var panel = new StackPanel(); panel.Children.Add(input); panel.Children.Add(apply); var flyout = Fluent.Acrylic(new Flyout { Content = panel }); apply.Click += (_, _) => { if (input.Text.Length == 0) columnFilters.Remove(col); else columnFilters[col] = input.Text; Apply(); flyout.Hide(); }; flyout.ShowAt(button); e.Handled = true; };
            header.Children.Add(button); var grip = new Thumb { Width = 7, HorizontalAlignment = HorizontalAlignment.Right, Opacity = .2 }; grip.DragDelta += (_, e) => { widths[col] = Math.Clamp(widths[col] + e.HorizontalChange, 70, 800); header.Width = widths[col]; }; grip.DragCompleted += (_, _) => BuildColumns(); header.Children.Add(grip); headers.Children.Add(header);
        }
        string cells = string.Join("", Enumerable.Range(0, Table.Columns.Count).Select(i => $"<TextBlock Tag=\"{i}\" Text=\"{{Binding Values[{i}]}}\" Width=\"{widths[i].ToString(CultureInfo.InvariantCulture)}\" Padding=\"12,5\" FontFamily=\"Cascadia Mono, Consolas\" FontSize=\"12\" TextTrimming=\"CharacterEllipsis\" MaxLines=\"1\"/>"));
        list.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><StackPanel Orientation=\"Horizontal\">" + cells + "</StackPanel></DataTemplate>"); list.MinWidth = widths.Values.Sum();
    }
    void Apply()
    {
        if (Table == null) return; IEnumerable<DataRow> rows = Table.Rows.Cast<DataRow>(); string find = search.Text.Trim();
        if (find.Length > 0) rows = rows.Where(r => r.ItemArray.Any(v => v != DBNull.Value && Convert.ToString(v)!.Contains(find, StringComparison.OrdinalIgnoreCase)));
        foreach (var (col, filter) in columnFilters) rows = rows.Where(r => filter == "@NULL" ? r[col] == DBNull.Value : r[col] != DBNull.Value && (filter.StartsWith('=') ? (string)r[col] == filter[1..] : ((string)r[col]).Contains(filter, StringComparison.OrdinalIgnoreCase)));
        if (sortColumn >= 0) { var comparer = Comparer<object>.Create(Compare); rows = descending ? rows.OrderByDescending(r => r[sortColumn], comparer) : rows.OrderBy(r => r[sortColumn], comparer); }
        var materialized = rows.Select(r => new RowModel(r)).ToList(); list.ItemsSource = materialized;
        empty.Text = Table.Rows.Count == 0 ? "This table has no rows." : "No loaded rows match these filters."; empty.Visibility = materialized.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        summary.Text = $"{materialized.Count:N0} of {Table.Rows.Count:N0} loaded rows  ·  {Table.Columns.Count} columns  ·  ∅ is empty  ·  click headers to sort  ·  Ctrl+C copies selected rows";
    }
    public static int Compare(object left, object right)
    {
        if (left == DBNull.Value) return right == DBNull.Value ? 0 : -1; if (right == DBNull.Value) return 1; string a = Convert.ToString(left)!, b = Convert.ToString(right)!;
        if (decimal.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) && decimal.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) return x.CompareTo(y); return StringComparer.OrdinalIgnoreCase.Compare(a, b);
    }
    static void ClipboardText(string text) { var data = new DataPackage(); data.SetText(text); Clipboard.SetContent(data); }
    void CopySelection() { if (Table == null || SelectedRows.Count == 0) return; ClipboardText(string.Join('\t', Table.Columns.Cast<DataColumn>().Select(c => c.ColumnName)) + "\r\n" + string.Join("\r\n", SelectedRows.Select(r => string.Join('\t', r.ItemArray.Select(v => v == DBNull.Value ? "NULL" : Convert.ToString(v)))))); }
}


