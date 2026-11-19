using System.Text.Json;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using System.IO.Compression;
using System.Diagnostics;

namespace Moyu.Desktop;

internal static class SmokeTests
{
    public static async Task Run(App app)
    {
        var checks = new List<string>();
        var report = Path.Combine(AppContext.BaseDirectory, "smoke-report.json");
        try
        {
            void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); checks.Add(label); }
            app.Store.Preferences.FontSize = 16;
            app.Reader.Width = 420; app.Reader.Height = 600;
            var fixture = Path.Combine(AppContext.BaseDirectory, "smoke-fixture.txt");
            File.WriteAllText(fixture, string.Join("\n", Enumerable.Range(1, 240).Select(i => i % 30 == 1
                ? $"第{i}章 测试章节" : $"第{i}段：窗外的风吹过树梢，书页仍停在上一次读到的地方。改变字号和宽度以后，也应该能继续读到这里。这是一段用于检查排版、滚动与阅读位置保持的测试文字。")));
            var book = app.Store.Import(fixture);
            app.Store.Positions.Remove(book.Id);
            await app.OpenBook(book);
            await Pause();
            Capture(app.Reader, "reader.png");
            Check(app.Reader.IsVisible && !app.Shelf.IsVisible, "阅读页显示时书架隐藏");
            Check(app.Reader.ContextMenu!.Items.OfType<MenuItem>().Any(i => Equals(i.Header, "书架")), "正文右键菜单有书架入口");
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
            app.Reader.RaiseEvent(wheel);
            await Pause();
            Check(wheel.Handled && app.Reader.ReadingOffset > 0, "实际 WPF 滚轮事件由阅读区处理");
            Check(app.Reader.ChapterCount == 8, "TXT 章节识别与目录");
            app.Reader.RestorePosition(new ReadingPosition { SymbolOffset = 9000, LineOffset = 12 });
            await Pause();
            var before = app.Reader.GetPosition();
            Check(before.SymbolOffset > 8000 && app.Reader.ReadingOffset > 100, "长正文连续滚动与定位");
            app.Store.Preferences.FontSize = 23;
            app.Reader.ApplyAppearance();
            await Pause();
            var after = app.Reader.GetPosition();
            Check(Math.Abs(after.SymbolOffset - before.SymbolOffset) < 120, "改变字号后保持正文锚点");
            app.Reader.Width = 320;
            app.Reader.RestorePosition(after);
            await Pause();
            Check(Math.Abs(app.Reader.GetPosition().SymbolOffset - after.SymbolOffset) < 120, "改变宽度后保持正文锚点");
            var reader = app.Reader;
            var body = (RichTextBox)((Decorator)reader.Content).Child;
            foreach (var edge in new[] { ResizeEdge.Right, ResizeEdge.Left | ResizeEdge.Top })
            {
                var anchor = reader.GetPosition();
                var original = new ReaderBounds(reader.Left, reader.Top, reader.Width, reader.Height, body.Document.FontSize);
                var size = body.RenderSize;
                var bounds = ReaderGeometry.Resize(original, edge, 48, 36);
                // Exercise the same gesture path, including the real capture-loss notification.
                reader.CaptureMouse();
                reader.BeginDrag(edge, new Point());
                for (var step = 1; step <= 3; step++)
                {
                    reader.UpdateDrag(new Point(16 * step, 12 * step));
                    reader.UpdateLayout();
                    if (body.RenderSize != size || body.Document.FontSize != original.FontSize)
                        throw new InvalidOperationException("拖动预览期间正文提前重排");
                }
                Check(Math.Abs(reader.ActualWidth - bounds.Width) <= 1 && body.RenderTransform.Value.M11 == bounds.FontSize / original.FontSize,
                    $"{edge}：窗口跟随拖动，正文保持原排版");
                await Task.Delay(1150); // Let a pending save run while the pointer is held still.
                app.SaveNow();
                Check(app.Store.Positions[book.Id].SymbolOffset == anchor.SymbolOffset && app.Store.Preferences.Width == original.Width,
                    $"{edge}：停顿时不保存预览尺寸或临时进度");
                if (edge == ResizeEdge.Right) reader.EndDrag(); else reader.ReleaseMouseCapture();
                await Pause();
                Check(body.RenderTransform.Value.IsIdentity && body.CacheMode is null && Math.Abs(body.ActualWidth - reader.ActualWidth) < 1
                    && body.Document.FontSize == bounds.FontSize && Math.Abs(reader.GetPosition().SymbolOffset - anchor.SymbolOffset) < 120,
                    $"{edge}：结束拖动后按最终尺寸排版并恢复阅读位置");
            }
            app.HideCurrent();
            Check(!app.Reader.IsVisible && !app.Shelf.IsVisible, "隐藏无可见页面");
            app.RestoreCurrent();
            Check(app.Reader.IsVisible && !app.Shelf.IsVisible, "恢复阅读页");
            app.ShowShelf();
            await Pause();
            Capture(app.Shelf, "shelf.png");
            Check(app.Shelf.IsVisible && !app.Reader.IsVisible, "书架与正文互斥");
            app.HideCurrent(); app.RestoreCurrent();
            Check(app.Shelf.IsVisible && !app.Reader.IsVisible, "恢复隐藏前书架");
            app.SaveNow();
            var reopened = new LibraryStore(AppContext.BaseDirectory);
            Check(reopened.Positions.TryGetValue(book.Id, out var position) && position.SymbolOffset > 8000, "进度落盘与重新加载");
            var settings = new SettingsWindow(app);
            settings.Show(); await Pause(); Capture(settings, "settings.png"); settings.Close();
            Check(!app.ChangeHotkey(0, 0), "非法快捷键不会覆盖已有注册");
            var epubPath = MakeEpub();
            var epub = app.Store.Import(epubPath);
            await app.OpenBook(epub); await Pause();
            Check(app.Reader.RenderedImageCount == 1 && app.Reader.ChapterCount == 2, "EPUB 正文、目录与插图实际渲染");
            Capture(app.Reader, "epub.png");
            app.ShowShelf(); app.RemoveBook(epub); app.SaveNow();
            Check(File.Exists(epubPath) && !File.Exists(app.Store.BookPath(epub)), "从书架移除只清理副本");
            Check(!app.Store.Positions.ContainsKey(epub.Id), "移除后定时保存不会重新写入旧书进度");
            var largePath = Path.Combine(AppContext.BaseDirectory, "large-fixture.txt");
            File.WriteAllText(largePath, string.Join("\n", Enumerable.Range(1, 6000).Select(i => $"第{i}段 " + string.Concat(Enumerable.Repeat("这是一段用于验证长篇小说排版与滚动性能的文字。", 4)))));
            var large = app.Store.Import(largePath);
            var watch = Stopwatch.StartNew();
            await app.OpenBook(large); await Pause();
            app.Reader.RestorePosition(new ReadingPosition { SymbolOffset = 300000, LineOffset = 12 }); await Pause();
            Check(app.Reader.GetPosition().SymbolOffset > 299800, "约 1.6 MB 长正文能够跳至中段并排版");
            checks.Add($"长书打开与中段定位：{watch.Elapsed.TotalSeconds:0.0} 秒");
            File.WriteAllText(report, JsonSerializer.Serialize(new { success = true, checks }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { success = false, checks, error = e.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
        }
        app.ExitApp();
    }

    private static string MakeEpub()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "smoke-book.epub");
        using var file = new FileStream(path, FileMode.Create);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        void Entry(string name, string text) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(text); }
        Entry("META-INF/container.xml", "<container><rootfiles><rootfile full-path='book.opf'/></rootfiles></container>");
        Entry("book.opf", "<package><metadata><title>午后的几页</title></metadata><manifest><item id='one' href='one.xhtml'/><item id='two' href='two.xhtml'/></manifest><spine><itemref idref='one'/><itemref idref='two'/></spine></package>");
        Entry("one.xhtml", "<html><body><h1>第一章 窗边</h1><p>窗边留着一块阳光，刚好够放下一本书。街道上的声音很远，纸页翻动的声音却很近。</p><p>这块文字可以放在任何地方。按住正文移动，滚轮向下继续阅读。</p><img src='scene.png' alt='窗外的山色'/><p>图片会随着阅读区域的宽度调整，文字会接着排下去。</p></body></html>");
        Entry("two.xhtml", "<html><body><h1>第二章 风起</h1><p>风掠过窗台，树影在墙上缓缓移动。他把读到的那一行记在心里，然后继续向下翻。</p></body></html>");
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(230, 239, 230)), null, new Rect(0, 0, 600, 240));
            drawing.DrawEllipse(new SolidColorBrush(Color.FromRgb(223, 201, 149)), null, new Point(450, 70), 30, 30);
            var mountains = Geometry.Parse("M0,240 L0,190 L150,70 L290,200 L400,120 L600,220 L600,240 Z");
            drawing.DrawGeometry(new SolidColorBrush(Color.FromRgb(117, 152, 132)), null, mountains);
        }
        var bitmap = new RenderTargetBitmap(600, 240, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var memory = new MemoryStream(); png.Save(memory);
        using var imageStream = zip.CreateEntry("scene.png").Open(); memory.WriteTo(imageStream);
        return path;
    }

    private static void Capture(Window window, string name)
    {
        window.UpdateLayout();
        var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (var drawing = backdrop.RenderOpen()) drawing.DrawRectangle(window.Background ?? Brushes.White, null, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
        image.Render(backdrop);
        image.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        var output = Path.Combine(AppContext.BaseDirectory, "validation"); Directory.CreateDirectory(output);
        using var stream = File.Create(Path.Combine(output, name)); encoder.Save(stream);
    }

    private static async Task Pause()
    {
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(250);
    }
}
