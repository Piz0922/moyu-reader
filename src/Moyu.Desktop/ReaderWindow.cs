using System.Windows.Documents;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Moyu.Desktop;

public sealed class ReaderWindow : Window
{
    private readonly App app;
    private readonly RichTextBox text = new();
    private readonly List<(string Title, TextPointer Position)> chapters = [];
    private readonly List<Image> images = [];
    private readonly DispatcherTimer scrollSave = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private Point dragOrigin;
    private ReaderBounds dragBounds;
    private ResizeEdge edge;
    private bool dragging;
    private readonly ResizeViewport viewport = new() { ClipToBounds = true };
    private readonly ScaleTransform previewScale = new();
    private ReaderBounds previewBounds;
    private bool loading;
    private ReadingPosition? resizeAnchor;
    public Book? CurrentBook { get; private set; }
    public double ReadingOffset => text.VerticalOffset;
    public int ChapterCount => chapters.Count;
    public int RenderedImageCount => images.Count;

    public ReaderWindow(App app)
    {
        this.app = app;
        Style = (Style)app.FindResource(typeof(Window));
        Title = "摸鱼阅读器 · 阅读";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        MinWidth = 160; MinHeight = 100;
        var p = app.Store.Preferences;
        Left = p.Left; Top = p.Top; Width = p.Width; Height = p.Height;
        text.IsReadOnly = true;
        text.IsReadOnlyCaretVisible = false;
        text.IsDocumentEnabled = false;
        text.Focusable = false;
        text.IsTabStop = false;
        text.BorderThickness = new Thickness(0);
        text.Padding = new Thickness(14, 10, 14, 12);
        text.Background = Brushes.Transparent;
        text.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        text.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        text.Cursor = Cursors.Arrow;
        text.SelectionBrush = Brushes.Transparent;
        viewport.Child = text;
        Content = viewport;
        text.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) =>
        {
            if (!loading && resizeAnchor is null) { scrollSave.Stop(); scrollSave.Start(); }
        }));
        scrollSave.Tick += (_, _) => { scrollSave.Stop(); CaptureProgress(); app.SaveSoon(); };
        PreviewMouseLeftButtonDown += HandleMouseDown;
        PreviewMouseMove += HandleMouseMove;
        PreviewMouseLeftButtonUp += HandleMouseUp;
        LostMouseCapture += (_, _) => { if (!IsMouseCaptured) EndDrag(); };
        PreviewMouseWheel += Wheel;
        ContextMenu = BuildMenu();
        text.ContextMenu = ContextMenu;
        AllowDrop = true;
        DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        Drop += async (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) await app.ImportFiles(paths); };
        Closing += (_, e) => { if (!app.IsExiting) { e.Cancel = true; app.HideCurrent(); } };
        LocationChanged += (_, _) => { if (!loading && !dragging) { SaveBounds(); app.SaveSoon(); } };
        SizeChanged += (_, e) =>
        {
            if (resizeAnchor is null && e.WidthChanged) UpdateImages(e.NewSize.Width);
            if (!loading && !dragging) { SaveBounds(); app.SaveSoon(); }
        };
        ApplyAppearance(false);
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        void Item(string title, Action action) { var item = new MenuItem { Header = title }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        Item("书架", app.ShowShelf);
        Item("打开文件…", async () => await app.PickFiles());
        Item("目录…", ShowChapters);
        menu.Items.Add(new Separator());
        Item("设置…", app.ShowSettings);
        Item("隐藏", app.HideCurrent);
        return menu;
    }

    public void LoadBook(Book book, BookContent content)
    {
        EndDrag();
        loading = true;
        scrollSave.Stop();
        CurrentBook = book;
        chapters.Clear(); images.Clear();
        var document = new FlowDocument { PagePadding = new Thickness(14, 10, 14, 12), ColumnWidth = double.PositiveInfinity };
        foreach (var part in content.Parts)
        {
            if (part.Image is { } bytes)
            {
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = new MemoryStream(bytes); bitmap.DecodePixelWidth = 1600; bitmap.EndInit(); bitmap.Freeze();
                    var image = new Image { Source = bitmap, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = Math.Max(80, Width - 32) };
                    images.Add(image);
                    document.Blocks.Add(new BlockUIContainer(image) { Margin = new Thickness(0, 8, 0, 12) });
                }
                catch (Exception e) when (e is NotSupportedException or FileFormatException or IOException or ArgumentException)
                { document.Blocks.Add(new Paragraph(new Run($"［{part.Text}：图片格式暂不支持］"))); }
            }
            else
            {
                var paragraph = new Paragraph(new Run(part.Text))
                {
                    Margin = new Thickness(0, part.Heading ? 14 : 0, 0, part.Heading ? 12 : 10),
                    FontWeight = part.Heading ? FontWeights.SemiBold : FontWeights.Normal,
                    KeepTogether = false, KeepWithNext = part.Heading
                };
                document.Blocks.Add(paragraph);
                if (part.Heading) chapters.Add((part.Text, paragraph.ContentStart));
            }
        }
        text.Document = document;
        ApplyAppearance(false);
        text.ScrollToHome();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (app.Store.Positions.TryGetValue(book.Id, out var position)) RestorePosition(position);
            loading = false;
        });
    }

    public ReadingPosition GetPosition()
    {
        var pointer = text.GetPositionFromPoint(new Point(18, 12), true) ?? text.Document.ContentStart;
        var rect = pointer.GetCharacterRect(LogicalDirection.Forward);
        var offset = text.Document.ContentStart.GetOffsetToPosition(pointer);
        var total = text.Document.ContentStart.GetOffsetToPosition(text.Document.ContentEnd);
        return new ReadingPosition
        {
            SymbolOffset = offset, LineOffset = rect.IsEmpty ? 0 : rect.Top,
            Fraction = text.ExtentHeight > text.ViewportHeight && text.VerticalOffset + text.ViewportHeight >= text.ExtentHeight - 2
                ? 1 : Math.Clamp((double)offset / Math.Max(1, total), 0, 1)
        };
    }

    public void RestorePosition(ReadingPosition position)
    {
        text.UpdateLayout();
        var total = text.Document.ContentStart.GetOffsetToPosition(text.Document.ContentEnd);
        var pointer = text.Document.ContentStart.GetPositionAtOffset(Math.Clamp(position.SymbolOffset, 0, total));
        if (pointer is null) return;
        var rect = pointer.GetCharacterRect(LogicalDirection.Forward);
        if (!rect.IsEmpty)
        {
            text.ScrollToVerticalOffset(Math.Max(0, text.VerticalOffset + rect.Top - position.LineOffset));
            text.UpdateLayout();
        }
    }

    public void CaptureProgress()
    {
        if (CurrentBook is null || loading || !IsLoaded || app.IsBusy || !app.Store.Books.Any(b => b.Id == CurrentBook.Id)) return;
        app.Store.Positions[CurrentBook.Id] = resizeAnchor ?? GetPosition();
    }

    public void ApplyAppearance(bool preservePosition = true)
    {
        var anchor = preservePosition && IsLoaded && CurrentBook != null ? GetPosition() : null;
        var p = app.Store.Preferences;
        var bg = (Color)ColorConverter.ConvertFromString(p.Background);
        // Alpha 1 retains mouse hit testing even when the background looks fully transparent.
        bg.A = (byte)Math.Max(1, Math.Round(p.BackgroundOpacity * 255));
        Background = new SolidColorBrush(bg);
        text.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(p.Foreground));
        text.Document.FontFamily = new FontFamily(p.FontFamily);
        text.Document.FontSize = p.FontSize;
        text.Document.LineHeight = p.FontSize * p.LineSpacing;
        if (anchor != null) RestorePosition(anchor);
    }

    private void UpdateImages(double width)
    {
        foreach (var image in images) image.MaxWidth = Math.Max(80, width - 32);
    }

    private ResizeEdge HitEdge(Point point)
    {
        const double border = 7, corner = 17;
        var nearX = point.X < corner || point.X > ActualWidth - corner;
        var nearY = point.Y < corner || point.Y > ActualHeight - corner;
        var xMargin = nearY ? corner : border;
        var yMargin = nearX ? corner : border;
        var result = ResizeEdge.None;
        if (point.X < xMargin) result |= ResizeEdge.Left;
        else if (point.X > ActualWidth - xMargin) result |= ResizeEdge.Right;
        if (point.Y < yMargin) result |= ResizeEdge.Top;
        else if (point.Y > ActualHeight - yMargin) result |= ResizeEdge.Bottom;
        return result;
    }

    private Point ScreenPoint(MouseEventArgs e)
    {
        var pixels = PointToScreen(e.GetPosition(this));
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Point(pixels.X / dpi.DpiScaleX, pixels.Y / dpi.DpiScaleY);
    }

    private void HandleMouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (e.ClickCount == 2) { ReleaseMouseCapture(); app.HideCurrent(); return; }
        if (CaptureMouse()) BeginDrag(HitEdge(e.GetPosition(this)), ScreenPoint(e));
    }

    internal void BeginDrag(ResizeEdge hit, Point origin)
    {
        edge = hit;
        dragOrigin = origin;
        dragBounds = previewBounds = new(Left, Top, Width, Height, app.Store.Preferences.FontSize);
        dragging = true;
        if (edge == ResizeEdge.None) return;
        resizeAnchor = GetPosition();
        scrollSave.Stop();
        viewport.FreezeLayout(text.RenderSize);
        if ((edge & (ResizeEdge.Left | ResizeEdge.Right)) != 0 && (edge & (ResizeEdge.Top | ResizeEdge.Bottom)) != 0)
            text.CacheMode = new BitmapCache();
        text.RenderTransform = previewScale;
    }

    internal void UpdateDrag(Point now)
    {
        if (!dragging) return;
        var dx = now.X - dragOrigin.X; var dy = now.Y - dragOrigin.Y;
        if (edge == ResizeEdge.None)
        {
            if (Math.Abs(dx) < 3 && Math.Abs(dy) < 3) return;
            Left = dragBounds.Left + dx; Top = dragBounds.Top + dy;
            return;
        }
        var bounds = ReaderGeometry.Resize(dragBounds, edge, dx, dy);
        if (bounds == previewBounds) return;
        previewBounds = bounds;
        Left = bounds.Left; Top = bounds.Top; Width = bounds.Width; Height = bounds.Height;
        previewScale.ScaleX = previewScale.ScaleY = bounds.FontSize / dragBounds.FontSize;
    }

    internal void EndDrag()
    {
        if (!dragging) return;
        dragging = false;
        if (resizeAnchor is { } anchor)
        {
            text.CacheMode = null;
            text.RenderTransform = Transform.Identity;
            previewScale.ScaleX = previewScale.ScaleY = 1;
            viewport.ResumeLayout();
            if (previewBounds.FontSize != dragBounds.FontSize)
            {
                var p = app.Store.Preferences;
                text.Document.FontSize = p.FontSize = previewBounds.FontSize;
                text.Document.LineHeight = p.FontSize * p.LineSpacing;
            }
            if (previewBounds.Width != dragBounds.Width) UpdateImages(Width);
            if (previewBounds != dragBounds) RestorePosition(anchor);
            resizeAnchor = null;
        }
        if (IsMouseCaptured) ReleaseMouseCapture();
        SaveBounds(); app.SaveSoon();
    }

    private void HandleMouseMove(object sender, MouseEventArgs e)
    {
        if (dragging && e.LeftButton == MouseButtonState.Pressed)
        {
            UpdateDrag(ScreenPoint(e));
            e.Handled = true;
            return;
        }
        var hit = HitEdge(e.GetPosition(this));
        Cursor = hit switch
        {
            ResizeEdge.Left or ResizeEdge.Right => Cursors.SizeWE,
            ResizeEdge.Top or ResizeEdge.Bottom => Cursors.SizeNS,
            ResizeEdge.Left | ResizeEdge.Top or ResizeEdge.Right | ResizeEdge.Bottom => Cursors.SizeNWSE,
            ResizeEdge.Left | ResizeEdge.Bottom or ResizeEdge.Right | ResizeEdge.Top => Cursors.SizeNESW,
            _ => Cursors.Arrow
        };
        text.Cursor = Cursor;
    }

    private void HandleMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (dragging) { UpdateDrag(ScreenPoint(e)); EndDrag(); }
        e.Handled = true;
    }

    private void Wheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (resizeAnchor is not null) return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            app.Store.Preferences.FontSize = Math.Clamp(app.Store.Preferences.FontSize + e.Delta / 120.0, 8, 72);
            ApplyAppearance(); app.SaveSoon();
        }
        else text.ScrollToVerticalOffset(text.VerticalOffset - e.Delta / 120.0 * app.Store.Preferences.FontSize * 3);
    }

    public void SaveBounds()
    {
        if (resizeAnchor is not null) return;
        var p = app.Store.Preferences;
        p.Left = Left; p.Top = Top; p.Width = Width; p.Height = Height;
    }

    public void ShowChapters()
    {
        var list = new ListBox { Margin = new Thickness(18), BorderThickness = new Thickness(0) };
        foreach (var chapter in chapters) list.Items.Add(new ListBoxItem { Content = chapter.Title, Padding = new Thickness(8, 10, 8, 10), Tag = chapter.Position });
        var window = new Window { Title = "目录", Width = 420, Height = 520, MinWidth = 300, MinHeight = 250,
            Owner = this, Topmost = true, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = chapters.Count == 0 ? new TextBlock { Text = "这本书没有识别到章节标题。", Margin = new Thickness(24) } : list };
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is ListBoxItem { Tag: TextPointer pointer })
            {
                RestorePosition(new ReadingPosition { SymbolOffset = text.Document.ContentStart.GetOffsetToPosition(pointer), LineOffset = 10 });
                CaptureProgress(); app.SaveSoon(); window.Close();
            }
        };
        window.ShowDialog();
    }
}
