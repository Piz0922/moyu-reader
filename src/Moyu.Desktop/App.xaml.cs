using System.Threading;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Moyu.Desktop;

public partial class App : Application
{
    public LibraryStore Store { get; private set; } = null!;
    public ReaderWindow Reader { get; private set; } = null!;
    public ShelfWindow Shelf { get; private set; } = null!;
    public bool IsExiting { get; private set; }
    public string HotkeyWarning { get; private set; } = "";
    private GlobalHotkey hotkey = null!;
    private Forms.NotifyIcon tray = null!;
    private System.Drawing.Icon? trayIcon;
    private SettingsWindow? settings;
    private Mutex? instance;
    private bool ownsMutex;
    private bool busy;
    public bool IsBusy => busy;
    private bool saveErrorShown;
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var identity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(AppContext.BaseDirectory.ToUpperInvariant())))[..20];
            instance = new Mutex(true, "Local\\MoyuReader-" + identity, out ownsMutex);
            if (!ownsMutex) { MessageBox.Show("阅读器已经在运行，请点击右下角托盘图标恢复。", "摸鱼阅读器"); Shutdown(); return; }
            Store = new LibraryStore(AppContext.BaseDirectory);
            Reader = new ReaderWindow(this); Shelf = new ShelfWindow(this); MainWindow = Shelf;
            hotkey = new GlobalHotkey(); hotkey.Pressed += Toggle;
            if (!hotkey.Set(Store.Preferences.HotkeyModifiers, Store.Preferences.HotkeyKey))
                HotkeyWarning = "隐藏快捷键已被其他软件占用。请打开设置更换；现在仍可通过托盘恢复窗口。";
            BuildTray();
            saveTimer.Tick += (_, _) => { saveTimer.Stop(); if (!busy) SafeSave(); };
            SessionEnding += (_, _) => { Reader.CaptureProgress(); SafeSave(); };
            if (e.Args.Contains("--smoke-test")) { await SmokeTests.Run(this); return; }
            var files = e.Args.Where(File.Exists).ToArray();
            if (files.Length > 0) { ShowShelf(); await ImportFiles(files); }
            else if (Store.Preferences.LastPage == "reader" && Store.Books.FirstOrDefault(b => b.Id == Store.Preferences.LastBookId) is { } last)
                await OpenBook(last);
            else ShowShelf();
            foreach (var notice in Store.RecoveryNotices) MessageBox.Show(notice, "数据恢复", MessageBoxButton.OK, MessageBoxImage.Information);
            if (HotkeyWarning.Length > 0) MessageBox.Show(HotkeyWarning, "快捷键提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "阅读器无法启动", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void BuildTray()
    {
        var resource = GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
        trayIcon = new System.Drawing.Icon(resource.Stream);
        tray = new Forms.NotifyIcon { Icon = trayIcon, Text = "摸鱼阅读器", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("书架", null, (_, _) => Dispatcher.Invoke(ShowShelf));
        menu.Items.Add("设置", null, (_, _) => Dispatcher.Invoke(ShowSettings));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(ExitApp));
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) Dispatcher.Invoke(RestoreCurrent); };
    }

    public void ShowShelf()
    {
        if (busy) return;
        Reader.CaptureProgress(); Reader.Hide();
        Store.Preferences.LastPage = "shelf";
        Shelf.Refresh(); Shelf.Show(); if (Shelf.WindowState == WindowState.Minimized) Shelf.WindowState = WindowState.Normal;
        Shelf.Activate(); SaveSoon();
    }

    public void HideCurrent()
    {
        Reader.CaptureProgress();
        settings?.Close();
        foreach (var window in Windows.Cast<Window>().Where(w => w.Owner == Reader).ToArray()) window.Close();
        Reader.Hide(); Shelf.Hide(); SaveSoon();
    }

    public void RestoreCurrent()
    {
        if (busy) return;
        if (Store.Preferences.LastPage == "reader" && Reader.CurrentBook != null)
        { Shelf.Hide(); ScreenPlacement.EnsureVisible(Reader); Reader.Show(); }
        else ShowShelf();
    }

    public void Toggle()
    {
        if (Reader.IsVisible || Shelf.IsVisible || settings?.IsVisible == true) HideCurrent(); else RestoreCurrent();
    }

    public async Task PickFiles()
    {
        if (busy) return;
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "导入电子书", Filter = "电子书 (*.txt;*.epub)|*.txt;*.epub", Multiselect = true };
        if (picker.ShowDialog() == true) await ImportFiles(picker.FileNames);
    }

    public async Task ImportFiles(string[] paths)
    {
        if (busy || paths.Length == 0) return;
        Reader.CaptureProgress(); busy = true; saveTimer.Stop();
        Shelf.SetBusy(true, "正在导入书籍…");
        Book? first = null;
        var errors = new List<string>();
        try
        {
            foreach (var path in paths)
            {
                try { var imported = await Task.Run(() => Store.Import(path)); first ??= imported; }
                catch (Exception ex) { errors.Add(Path.GetFileName(path) + "：" + ex.Message); }
            }
        }
        finally { busy = false; Shelf.SetBusy(false); }
        if (errors.Count > 0) MessageBox.Show(string.Join("\n\n", errors), "部分书籍未导入", MessageBoxButton.OK, MessageBoxImage.Warning);
        if (first != null && paths.Length == 1) await OpenBook(first);
        else ShowShelf();
    }

    public async Task OpenBook(Book book)
    {
        if (busy) return;
        Reader.CaptureProgress(); busy = true; saveTimer.Stop(); Shelf.SetBusy(true, "正在打开书籍…");
        try
        {
            var content = await Task.Run(() => EbookParser.Read(Store.BookPath(book), book.Title));
            Reader.LoadBook(book, content);
            Store.Preferences.LastPage = "reader"; Store.Preferences.LastBookId = book.Id;
            book.LastReadAt = DateTime.Now;
            Shelf.Hide(); ScreenPlacement.EnsureVisible(Reader); Reader.Show(); SaveSoon();
        }
        catch (Exception ex)
        {
            Shelf.Show(); Shelf.Activate(); Store.Preferences.LastPage = "shelf";
            MessageBox.Show(ex.Message, "无法打开这本书", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { busy = false; Shelf.SetBusy(false); }
    }

    public void RemoveBook(Book book)
    {
        if (busy) return;
        try { Store.Remove(book); Shelf.Refresh(); }
        catch (Exception e) { MessageBox.Show(e.Message, "移除未完成", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    public void ShowSettings()
    {
        if (settings is { IsVisible: true }) { settings.Activate(); return; }
        settings = new SettingsWindow(this);
        settings.Closed += (_, _) => settings = null;
        settings.Show(); settings.Activate();
    }

    public void RecoverReader()
    {
        if (Reader.CurrentBook == null || !Store.Books.Any(b => b.Id == Reader.CurrentBook.Id)) { ShowShelf(); return; }
        Shelf.Hide(); Store.Preferences.LastPage = "reader";
        ScreenPlacement.EnsureVisible(Reader, true); Reader.Show(); Reader.SaveBounds(); SaveSoon();
    }

    public bool ChangeHotkey(uint modifiers, uint key)
    {
        if ((modifiers & 3) == 0 || (modifiers & ~7u) != 0 || key == 0 || key > 255) return false;
        var p = Store.Preferences;
        if (p.HotkeyModifiers == modifiers && p.HotkeyKey == key && HotkeyWarning.Length == 0) return true;
        if (!hotkey.Set(modifiers, key)) return false;
        p.HotkeyModifiers = modifiers; p.HotkeyKey = key; HotkeyWarning = ""; return true;
    }

    public void SaveSoon() { saveTimer.Stop(); saveTimer.Start(); }
    public void SaveNow() { if (!busy) { Reader.CaptureProgress(); Reader.SaveBounds(); Store.Save(); saveErrorShown = false; } }
    private void SafeSave()
    {
        try { SaveNow(); }
        catch (Exception ex)
        {
            if (!saveErrorShown) { saveErrorShown = true; MessageBox.Show("保存失败，请检查程序目录是否可写。\n\n" + ex.Message, "保存失败", MessageBoxButton.OK, MessageBoxImage.Error); }
        }
    }

    public void ExitApp()
    {
        if (busy) { MessageBox.Show("正在处理书籍，请稍候再退出。", "摸鱼阅读器"); return; }
        try { SaveNow(); }
        catch (Exception ex) { MessageBox.Show("保存失败，暂未退出，避免丢失进度：\n" + ex.Message, "保存失败"); return; }
        IsExiting = true; Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        saveTimer.Stop(); tray?.Dispose(); trayIcon?.Dispose(); hotkey?.Dispose();
        if (ownsMutex) instance?.ReleaseMutex(); instance?.Dispose(); base.OnExit(e);
    }
}
