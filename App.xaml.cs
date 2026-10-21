using System.Windows;
using System.Windows.Threading;
using Memo.Services;
using Memo.ViewModels;

namespace Memo;

public partial class App : Application
{
    public static bool IsExiting { get; private set; }

    private TrayIconManager? _tray;
    private GlobalHotKey? _hotKey;
    private MainWindow? _window;
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 开发自检模式：走 --selftest，跑完直接退出，不建窗口。
        var selfTestArg = e.Args.FirstOrDefault(a => a.StartsWith("--selftest", StringComparison.OrdinalIgnoreCase));
        if (selfTestArg is not null)
        {
            var outPath = Path.Combine(Path.GetTempPath(), "memo_selftest_result.txt");
            SelfTest.Run(outPath);
            Shutdown();
            Environment.Exit(0);
            return;
        }

        // 单实例：第二次启动时把已有窗口叫到前面，而不是开第二个卡片。
        _singleInstanceMutex = new Mutex(true, @"Local\Memo.SingleInstance.9F1C", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("Memo 已经在运行了，看托盘图标。", "Memo", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        MemoStore.Initialize();
        var settings = AppSettings.Load();

        // 主题必须在建窗口之前套上，否则第一帧会先闪一下深色再变浅色。
        ThemeManager.Apply(settings.Theme);

        SeedIfRequested();
        var vm = new MemoViewModel();

        _window = new MainWindow(vm, settings, _tray = new TrayIconManager());
        _tray.ShowRequested += () => ShowWindow();
        _tray.ToggleRequested += ToggleWindow;
        _tray.SettingsRequested += () => _window?.OpenSettings();
        _tray.ExitRequested += ExitApp;

        MainWindow = _window;
        _window.Show();

        _hotKey = new GlobalHotKey();
        _hotKey.Pressed += ToggleWindow;
        _hotKey.Register(_window);

        if (!_hotKey.IsRegistered)
        {
            // 热键被占用不致命：托盘菜单照样能用
            _tray.ShowBalloon("快捷键不可用", "Ctrl+Alt+M 被其他程序占用，可点托盘图标唤出卡片。");
        }
    }

    /// <summary>
    /// 只有传 --seed 时才写入演示数据，方便第一次打开就看到真实排布；正常启动不会自动加内容。
    /// </summary>
    private static void SeedIfRequested()
    {
        if (!Environment.GetCommandLineArgs().Any(a => string.Equals(a, "--seed", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        var samples = new (string Text, int Priority, bool Done)[]
        {
            ("把评审意见合并进 2.3 分支", 2, false),
            ("修复登录页在 4K 屏下的错位", 2, false),
            ("整理下周的迭代发布说明", 1, false),
            ("回复测试组提的验证问题", 1, false),
            ("更新接口文档里的鉴权说明", 0, false),
            ("晨会同步进度", 1, true),
        };

        var order = 0;
        foreach (var (text, priority, done) in samples)
        {
            var item = new Memo.Models.MemoItem
            {
                Content = text,
                Priority = priority,
                SortOrder = order++,
            };
            item.Id = MemoStore.Insert(item, today);
            if (done)
            {
                item.IsDone = true;
                MemoStore.UpdateDone(item, today);
            }
        }
    }

    private void ShowWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.ShowFromTray();
    }

    private void ToggleWindow()
    {
        if (_window is null)
        {
            return;
        }

        if (_window.IsVisible && _window.WindowState != WindowState.Minimized)
        {
            _window.HideToTray();
        }
        else
        {
            _window.ShowFromTray();
        }
    }

    private void ExitApp()
    {
        IsExiting = true;
        _window?.SavePlacement();
        _window?.Close();
        Shutdown();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // 桌面小工具不该因为一次异常整个崩掉：提示后继续运行
        MessageBox.Show($"出了点小问题：{e.Exception.Message}", "Memo", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotKey?.Dispose();
        _tray?.Dispose();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
