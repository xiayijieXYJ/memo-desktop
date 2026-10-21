using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Memo.Services;
using Memo.ViewModels;

namespace Memo;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly MemoViewModel _vm;
    private bool _initializing = true;

    public SettingsWindow(AppSettings settings, MemoViewModel vm)
    {
        InitializeComponent();
        _settings = settings;
        _vm = vm;

        OpacitySlider.Value = Math.Round(settings.Opacity * 100);
        OpacityLabel.Text = $"{OpacitySlider.Value:0}%";
        PinnedCheck.IsChecked = settings.IsPinned;
        ShowDoneCheck.IsChecked = vm.ShowDone;
        StartupCheck.IsChecked = StartupRegistrar.IsEnabled();
        SyncThemeChips();
        _initializing = false;

        if (Application.Current.MainWindow is MainWindow main)
        {
            Left = main.Left + (main.Width - Width) / 2;
            Top = main.Top + 80;
        }
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing || OpacityLabel is null)
        {
            return;
        }

        OpacityLabel.Text = $"{e.NewValue:0}%";
        _settings.Opacity = e.NewValue / 100.0;
        _settings.Save();

        if (Application.Current.MainWindow is MainWindow main)
        {
            main.ApplyOpacity();
        }
    }

    private void PinnedCheck_Click(object sender, RoutedEventArgs e)
    {
        _settings.IsPinned = PinnedCheck.IsChecked == true;
        _settings.Save();
        (Application.Current.MainWindow as MainWindow)?.RefreshPinState();
    }

    /// <summary>
    /// 深色 / 浅色 / 浅黄三选一。切完写回 settings.json，重开还是这个主题；
    /// 界面颜色全走 DynamicResource，所以除了托盘图标要重画，别的控件会自己跟着变。
    /// </summary>
    private void ThemeChip_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing || sender is not RadioButton { Tag: string wanted })
        {
            return;
        }

        ThemeManager.Apply(wanted);
        _settings.Theme = ThemeManager.Current;
        _settings.Save();

        // 三颗胶囊的选中态再对一遍，防止在已选中的那颗上又点了一下
        SyncThemeChips();
    }

    private void SyncThemeChips()
    {
        DarkThemeChip.IsChecked = !ThemeManager.IsLight && !ThemeManager.IsSepia;
        LightThemeChip.IsChecked = ThemeManager.IsLight;
        SepiaThemeChip.IsChecked = ThemeManager.IsSepia;
    }

    private void ShowDoneCheck_Click(object sender, RoutedEventArgs e)
    {
        // 底部那个「显示/隐藏已完成」按钮已经去掉，这里改 viewmodel 就够了，
        // 列表靠 ShowDone 的通知自更新，不用再回头刷新按钮文案。
        _vm.ShowDone = ShowDoneCheck.IsChecked == true;
    }

    private void StartupCheck_Click(object sender, RoutedEventArgs e)
    {
        var want = StartupCheck.IsChecked == true;
        if (!StartupRegistrar.SetEnabled(want))
        {
            // 写注册表失败时把勾选状态还原，不留给用户一个假的"已开启"
            StartupCheck.IsChecked = !want;
            MessageBox.Show("设置开机启动失败，可能是注册表权限被限制。", "Memo",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenDataDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            Process.Start(new ProcessStartInfo
            {
                FileName = AppPaths.DataDir,
                UseShellExecute = true,
            });
        }
        catch
        {
            // 打开目录失败不阻塞设置窗口
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 设置窗是无边框窗口，默认没法拖动。在根 Border 上按下就调 DragMove。
    /// 按钮、复选框、滑块会自己消化掉 MouseLeftButtonDown，不会走到这里，
    /// 所以拖动和点控件不冲突。
    /// </summary>
    private void RootBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标已释放时 DragMove 会抛，忽略即可
        }
    }
}
