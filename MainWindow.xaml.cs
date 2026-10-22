using System;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Memo.Models;
using Memo.Services;
using Memo.ViewModels;

namespace Memo;

public partial class MainWindow : Window
{
    private readonly MemoViewModel _vm;
    private readonly AppSettings _settings;
    private readonly TrayIconManager _tray;
    private readonly DispatcherTimer _dateTimer;

    /// <summary>正在由快捷键（回车 / Esc）主动收编辑框，期间屏蔽失焦自动提交。</summary>
    private bool _closingEdit;

    public MainWindow(MemoViewModel vm, AppSettings settings, TrayIconManager tray)
    {
        InitializeComponent();
        _vm = vm;
        _settings = settings;
        _tray = tray;
        DataContext = vm;

        RestorePlacement();
        ApplyOpacity();
        UpdatePinVisual();
        UpdateNewPriorityVisual();
        UpdatePlaceholder();

        // 每分钟检查一次是否跨天：跨天自动切换"今天"的视图，不需要重启。
        _dateTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMinutes(1),
        };
        _dateTimer.Tick += (_, _) => _vm.RefreshIfDateChanged();
        _dateTimer.Start();

        Loaded += (_, _) => NewItemBox.Focus();
        SizeChanged += (_, _) => SavePlacement();
    }

    private const double EdgeMargin = 8;

    private void RestorePlacement()
    {
        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;

        if (!_settings.HasPosition)
        {
            // 首次运行放到主屏右上角，避开任务栏。
            var area = SystemParameters.WorkArea;
            Left = area.Right - Width - 24;
            Top = area.Top + 80;
            return;
        }

        // 外接屏拔掉后旧坐标可能落到屏幕外，夹回可见区域。
        var left = _settings.WindowLeft;
        var top = _settings.WindowTop;
        if (left < SystemParameters.VirtualScreenLeft - Width + EdgeMargin ||
            left > SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - EdgeMargin ||
            top < SystemParameters.VirtualScreenTop - Height + EdgeMargin ||
            top > SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - EdgeMargin)
        {
            var area = SystemParameters.WorkArea;
            left = area.Right - Width - 24;
            top = area.Top + 80;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left;
        Top = top;
    }

    public void SavePlacement()
    {
        if (!IsLoaded)
        {
            return;
        }

        _settings.WindowLeft = Left;
        _settings.WindowTop = Top;
        _settings.WindowWidth = Width;
        _settings.WindowHeight = Height;
        _settings.HasPosition = true;
        _settings.Save();
    }

    public void ApplyOpacity() => Opacity = _settings.Opacity;

    /// <summary>设置窗口改完置顶后，让主窗口同步刷新。</summary>
    public void RefreshPinState() => UpdatePinVisual();

    public void OpenSettings()
    {
        var existing = Application.Current.Windows.OfType<SettingsWindow>().FirstOrDefault();
        if (existing is not null)
        {
            existing.Activate();
            return;
        }

        var win = new SettingsWindow(_settings, _vm) { Owner = null };
        win.ShowDialog();
        UpdatePinVisual();
        ApplyOpacity();
    }

    private void OpenSettingsEntry(object sender, RoutedEventArgs e) => OpenSettings();

    /// <summary>
    /// 打开月度总结小窗。不做模态 —— 看完随时能回来接着干活；
    /// 已经开着就把它叫到前面，免得连着点几次叠出一堆一样的窗口。
    /// </summary>
    private void OpenSummaryEntry(object sender, RoutedEventArgs e) => OpenSummary();

    public void OpenSummary()
    {
        var existing = Application.Current.Windows.OfType<MonthSummaryWindow>().FirstOrDefault();
        if (existing is not null)
        {
            existing.Activate();
            return;
        }

        // 主窗口如果正停在别的日期上，就从那个日期所在的月份看起来，更符合当下的上下文
        var win = new MonthSummaryWindow(_vm.EditingDate) { Owner = null };
        win.Show();
    }

    private void FocusInputEntry(object sender, RoutedEventArgs e)
    {
        NewItemBox.Focus();
        NewItemBox.SelectAll();
    }

    private void UpdatePinVisual()
    {
        Topmost = _settings.IsPinned;
        PinButton.Opacity = _settings.IsPinned ? 1.0 : 0.4;
        PinButton.ToolTip = _settings.IsPinned ? "取消置顶" : "置顶显示";
    }

    private void UpdateNewPriorityVisual()
    {
        // 文字和配色都由 XAML 里的 DataTrigger 绑定 NewPriorityLabel/Key，这里只需刷新提示
        NewPrioButton.ToolTip = $"新条目优先级：{_vm.NewPriorityLabel}（点击切换）";
    }

    private void UpdatePlaceholder()
    {
        Placeholder.Visibility = string.IsNullOrEmpty(_vm.NewContent)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // ---------- 窗口拖动 / 关闭 ----------

    private void RootCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
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
            // 鼠标已释放时 DragMove 会抛异常，忽略即可
        }

        SavePlacement();
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.IsPinned = !_settings.IsPinned;
        _settings.Save();
        UpdatePinVisual();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        // 任务栏不显示这个窗口，最小化后就没有图标可以点回来了，
        // 所以 — 和 ✕ 都收进托盘（托盘图标左键点一下、或 Ctrl+Alt+M 唤出）。
        HideToTray();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => HideToTray();

    public void HideToTray()
    {
        SavePlacement();
        Hide();
    }

    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = _settings.IsPinned;

        if (!_settings.IsPinned)
        {
            // 取消置顶后重新显示时，手动刷一次置顶状态，避免被其他窗口压住
            Topmost = true;
            Topmost = false;
        }

        NewItemBox.Focus();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // 点 X 只收进托盘，真退出走托盘菜单。
        if (!App.IsExiting)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        _dateTimer.Stop();
        SavePlacement();
        base.OnClosing(e);
    }

    // ---------- 输入与列表交互 ----------

    private void NewItemBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddCurrent();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _vm.NewContent = string.Empty;
            UpdatePlaceholder();
            e.Handled = true;
        }
    }

    private void NewItemBox_TextChanged(object sender, TextChangedEventArgs e) => UpdatePlaceholder();

    private void AddButton_Click(object sender, RoutedEventArgs e) => AddCurrent();

    private void AddCurrent()
    {
        // 焦点还在输入框时按回车：先把当前文本同步到 VM，再执行添加
        _vm.NewContent = NewItemBox.Text;
        _vm.AddCommand.Execute(null);
        NewItemBox.Clear();
        NewItemBox.Focus();
        UpdatePlaceholder();
    }

    private void NewPriorityButton_Click(object sender, RoutedEventArgs e)
    {
        _vm.SetNewPriority(_vm.NewPriority switch { 2 => 1, 1 => 0, _ => 2 });
        UpdateNewPriorityVisual();
        NewItemBox.Focus();
    }

    private void PriorityBadge_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MemoItem item })
        {
            _vm.CyclePriorityCommand.Execute(item);
        }
    }

    private void ContentText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && sender is FrameworkElement { DataContext: MemoItem item })
        {
            _vm.BeginEditCommand.Execute(item);
            e.Handled = true;
            // 取焦点/滚动交给 EditBox_IsVisibleChanged 统一处理
        }
    }

    /// <summary>
    /// 编辑框从隐藏变成可见时：取焦点、全选、并把它滚进视野。
    /// 放在这里而不是双击处理里，是因为右键菜单「编辑」也要走同一套
    /// —— 上一版只在双击分支里取焦点，右键编辑进入后光标没落进框里，得再点一下。
    /// </summary>
    private void EditBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox { IsVisible: true } box)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            box.Focus();
            box.SelectAll();
            box.BringIntoView();
        }));
    }

    /// <summary>
    /// 边打字边长高的框，靠这一手让新长出来的那一行留在视野里。
    /// 不这么做的话，文案变长后框在下面延伸，但外层 ScrollViewer 不知道要跟，
    /// 光标就跑到可视区外面去了 —— 又回到"看不见自己在打什么"的老问题。
    /// </summary>
    private void EditBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox { IsVisible: true } box)
        {
            box.BringIntoView();
        }
    }

    private void EditBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MemoItem item })
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CloseEditor(item, commit: true);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseEditor(item, commit: false);
        }
    }

    /// <summary>
    /// 收编辑框：先把键盘焦点接回顶部输入框，再让编辑框消失。
    ///
    /// 顺序不能反。反过来的话（先 IsEditing = false 让框收起）会出现一瞬间的「焦点悬空」：
    /// 编辑框还握着键盘焦点却被隐藏了，WPF 只能把焦点甩给下一层可聚焦祖先（列表外层 ScrollViewer），
    /// 并且因为回车是键盘操作，顺手给那个祖先画一圈焦点虚线框 ——
    /// 于是「改完回车，列表四周闪一下虚线框」，闪完焦点被我们抢回去，框又消失。
    /// 先落焦再收框，就没有这段悬空时间，也不会闪。
    ///
    /// _closingEdit 用来屏蔽「落焦 → 编辑框失焦 → EndEdit 又提交一次」：
    /// 尤其是 Esc 取消编辑的情况，失焦自动提交会把改了一半的内容写进库，跟界面显示的原文本不一致。
    /// </summary>
    private void CloseEditor(MemoItem item, bool commit)
    {
        _closingEdit = true;
        try
        {
            if (NewItemBox.IsVisible)
            {
                NewItemBox.Focus();
            }

            if (commit)
            {
                _vm.CommitEditCommand.Execute(item);
            }
            else
            {
                // 取消编辑：丢弃本次改动，恢复原文本
                item.Content = item.OriginalContent;
                item.IsEditing = false;
            }
        }
        finally
        {
            _closingEdit = false;
        }
    }

    private void EditBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // 键盘收编辑框时（CloseEditor）焦点是先挪走的，这里会跟着触发一次，
        // 交给 CloseEditor 自己决定是提交还是丢弃，别在这儿再提交一遍。
        if (_closingEdit)
        {
            return;
        }

        if (sender is FrameworkElement { DataContext: MemoItem item })
        {
            _vm.EndEdit(item);
        }
    }

    // ---------- 右键菜单 ----------

    private static MemoItem? ItemFromMenu(object sender)
    {
        if (sender is MenuItem { Parent: ContextMenu menu } &&
            menu.PlacementTarget is FrameworkElement { DataContext: MemoItem item })
        {
            return item;
        }

        return null;
    }

    private void MenuItem_Edit_Click(object sender, RoutedEventArgs e)
    {
        if (ItemFromMenu(sender) is { } item)
        {
            _vm.BeginEditCommand.Execute(item);
        }
    }

    /// <summary>
    /// 复制一条的文字。主卡片双击是进编辑，复制就放右键菜单里，
    /// 免得跟双击抢手势；成功失败都不弹窗，剪贴板是顺手用的东西。
    /// </summary>
    private void MenuItem_Copy_Click(object sender, RoutedEventArgs e)
    {
        if (ItemFromMenu(sender) is { } item)
        {
            ClipboardHelper.TrySetText(item.Content);
        }
    }

    private void MenuItem_Priority_Click(object sender, RoutedEventArgs e)
    {
        if (ItemFromMenu(sender) is { } item && sender is MenuItem { Tag: string tag })
        {
            _vm.SetPriorityExactCommand.Execute(new object[] { item, tag });
        }
    }

    private void MenuItem_Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ItemFromMenu(sender) is { } item)
        {
            _vm.DeleteCommand.Execute(item);
        }
    }

    private void ClearDoneButton_Click(object sender, RoutedEventArgs e) => _vm.ClearDoneCommand.Execute(null);

    /// <summary>卡片右键菜单里的「显示/隐藏已完成」，等价于设置窗口里那个勾选项。</summary>
    private void ToggleShowDoneEntry(object sender, RoutedEventArgs e) => _vm.ShowDone = !_vm.ShowDone;

    private void UndoDeleteButton_Click(object sender, RoutedEventArgs e) => _vm.UndoDeleteCommand.Execute(null);

    // ---------- 按日期回看 ----------

    private void PrevDateButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.GoAdjacentDate(-1))
        {
            SystemSounds.Beep.Play();
        }
    }

    private void NextDateButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.GoAdjacentDate(+1))
        {
            SystemSounds.Beep.Play();
        }
    }

    private void GoTodayButton_Click(object sender, RoutedEventArgs e)
    {
        _vm.ViewDate(null);
        NewItemBox.Focus();
    }

    /// <summary>打开日历小窗，点哪一天就把卡片切到那天：过去只读回看，将来可以直接安排。</summary>
    private void CalendarButton_Click(object sender, RoutedEventArgs e)
    {
        var current = _vm.IsHistoryView ? _vm.ViewingDate : (DateOnly?)null;
        var picker = new DatePickerWindow(current, date => _vm.ViewDate(date))
        {
            Owner = this,
        };
        picker.ShowDialog();
    }

    /// <summary>看过去的那一天时把行菜单里的「编辑 / 优先级 / 删除」灰掉，只留查看。</summary>
    private void Row_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.ContextMenu is not { } menu)
        {
            return;
        }

        foreach (var item in menu.Items)
        {
            if (item is MenuItem mi)
            {
                mi.IsEnabled = _vm.IsEditable;
            }
        }
    }
}
