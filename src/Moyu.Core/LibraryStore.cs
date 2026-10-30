using System.Security.Cryptography;
using System.Text.Json;

namespace Moyu.Core;

public sealed class LibraryStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public string Root { get; }
    public Preferences Preferences { get; }
    public List<Book> Books { get; }
    public Dictionary<string, ReadingPosition> Positions { get; }
    public List<string> RecoveryNotices { get; } = [];

    public LibraryStore(string programDirectory)
    {
        Root = Path.Combine(Path.GetFullPath(programDirectory), "resources");
        try
        {
            foreach (var name in new[] { "books", "config", "progress" }) Directory.CreateDirectory(Path.Combine(Root, name));
            var probe = Path.Combine(Root, $"write-check-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "write check");
            File.Delete(probe);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new IOException("无法写入程序旁的 resources 资源目录。请把整个程序文件夹移到有写入权限的位置后重试。没有改用其他保存位置。", e);
        }
        Preferences = Read<Preferences>("config/settings.json") ?? new();
        Preferences.Normalize();
        Books = Read<List<Book>>("config/library.json") ?? [];
        Positions = Read<Dictionary<string, ReadingPosition>>("progress/positions.json") ?? [];
        foreach (var book in Books) _ = BookPath(book); // Validate persisted identities before any file operation.
    }

    private T? Read<T>(string relative)
    {
        var path = Path.Combine(Root, relative);
        if (!File.Exists(path)) return default;
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? throw new JsonException("记录为空"); }
        catch (JsonException)
        {
            if (!File.Exists(path + ".bak")) throw new InvalidDataException($"数据文件损坏：{path}。已保留原文件，请先备份资源目录。");
            try
            {
                var result = JsonSerializer.Deserialize<T>(File.ReadAllText(path + ".bak")) ?? throw new JsonException();
                File.Copy(path, path + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}", true);
                File.Copy(path + ".bak", path, true);
                RecoveryNotices.Add($"{Path.GetFileName(path)} 已从上一次备份恢复，损坏文件已保留。");
                return result;
            }
            catch (JsonException e) { throw new InvalidDataException($"数据及备份均损坏：{path}。未重置书库。", e); }
        }
    }

    private void Write<T>(string relative, T value)
    {
        var path = Path.Combine(Root, relative);
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, Json);
            stream.Flush(true);
        }
        if (File.Exists(path)) File.Replace(temp, path, path + ".bak");
        else File.Move(temp, path);
    }

    public void Save()
    {
        Write("config/settings.json", Preferences);
        Write("config/library.json", Books);
        Write("progress/positions.json", Positions);
    }

    public string BookPath(Book book)
    {
        if (!Guid.TryParseExact(book.Id, "N", out _) || book.Extension is not (".txt" or ".epub"))
            throw new InvalidDataException("书库记录包含无效文件标识。未访问资源目录外的文件。");
        return Path.Combine(Root, "books", book.Id, "book" + book.Extension);
    }

    public Book Import(string original)
    {
        var ext = Path.GetExtension(original).ToLowerInvariant();
        if (ext is not (".txt" or ".epub")) throw new InvalidDataException("请选择 TXT 或 EPUB 电子书。");
        var info = new FileInfo(original);
        if (info.Length > 128L * 1024 * 1024) throw new InvalidDataException("这本书超过 128 MB，暂时无法导入。");
        using var source = File.OpenRead(original);
        var digest = Convert.ToHexString(SHA256.HashData(source));
        var existing = Books.FirstOrDefault(b => b.Digest == digest);
        if (existing is not null) return existing;
        source.Position = 0;
        var book = new Book { Extension = ext, Digest = digest, Title = Path.GetFileNameWithoutExtension(original) };
        var destination = BookPath(book);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            using (var target = File.Create(destination)) source.CopyTo(target);
            var content = EbookParser.Read(destination, book.Title);
            book.Title = content.Title;
            Books.Add(book);
            try { Save(); }
            catch { Books.Remove(book); throw; }
            return book;
        }
        catch
        {
            Directory.Delete(Path.GetDirectoryName(destination)!, true);
            throw;
        }
    }

    public void Remove(Book book)
    {
        var path = BookPath(book);
        var index = Books.IndexOf(book);
        if (index < 0) return;
        Positions.TryGetValue(book.Id, out var position);
        var last = Preferences.LastBookId;
        var lastPage = Preferences.LastPage;
        Books.RemoveAt(index);
        Positions.Remove(book.Id);
        if (last == book.Id) { Preferences.LastBookId = null; Preferences.LastPage = "shelf"; }
        try { Save(); }
        catch
        {
            Books.Insert(index, book);
            if (position is not null) Positions[book.Id] = position;
            Preferences.LastBookId = last;
            Preferences.LastPage = lastPage;
            throw;
        }
        // This path is derived solely from a validated generated ID, never from the user's source path.
        if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true);
    }
}
