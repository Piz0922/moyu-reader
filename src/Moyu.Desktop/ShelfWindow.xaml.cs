namespace Moyu.Desktop;

public sealed record BookRow(Book Book, string Title, string Format, string Detail);

public partial class ShelfWindow : Window
{
    private readonly App app;
    public ShelfWindow(App app)
    {
        this.app = app;
        InitializeComponent();
        Closing += (_, e) => { if (!app.IsExiting) { e.Cancel = true; app.HideCurrent(); } };
        DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        Drop += async (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) await app.ImportFiles(paths); };
        Refresh();
    }

    public void Refresh()
    {
        if (BooksList is null) return;
        var query = SearchBox.Text.Trim();
        var rows = app.Store.Books.Where(b => b.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(b => b.LastReadAt).ThenByDescending(b => b.AddedAt)
            .Select(b => new BookRow(b, b.Title, b.Format,
                app.Store.Positions.TryGetValue(b.Id, out var pos)
                    ? $"已读 {pos.Fraction:P0}   ·   {b.LastReadAt:MM月dd日 HH:mm}"
                    : "尚未开始")).ToList();
        BooksList.ItemsSource = rows;
        EmptyState.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Text = query.Length == 0 ? "把 TXT 或 EPUB 拖到这里，或点击右上角导入。" : "没有找到匹配的书名。";
        StatusText.Text = $"共 {app.Store.Books.Count} 本书   ·   书籍和进度只保存在本机";
    }

    public void SetBusy(bool busy, string message = "")
    {
        ImportButton.IsEnabled = !busy; BooksList.IsEnabled = !busy;
        if (busy) StatusText.Text = message; else Refresh();
    }
    private void SearchChanged(object sender, TextChangedEventArgs e) => Refresh();
    private async void ImportClick(object sender, RoutedEventArgs e) => await app.PickFiles();
    private void SettingsClick(object sender, RoutedEventArgs e) => app.ShowSettings();
    private async void ReadClick(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { DataContext: BookRow row }) await app.OpenBook(row.Book); }
    private async void BookDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject initialSource)
        {
            DependencyObject? source = initialSource;
            while (source != null)
            {
                if (source is Button) return;
                source = source is Visual ? VisualTreeHelper.GetParent(source) : null;
            }
        }
        if (BooksList.SelectedItem is BookRow row) await app.OpenBook(row.Book);
    }
    private void RemoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: BookRow row }) return;
        if (MessageBox.Show(this, $"从书架移除《{row.Title}》？\n\n将清理资源目录中的副本和阅读进度。你导入时的原文件会保留。", "移除书籍", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        app.RemoveBook(row.Book);
    }
}
