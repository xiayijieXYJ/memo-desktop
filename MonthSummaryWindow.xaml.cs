using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Memo.Models;
using Memo.Services;

namespace Memo;

/// <summary>
/// 月度总结：左边是所选月份「完成的」，右边（下半截）是「仍在处理」的全部记录。
///
/// 两个口径要分清，别混：
///   完成的 —— 按 done_date 落在该月算，9 月安排 10 月才勾掉的算 10 月的成绩；
///   仍在处理 —— 不分月份，全库所有 is_done = 0 的记录，这才是"欠着没干"的真实清单。
/// </summary>
public partial class MonthSummaryWindow : Window
{
    private readonly DateOnly _today = DateOnly.FromDateTime(DateTime.Now);
    private DateOnly _month;

    /// <summary>「已复制」那个小提示的自动收起计时器。</summary>
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromMilliseconds(1400) };

    public MonthSummaryWindow(DateOnly? monthAnchor = null)
    {
        InitializeComponent();

        var start = monthAnchor ?? _today;
        _month = new DateOnly(start.Year, start.Month, 1);

        Loaded += (_, _) => CenterOverMain();
        RenderAll();

        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            CopyToast.Visibility = Visibility.Collapsed;
        };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        };
    }

    /// <summary>
    /// 每次切回这个窗口都重读一遍：卡片上勾掉一条、加一条之后回到这里，
    /// 数字和清单要跟着变，不能停在上次打开时的快照上。
    /// </summary>
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        RenderAll();
    }

    /// <summary>跟着主窗口摆，比 WindowStartupLocation="CenterScreen" 更贴合"从卡片点开"的感觉。</summary>
    private void CenterOverMain()
    {
        if (Application.Current.MainWindow is not { } main || main.WindowState == WindowState.Minimized)
        {
            return;
        }

        Left = main.Left + (main.ActualWidth - ActualWidth) / 2;
        Top = main.Top + (main.ActualHeight - ActualHeight) / 2;
    }

    private void RenderAll()
    {
        MonthLabel.Text = $"{_month.Year}年{_month.Month}月";

        var done = MemoStore.LoadDoneInMonth(_month);
        var open = MemoStore.LoadAllOpen();
        var createdCount = MemoStore.CountCreatedInMonth(_month);

        DoneCountText.Text = done.Count.ToString(CultureInfo.InvariantCulture);
        CreatedCountText.Text = createdCount.ToString(CultureInfo.InvariantCulture);
        OpenCountText.Text = open.Count.ToString(CultureInfo.InvariantCulture);

        DoneHeader.Text = done.Count > 0 ? $"本月完成 · {done.Count} 条" : "本月完成";
        OpenHeader.Text = open.Count > 0 ? $"仍在处理 · {open.Count} 条" : "仍在处理";

        DoneList.ItemsSource = done.Select(item => ToEntry(item, isDone: true)).ToList();
        OpenList.ItemsSource = open.Select(item => ToEntry(item, isDone: false)).ToList();

        DoneEmpty.Visibility = done.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        OpenEmpty.Visibility = open.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private SummaryEntry ToEntry(MemoItem item, bool isDone) => new()
    {
        Content = item.Content,
        PriorityText = item.PriorityText,
        PriorityKey = item.PriorityKey,
        IsDone = isDone,
        DateText = isDone ? FormatDoneDay(item) : FormatOpenDay(item),
    };

    /// <summary>完成那一栏显示"哪天勾掉的"，比显示"原本安排在哪天"更有用。</summary>
    private string FormatDoneDay(MemoItem item)
    {
        if (TryParse(item.DoneDate, out var date))
        {
            return $"{date.Month}月{date.Day}日";
        }

        return TryParse(item.CreatedDate, out var planned) ? $"{planned.Month}月{planned.Day}日" : "—";
    }

    /// <summary>没完成的：过期的说"顺延几天"，将来的是日期，今天就是今天。</summary>
    private string FormatOpenDay(MemoItem item)
    {
        if (!TryParse(item.CreatedDate, out var planned))
        {
            return "—";
        }

        var diff = _today.DayNumber - planned.DayNumber;
        if (diff > 0)
        {
            return $"顺延{diff}天";
        }

        return diff == 0 ? "今天" : $"{planned.Month}月{planned.Day}日";
    }

    private static bool TryParse(string? text, out DateOnly date) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private void PrevMonth_Click(object sender, RoutedEventArgs e)
    {
        _month = _month.AddMonths(-1);
        RenderAll();
    }

    private void NextMonth_Click(object sender, RoutedEventArgs e)
    {
        _month = _month.AddMonths(1);
        RenderAll();
    }

    private void CurrentMonth_Click(object sender, RoutedEventArgs e)
    {
        _month = new DateOnly(_today.Year, _today.Month, 1);
        RenderAll();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 双击一行把文字复制走。
    ///
    /// 这个窗口是只读的，双击本来就空着 —— 主卡片那边双击是进编辑，这里不需要，
    /// 就拿来做复制（单号、任务原文贴到提交日志或邮件里都用得上）。
    /// 单击不拦：不设 Handled 时事件照旧冒泡到根 Border，按住空白处还能拖窗口。
    /// </summary>
    private void Row_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || sender is not FrameworkElement { DataContext: SummaryEntry entry })
        {
            return;
        }

        ShowToast(ClipboardHelper.TrySetText(entry.Content) ? "已复制" : "复制失败");
        e.Handled = true;
    }

    private void ShowToast(string text)
    {
        CopyToastText.Text = text;
        CopyToast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    /// <summary>无边框窗口，只有空白处能拖；标题栏按钮和列表行会先把鼠标事件消化掉。</summary>
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
            // 鼠标已释放时 DragMove 会抛，忽略
        }
    }
}
