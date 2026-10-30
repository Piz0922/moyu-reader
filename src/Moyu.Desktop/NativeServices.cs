using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Moyu.Desktop;

internal sealed class GlobalHotkey : IDisposable
{
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    private readonly HwndSource source;
    private int activeId;
    public event Action? Pressed;
    public GlobalHotkey()
    {
        source = new HwndSource(new HwndSourceParameters("MoyuHotkey") { ParentWindow = new IntPtr(-3), WindowStyle = 0, Width = 0, Height = 0 });
        source.AddHook((IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg == 0x312 && w.ToInt32() == activeId) { handled = true; Pressed?.Invoke(); }
            return IntPtr.Zero;
        });
    }
    public bool Set(uint modifiers, uint key)
    {
        var next = activeId == 1 ? 2 : 1;
        if (!RegisterHotKey(source.Handle, next, modifiers | 0x4000, key)) return false;
        if (activeId != 0) UnregisterHotKey(source.Handle, activeId);
        activeId = next;
        return true;
    }
    public void Dispose() { if (activeId != 0) UnregisterHotKey(source.Handle, activeId); source.Dispose(); }
}

internal static class ScreenPlacement
{
    public static void EnsureVisible(Window window, bool center = false)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var rect = new System.Drawing.Rectangle((int)(window.Left * dpi.DpiScaleX), (int)(window.Top * dpi.DpiScaleY),
            (int)(window.Width * dpi.DpiScaleX), (int)(window.Height * dpi.DpiScaleY));
        var screen = System.Windows.Forms.Screen.FromRectangle(rect);
        var area = screen.WorkingArea;
        bool visible = System.Windows.Forms.Screen.AllScreens.Any(s =>
            System.Drawing.Rectangle.Intersect(rect, s.WorkingArea) is var intersection && intersection.Width >= 80 && intersection.Height >= 60);
        if (visible && !center) return;
        if (center) area = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        window.Left = area.Left / dpi.DpiScaleX + Math.Max(0, (area.Width / dpi.DpiScaleX - window.Width) / 2);
        window.Top = area.Top / dpi.DpiScaleY + Math.Max(0, (area.Height / dpi.DpiScaleY - window.Height) / 2);
    }
}
