using System.IO.Compression;
using System.Text;
using Moyu.Core;

var root = Path.Combine(Path.GetTempPath(), "moyu-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed = 0;
void Test(string name, Action run)
{
    try { run(); Console.WriteLine("PASS " + name); passed++; }
    catch (Exception e) { Console.Error.WriteLine("FAIL " + name + "\n" + e); Environment.ExitCode = 1; }
}
void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
string Fixture(string name, string text, Encoding? encoding = null)
{
    var path = Path.Combine(root, name); File.WriteAllText(path, text, encoding ?? new UTF8Encoding(false)); return path;
}
string Epub(string name, bool fixedLayout = false, bool traversal = false, bool encrypted = false)
{
    var path = Path.Combine(root, name);
    using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
    void Entry(string location, string text) { using var writer = new StreamWriter(zip.CreateEntry(location).Open()); writer.Write(text); }
    Entry("mimetype", "application/epub+zip");
    Entry("META-INF/container.xml", "<container><rootfiles><rootfile full-path='OPS/book.opf'/></rootfiles></container>");
    Entry("OPS/book.opf", $"<package><metadata><title>测试电子书</title>{(fixedLayout ? "<meta property='rendition:layout'>pre-paginated</meta>" : "")}</metadata><manifest><item id='b' href='b.xhtml'/><item id='a' href='chapters/a.xhtml'/></manifest><spine><itemref idref='a'/><itemref idref='b'/></spine></package>");
    Entry("OPS/chapters/a.xhtml", "<!DOCTYPE html SYSTEM 'http://invalid.example/external.dtd'><html><head><title>开篇</title></head><body><h1>第一章 起风</h1><p>第一段&nbsp;正文<br/>下一行</p><script>不应出现的脚本</script><p><img src='" + (traversal ? "../../../outside.png" : "../images/p.png") + "' alt='测试插图'/></p></body></html>");
    Entry("OPS/b.xhtml", "<html><head><title>第二章</title></head><body><p>这是第二章的内容。</p></body></html>");
    using (var image = zip.CreateEntry("OPS/images/p.png").Open())
    {
        image.Write(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l9sAAAAASUVORK5CYII="));
    }
    if (encrypted) Entry("META-INF/encryption.xml", "<encryption><EncryptionMethod Algorithm='http://www.w3.org/2001/04/xmlenc#aes128-cbc'/></encryption>");
    return path;
}

Test("UTF-8 / BOM / GB18030 / UTF-16 and chapter detection", () =>
{
    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    var encodings = new Encoding[] { new UTF8Encoding(false), new UTF8Encoding(true), Encoding.GetEncoding(54936), Encoding.Unicode, Encoding.BigEndianUnicode };
    foreach (var encoding in encodings)
    {
        var file = Fixture("encoding-" + Guid.NewGuid() + ".txt", "第一章 起风\r\n\r\n窗外的风，慢慢地吹。\r\nChapter 2 A new day\r\n新的正文。", encoding);
        var content = EbookParser.Read(file, "书名");
        Assert(content.Parts.Count == 4 && content.Parts.Count(p => p.Heading) == 2);
        Assert(content.Parts[1].Text == "窗外的风，慢慢地吹。");
    }
});
Test("Empty TXT rejected without creating a library record", () =>
{
    var store = new LibraryStore(Path.Combine(root, "empty"));
    Throws<InvalidDataException>(() => store.Import(Fixture("empty.txt", "\n \n")));
    Assert(store.Books.Count == 0 && !Directory.EnumerateDirectories(Path.Combine(store.Root, "books")).Any());
});
Test("Long single paragraph retains Unicode surrogate pairs", () =>
{
    var text = new string('字', 3999) + "😀" + new string('文', 6000);
    var content = EbookParser.Read(Fixture("long.txt", text), "长文");
    Assert(string.Concat(content.Parts.Select(p => p.Text)) == text);
    Assert(content.Parts.All(p => !char.IsHighSurrogate(p.Text[^1])));
});
Test("EPUB spine order, entities, chapters and embedded images", () =>
{
    var content = EbookParser.Read(Epub("normal.epub"), "备用");
    Assert(content.Title == "测试电子书");
    Assert(content.Parts[0].Text == "第一章 起风");
    Assert(content.Parts[1].Text.Contains("第一段") && content.Parts[1].Text.Contains("下一行"));
    Assert(content.Parts.Count(p => p.Heading) == 2);
    Assert(content.Parts.Count(p => p.Image != null) == 1);
    Assert(content.Parts.Last().Text == "这是第二章的内容。");
    Assert(!content.Parts.Any(p => p.Text.Contains("不应出现")));
});
Test("Fixed layout / DRM / path traversal rejected", () =>
{
    Throws<InvalidDataException>(() => EbookParser.Read(Epub("fixed.epub", fixedLayout: true), ""));
    Throws<InvalidDataException>(() => EbookParser.Read(Epub("drm.epub", encrypted: true), ""));
    Throws<InvalidDataException>(() => EbookParser.Read(Epub("traversal.epub", traversal: true), ""));
});
Test("Import copies and deduplicates; deleting original leaves readable copy", () =>
{
    var store = new LibraryStore(Path.Combine(root, "independent"));
    var original = Fixture("original.txt", "第一章 起风\n用户的原始电子书内容。");
    var book = store.Import(original);
    var originalBytes = File.ReadAllBytes(original);
    Assert(store.Import(original).Id == book.Id && store.Books.Count == 1);
    File.Delete(original);
    Assert(File.ReadAllBytes(store.BookPath(book)).SequenceEqual(originalBytes));
    Assert(EbookParser.Read(store.BookPath(book), book.Title).Parts.Count == 2);
});
Test("Removal never deletes the original and cannot escape library", () =>
{
    var store = new LibraryStore(Path.Combine(root, "removal"));
    var original = Fixture("keep-original.txt", "必须保留的原文件。");
    var bytes = File.ReadAllBytes(original);
    var book = store.Import(original); var copy = store.BookPath(book);
    store.Positions[book.Id] = new() { SymbolOffset = 3 };
    store.Remove(book);
    Assert(File.Exists(original) && File.ReadAllBytes(original).SequenceEqual(bytes));
    Assert(!File.Exists(copy) && store.Books.Count == 0 && store.Positions.Count == 0);
    Throws<InvalidDataException>(() => store.BookPath(new Book { Id = "../../outside", Extension = ".txt" }));
});
Test("Moving entire portable folder retains books, settings and positions", () =>
{
    var program = Path.Combine(root, "portable-before");
    var store = new LibraryStore(program);
    var book = store.Import(Fixture("portable.txt", "第一章\n跨目录的阅读记录。"));
    store.Preferences.FontSize = 27; store.Preferences.LastBookId = book.Id; store.Preferences.LastPage = "reader";
    store.Positions[book.Id] = new() { SymbolOffset = 18, Fraction = .45, LineOffset = 12 }; store.Save();
    var moved = Path.Combine(root, "portable-after"); Directory.Move(program, moved);
    var reopened = new LibraryStore(moved);
    Assert(reopened.Books.Single().Id == book.Id && File.Exists(reopened.BookPath(book)));
    Assert(reopened.Preferences.FontSize == 27 && reopened.Preferences.LastPage == "reader");
    Assert(reopened.Positions[book.Id].SymbolOffset == 18);
});
Test("Corrupt settings recover backup without silently discarding data", () =>
{
    var program = Path.Combine(root, "backup"); var store = new LibraryStore(program);
    store.Preferences.FontSize = 22; store.Save(); store.Preferences.FontSize = 24; store.Save();
    File.WriteAllText(Path.Combine(store.Root, "config/settings.json"), "not json");
    var recovered = new LibraryStore(program);
    Assert(recovered.Preferences.FontSize == 22 && recovered.RecoveryNotices.Count == 1);
    Assert(Directory.EnumerateFiles(Path.Combine(store.Root, "config"), "*.corrupt-*").Count() == 1);
});
Test("Unwritable resource location is explicit, no fallback", () =>
{
    var program = Path.Combine(root, "blocked"); Directory.CreateDirectory(program);
    File.WriteAllText(Path.Combine(program, "resources"), "file blocks directory");
    Throws<IOException>(() => new LibraryStore(program));
});
Test("Four corner scaling keeps opposite corner and all ratios", () =>
{
    var start = new ReaderBounds(100, 200, 400, 600, 16);
    foreach (var edge in new[] { ResizeEdge.Left | ResizeEdge.Top, ResizeEdge.Right | ResizeEdge.Top, ResizeEdge.Left | ResizeEdge.Bottom, ResizeEdge.Right | ResizeEdge.Bottom })
    {
        var left = edge.HasFlag(ResizeEdge.Left); var top = edge.HasFlag(ResizeEdge.Top);
        var end = ReaderGeometry.Resize(start, edge, left ? -200 : 200, top ? -300 : 300);
        Assert(Math.Abs(end.Width - 600) < .01 && Math.Abs(end.Height - 900) < .01 && Math.Abs(end.FontSize - 24) < .01);
        Assert(Math.Abs((left ? end.Left + end.Width : end.Left) - (left ? 500 : 100)) < .01);
        Assert(Math.Abs((top ? end.Top + end.Height : end.Top) - (top ? 800 : 200)) < .01);
    }
});
Test("Side stretching keeps font and perpendicular dimension; corner bounds preserve ratio", () =>
{
    var start = new ReaderBounds(0, 0, 400, 600, 16);
    var side = ReaderGeometry.Resize(start, ResizeEdge.Left, 100, 400);
    Assert(side.Left == 100 && side.Width == 300 && side.Height == 600 && side.FontSize == 16);
    var min = ReaderGeometry.Resize(start, ResizeEdge.Left | ResizeEdge.Top, 5000, 5000);
    Assert(min.FontSize == 8 && min.Width == 200 && min.Height == 300);
    var max = ReaderGeometry.Resize(start, ResizeEdge.Right | ResizeEdge.Bottom, 99999, 99999);
    Assert(max.FontSize == 72 && max.Width / max.Height == start.Width / start.Height);
});
Console.WriteLine($"\n{passed} integration checks passed. Fixture directory: {root}");
