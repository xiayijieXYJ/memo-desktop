using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Memo.Services;

namespace Memo;

/// <summary>
/// 按日期回看的日历小窗：有记录的日期带小圆点，点哪天就把主窗口切到那天的只读视图。
/// 只负责选日期，实际切视图由 MainWindow 处理。
/// </summary>
public partial class DatePickerWindow : Window
{
    private readonly Action<DateOnly?> _onPick;
    private readonly DateOnly _today = DateOnly.FromDateTime(DateTime.Now);
    private readonly HashSet<DateOnly> _recorded;
    private DateOnly _month;

    /// <summary>一个日期格子要显示的信息。</summary>
    private sealed class DayCell
    {
        public DateOnly Date { get; init; }
        public int Day => Date.Day;
        public bool HasRecord { get; init; }
        public bool IsToday { get; init; }
    }

    public DatePickerWindow(DateOnly? current, Action<DateOnly?> onPick)
    {
        InitializeComponent();
        _onPick = onPick;
        _recorded = MemoStore.GetRecordedDates().ToHashSet();

        var start = current ?? _today;
        _month = new DateOnly(start.Year, start.Month, 1);

        BuildWeekHeader();
        RenderMonth();

        // Esc 关掉，符合小窗的直觉
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        };

        if (Application.Current.MainWindow is MainWindow main)
        {
            Left = main.Left + (main.Width - Width) / 2;
            Top = main.Top + 60;
        }
    }

    private void BuildWeekHeader()
    {
        string[] names = { "一", "二", "三", "四", "五", "六", "日" };
        for (var i = 0; i < names.Length; i++)
        {
            var tb = new TextBlock
            {
                Text = names[i],
                // 用 DynamicResource 挂主题画刷，切主题时表头跟着变
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "WeekHeaderText");
            Grid.SetColumn(tb, i);
            WeekHeader.Children.Add(tb);
        }
    }

    private void RenderMonth()
    {
        MonthLabel.Text = $"{_month.Year}年{_month.Month}月";
        DayGrid.Children.Clear();

        // 周一为一周第一天：DayOfWeek 里周日是 0，换成 0=周一
        var offset = ((int)_month.DayOfWeek + 6) % 7;
        var daysInMonth = DateTime.DaysInMonth(_month.Year, _month.Month);

        for (var day = 1; day <= daysInMonth; day++)
        {
            var date = new DateOnly(_month.Year, _month.Month, day);
            var cell = new DayCell
            {
                Date = date,
                HasRecord = _recorded.Contains(date),
                IsToday = date == _today,
            };

            var btn = new Button
            {
                Style = (Style)FindResource("DayCell"),
                Content = day.ToString(),
                DataContext = cell,
                ToolTip = cell.HasRecord ? $"{date.Month}月{date.Day}日 · 有记录" : $"{date.Month}月{date.Day}日",
            };
            btn.Click += (_, _) => Pick(cell.Date);

            var index = offset + day - 1;
            Grid.SetColumn(btn, index % 7);
            Grid.SetRow(btn, index / 7);
            DayGrid.Children.Add(btn);
        }
    }

    private void Pick(DateOnly date)
    {
        _onPick(date == _today ? null : date);
        Close();
    }

    private void PrevMonth_Click(object sender, RoutedEventArgs e)
    {
        _month = _month.AddMonths(-1);
        RenderMonth();
    }

    private void NextMonth_Click(object sender, RoutedEventArgs e)
    {
        _month = _month.AddMonths(1);
        RenderMonth();
    }

    private void Today_Click(object sender, RoutedEventArgs e) => Pick(_today);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 日历窗同样是无边框窗口。日期格子和顶部按钮会把 MouseLeftButtonDown 消化掉，
    /// 只有空白处（月份标题、表头缝隙、底部提示行）会走到这里，可以拖着走。
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
