using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Moyu.Core;

public static partial class EbookParser
{
    [GeneratedRegex(@"^(第[零〇一二三四五六七八九十百千万两\d]+[章节卷回部篇].{0,70}|chapter\s+\d+.{0,70}|序章|前言|楔子|后记|尾声)$", RegexOptions.IgnoreCase)]
    private static partial Regex HeadingPattern();

    public static BookContent Read(string path, string fallbackTitle) =>
        Path.GetExtension(path).Equals(".epub", StringComparison.OrdinalIgnoreCase)
            ? ReadEpub(path, fallbackTitle) : ReadText(path, fallbackTitle);

    private static BookContent ReadText(string path, string title)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = File.ReadAllBytes(path);
        string text;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0, 0 })) text = Encoding.UTF32.GetString(bytes, 4, bytes.Length - 4);
        else if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff })) text = new UTF32Encoding(true, true).GetString(bytes, 4, bytes.Length - 4);
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) text = Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        else
        {
            try { text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
            catch (DecoderFallbackException) { text = Encoding.GetEncoding(54936).GetString(bytes); }
        }
        var parts = new List<ContentPart>();
        foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var clean = line.Trim();
            if (clean.Length == 0) continue;
            // Bound paragraph size so malformed one-line books cannot stall WPF's text formatter.
            for (var offset = 0; offset < clean.Length;)
            {
                var length = Math.Min(4000, clean.Length - offset);
                if (offset + length < clean.Length && char.IsHighSurrogate(clean[offset + length - 1])) length--;
                var segment = clean.Substring(offset, length);
                parts.Add(new(segment, clean.Length < 100 && HeadingPattern().IsMatch(segment)));
                offset += length;
            }
        }
        if (parts.Count == 0) throw new InvalidDataException("这份 TXT 没有可阅读的文字。");
        return new(title, parts);
    }

    private static XDocument Xml(Stream stream)
    {
        using var source = new StreamReader(stream, Encoding.UTF8, true);
        var xml = Regex.Replace(source.ReadToEnd(), @"&([A-Za-z][A-Za-z0-9]+);", match =>
        {
            if (match.Groups[1].Value is "amp" or "lt" or "gt" or "quot" or "apos") return match.Value;
            var decoded = System.Net.WebUtility.HtmlDecode(match.Value);
            return decoded == match.Value ? match.Value : System.Security.SecurityElement.Escape(decoded)!;
        });
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = 32 * 1024 * 1024
        });
        return XDocument.Load(reader);
    }

    private static string Resolve(string parent, string href)
    {
        href = Uri.UnescapeDataString(href.Split('#')[0].Split('?')[0]).Replace('\\', '/');
        if (href.StartsWith('/') || href.Contains(':')) throw new InvalidDataException("EPUB 包含不受支持的外部资源路径。");
        var parts = new List<string>();
        foreach (var piece in (parent + "/" + href).Split('/'))
        {
            if (piece is "" or ".") continue;
            if (piece == "..")
            {
                if (parts.Count == 0) throw new InvalidDataException("EPUB 资源路径越出书籍范围。");
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(piece);
        }
        return string.Join('/', parts);
    }

    private static BookContent ReadEpub(string path, string fallback)
    {
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Count > 20000 || zip.Entries.Sum(e => e.Length) > 256L * 1024 * 1024)
            throw new InvalidDataException("EPUB 解压后过大，暂时无法导入。");
        ZipArchiveEntry Entry(string name)
        {
            var entry = zip.GetEntry(name) ?? throw new InvalidDataException($"EPUB 缺少资源：{name}");
            if (entry.Length > 32 * 1024 * 1024) throw new InvalidDataException("EPUB 的单个资源超过 32 MB。");
            return entry;
        }
        XDocument Load(string name) { using var s = Entry(name).Open(); return Xml(s); }
        if (zip.GetEntry("META-INF/encryption.xml") is { } encryption)
        {
            using var s = encryption.Open();
            var encryptionDoc = Xml(s);
            var algorithms = encryptionDoc.Descendants().Where(e => e.Name.LocalName == "EncryptionMethod")
                .Select(e => (string?)e.Attribute("Algorithm") ?? "");
            if (algorithms.Any(a => a is not ("http://www.idpf.org/2008/embedding" or "http://ns.adobe.com/pdf/enc#RC")))
                throw new InvalidDataException("不支持带 DRM 加密的 EPUB。");
        }
        var container = Load("META-INF/container.xml");
        var package = container.Descendants().FirstOrDefault(e => e.Name.LocalName == "rootfile")?.Attribute("full-path")?.Value
            ?? throw new InvalidDataException("EPUB 缺少书籍清单。");
        package = Resolve("", package);
        var opf = Load(package);
        if (opf.Descendants().Any(e => (e.Name.LocalName == "meta" &&
             (e.Attribute("property")?.Value == "rendition:layout" && e.Value.Trim() == "pre-paginated" ||
              e.Attribute("name")?.Value == "fixed-layout" && e.Attribute("content")?.Value == "true"))))
            throw new InvalidDataException("第一版暂不支持固定版式 EPUB，请使用普通文字电子书。");
        var title = opf.Descendants().FirstOrDefault(e => e.Name.LocalName == "title")?.Value.Trim();
        var baseDir = package.Contains('/') ? package[..package.LastIndexOf('/')] : "";
        var manifest = opf.Descendants().Where(e => e.Name.LocalName == "item" && e.Attribute("id") is not null)
            .ToDictionary(e => e.Attribute("id")!.Value);
        var parts = new List<ContentPart>();
        foreach (var itemRef in opf.Descendants().Where(e => e.Name.LocalName == "itemref"))
        {
            if (itemRef.Attribute("linear")?.Value == "no") continue;
            if (!manifest.TryGetValue(itemRef.Attribute("idref")?.Value ?? "", out var item)) continue;
            var href = Resolve(baseDir, item.Attribute("href")?.Value ?? "");
            var chapter = Load(href);
            var chapterDir = href.Contains('/') ? href[..href.LastIndexOf('/')] : "";
            var body = chapter.Descendants().FirstOrDefault(e => e.Name.LocalName == "body") ?? chapter.Root!;
            var before = parts.Count;
            void AddImage(XElement element)
            {
                var src = element.Attribute("src")?.Value;
                if (string.IsNullOrWhiteSpace(src)) return;
                if (src.StartsWith("http", StringComparison.OrdinalIgnoreCase) || src.StartsWith("data:")) return;
                using var imageStream = Entry(Resolve(chapterDir, src)).Open();
                using var data = new MemoryStream();
                imageStream.CopyTo(data);
                parts.Add(new(element.Attribute("alt")?.Value ?? "插图", Image: data.ToArray()));
            }
            string Inline(XNode node) => node switch
            {
                XText t => Regex.Replace(t.Value, @"\s+", " "),
                XElement e when e.Name.LocalName == "br" => "\n",
                XElement e when e.Name.LocalName is "script" or "style" or "img" or "rt" or "rp" => "",
                XElement e => string.Concat(e.Nodes().Select(Inline)),
                _ => ""
            };
            void Walk(XNode node)
            {
                if (node is XText text)
                {
                    var value = text.Value.Trim();
                    if (value.Length > 0) parts.Add(new(value));
                    return;
                }
                if (node is not XElement e) return;
                var tag = e.Name.LocalName.ToLowerInvariant();
                if (tag is "script" or "style" or "nav" or "head") return;
                if (tag == "img") { AddImage(e); return; }
                if (tag is "p" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "li" or "pre" or "blockquote")
                {
                    var value = Inline(e).Trim();
                    if (value.Length > 0) parts.Add(new(value, tag[0] == 'h' && tag.Length == 2));
                    foreach (var img in e.Descendants().Where(i => i.Name.LocalName == "img")) AddImage(img);
                }
                else foreach (var child in e.Nodes()) Walk(child);
            }
            foreach (var child in body.Nodes()) Walk(child);
            if (parts.Count > before && !parts.Skip(before).Any(p => p.Heading))
            {
                var name = chapter.Descendants().FirstOrDefault(e => e.Name.LocalName == "title")?.Value.Trim();
                parts.Insert(before, new(string.IsNullOrEmpty(name) ? $"章节 {parts.Count(p => p.Heading) + 1}" : name, true));
            }
        }
        if (!parts.Any(p => p.Image == null && !p.Heading && p.Text.Length > 0))
            throw new InvalidDataException("这份 EPUB 没有可阅读的正文，可能是漫画或固定版式电子书。");
        return new(string.IsNullOrWhiteSpace(title) ? fallback : title, parts);
    }
}
