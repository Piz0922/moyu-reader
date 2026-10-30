namespace Moyu.Core;

public sealed class Preferences
{
    public double Left { get; set; } = 180;
    public double Top { get; set; } = 140;
    public double Width { get; set; } = 420;
    public double Height { get; set; } = 600;
    public double FontSize { get; set; } = 16;
    public string FontFamily { get; set; } = "Microsoft YaHei UI";
    public double LineSpacing { get; set; } = 1.65;
    public string Background { get; set; } = "#FFFFFF";
    public string Foreground { get; set; } = "#333333";
    public double BackgroundOpacity { get; set; } = 1;
    public uint HotkeyModifiers { get; set; } = 3; // Alt | Control
    public uint HotkeyKey { get; set; } = 0x20;
    public string LastPage { get; set; } = "shelf";
    public string? LastBookId { get; set; }

    public void Normalize()
    {
        static double Limit(double n, double min, double max, double fallback) =>
            double.IsFinite(n) ? Math.Clamp(n, min, max) : fallback;
        Left = Limit(Left, -100000, 100000, 180);
        Top = Limit(Top, -100000, 100000, 140);
        Width = Limit(Width, 160, 10000, 420);
        Height = Limit(Height, 100, 10000, 600);
        FontSize = Limit(FontSize, 8, 72, 16);
        LineSpacing = Limit(LineSpacing, 1.1, 2.5, 1.65);
        BackgroundOpacity = Limit(BackgroundOpacity, 0, 1, 1);
        if (LastPage != "reader") LastPage = "shelf";
    }
}

public sealed class Book
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Extension { get; set; } = ".txt";
    public string Digest { get; set; } = "";
    public DateTime AddedAt { get; set; } = DateTime.Now;
    public DateTime LastReadAt { get; set; }
    public string Format => Extension.TrimStart('.').ToUpperInvariant();
}

public sealed class ReadingPosition
{
    public int SymbolOffset { get; set; }
    public double LineOffset { get; set; }
    public double Fraction { get; set; }
}

public sealed record ContentPart(string Text, bool Heading = false, byte[]? Image = null);
public sealed record BookContent(string Title, List<ContentPart> Parts);

[Flags]
public enum ResizeEdge { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8 }
public readonly record struct ReaderBounds(double Left, double Top, double Width, double Height, double FontSize);

public static class ReaderGeometry
{
    public static ReaderBounds Resize(ReaderBounds start, ResizeEdge edge, double dx, double dy)
    {
        var left = edge.HasFlag(ResizeEdge.Left);
        var right = edge.HasFlag(ResizeEdge.Right);
        var top = edge.HasFlag(ResizeEdge.Top);
        var bottom = edge.HasFlag(ResizeEdge.Bottom);
        double w = start.Width, h = start.Height, font = start.FontSize;
        if ((left || right) && (top || bottom))
        {
            // Project the pointer onto the diagonal, keeping the opposite corner fixed.
            double sx = left ? -dx : dx, sy = top ? -dy : dy;
            var scale = 1 + (sx * w + sy * h) / (w * w + h * h);
            scale = Math.Clamp(scale, Math.Max(Math.Max(160 / w, 100 / h), 8 / font), 72 / font);
            w *= scale; h *= scale; font *= scale;
        }
        else
        {
            if (left || right) w = Math.Clamp(w + (left ? -dx : dx), 160, 10000);
            if (top || bottom) h = Math.Clamp(h + (top ? -dy : dy), 100, 10000);
        }
        return new(start.Left + (left ? start.Width - w : 0),
            start.Top + (top ? start.Height - h : 0), w, h, font);
    }
}
