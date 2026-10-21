using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Memo.Models;
using Memo.Services;

namespace Memo.ViewModels;

public partial class MemoViewModel : ObservableObject
{
    private DateOnly _today = DateOnly.FromDateTime(DateTime.Now);

    /// <summary>
    /// 正在查看的日期；null 表示看「今天」。
    /// 也可以指向未来（提前把事安排到明天、下周），那时同样可编辑。
    /// </summary>
    private DateOnly? _historyDate;

    private bool _reordering;
    private bool _reorderPending;

    public ObservableCollection<MemoItem> Items { get; } = new();

    [ObservableProperty]
    private string _newContent = string.Empty;

    [ObservableProperty]
    private int _newPriority = 1;

    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>新增条目的优先级文字（高/中/低）。</summary>
    public string NewPriorityLabel => NewPriority switch
    {
        2 => "高",
        0 => "低",
        _ => "中",
    };

    /// <summary>新增条目优先级对应的样式键，XAML 用它切换徽章配色。</summary>
    public string NewPriorityKey => NewPriority switch
    {
        2 => "High",
        0 => "Low",
        _ => "Normal",
    };

    partial void OnNewPriorityChanged(int value)
    {
        OnPropertyChanged(nameof(NewPriorityLabel));
        OnPropertyChanged(nameof(NewPriorityKey));
    }

    [ObservableProperty]
    private bool _hasItems;

    [ObservableProperty]
    private bool _hasNoDoneItems = true;

    /// <summary>是否显示已完成项。默认显示：勾掉后能看到划线划过去，观感更自然。</summary>
    [ObservableProperty]
    private bool _showDone = true;

    /// <summary>是否有可撤销的删除，控制底部「撤销删除」的显隐。</summary>
    [ObservableProperty]
    private bool _canUndoDelete;

    /// <summary>按优先级倒序展示、自动隐藏已完成项的视图。</summary>
    public ICollectionView ItemsView { get; }

    public MemoViewModel()
    {
        ItemsView = new ListCollectionView(Items)
        {
            Filter = o => ShowDone || o is not MemoItem m || !m.IsDone,
        };
        Reload();
    }

    partial void OnShowDoneChanged(bool value)
    {
        ItemsView.Refresh();
        UpdateStats();
    }

    /// <summary>是否不在看今天（看过去或看未来都算）。</summary>
    public bool IsHistoryView => _historyDate is not null;

    /// <summary>是否在看过去的某一天。过去的记录只读，不能改。</summary>
    public bool IsReadOnlyView => _historyDate is { } d && d < _today;

    /// <summary>是否在看将来的某一天。将来的安排可以照常新增、勾选、改优先级。</summary>
    public bool IsFutureView => _historyDate is { } d && d > _today;

    /// <summary>非今天时顶部提示，例如「回看 10月19日」「安排 10月21日」。</summary>
    public string HistoryBadgeText => _historyDate is { } d
        ? (d > _today ? $"安排 {d.Month}月{d.Day}日" : $"回看 {d.Month}月{d.Day}日")
        : string.Empty;

    /// <summary>输入框的占位提示。安排在未来的日子要明确写清是哪天，免得以为记到今天了。</summary>
    public string PlaceholderText => _historyDate is { } d
        ? $"提前记下 {d.Month}月{d.Day}日 要做的事…"
        : "记一件今天要做的事…";

    public void Reload()
    {
        _today = DateOnly.FromDateTime(DateTime.Now);

        foreach (var old in Items.ToList())
        {
            Detach(old);
        }

        Items.Clear();

        var source = _historyDate switch
        {
            // 看过去：只读回看那天当时的样子
            { } past when past < _today => MemoStore.LoadHistoryForDate(past),
            // 看将来：只看"安排在那天"的事，不要把今天没做完的一堆拖进去
            { } future => MemoStore.LoadPlannedForDate(future),
            // 看今天
            _ => MemoStore.LoadForDate(_today),
        };

        foreach (var item in source)
        {
            Attach(item);
            Items.Add(item);
        }

        if (IsReadOnlyView)
        {
            // 历史记录只读：不再重排（重排会把"当时已完成"的挪到末尾，破坏当时的顺序感），
            // 但统计和标题要跟着刷。
            UpdateStats();
        }
        else
        {
            Reorder();
        }
    }

    /// <summary>切到某一天。传 null 回到今天；传今天本身也视作回到今天。</summary>
    public void ViewDate(DateOnly? date)
    {
        // "今天"永远用 null 表示，免得同一状态有两种写法
        _historyDate = date == _today ? null : date;
        OnPropertyChanged(nameof(IsHistoryView));
        OnPropertyChanged(nameof(IsReadOnlyView));
        OnPropertyChanged(nameof(IsFutureView));
        OnPropertyChanged(nameof(HistoryBadgeText));
        OnPropertyChanged(nameof(IsEditable));
        OnPropertyChanged(nameof(EditingDate));
        OnPropertyChanged(nameof(PlaceholderText));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
        Reload();
    }

    /// <summary>是否允许编辑。过去的日子只读；今天和将来都能改。</summary>
    public bool IsEditable => !IsReadOnlyView;

    /// <summary>正在看的日期；看今天时为 null。</summary>
    public DateOnly? ViewingDate => _historyDate;

    /// <summary>新建条目该落到哪一天：正在看哪天就记在哪天，看今天就是今天。</summary>
    public DateOnly EditingDate => _historyDate ?? _today;

    /// <summary>
    /// 翻到前一天 / 后一天（按自然日，不是按"有记录的日子"）。
    /// 按自然日翻才符合直觉：箭头永远点得动，翻到空的日子就显示空提示。
    /// 往后翻到将来是允许的 —— 提前把事安排到明天、下周就靠这个。
    /// 想快速跳到有记录的日子用日历。
    /// </summary>
    public bool GoAdjacentDate(int direction)
    {
        var anchor = _historyDate ?? _today;

        DateOnly target;
        try
        {
            target = anchor.AddDays(direction < 0 ? -1 : 1);
        }
        catch (ArgumentOutOfRangeException)
        {
            // 翻到日期边界了，理论上碰不到
            return false;
        }

        ViewDate(target == _today ? null : target);
        return true;
    }

    /// <summary>能否往前翻。过去的每一天都能翻，翻到没记录的日子会显示空。</summary>
    public bool CanGoPrevious => true;

    /// <summary>能否往后翻。今天不是最后一天，可以翻到将来提前安排。</summary>
    public bool CanGoNext => true;

    /// <summary>
    /// 统一在属性变化处落库并重排。
    /// 不放在按钮 Click 里：键盘空格、UI Automation、外部绑定都能改 IsDone，
    /// 只有订阅 PropertyChanged 才不会漏。
    /// </summary>
    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not MemoItem item)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(MemoItem.IsDone):
                MemoStore.UpdateDone(item, _today);
                ScheduleReorder();
                break;
            case nameof(MemoItem.Priority):
                MemoStore.UpdatePriority(item);
                ScheduleReorder();
                break;
        }
    }

    /// <summary>
    /// 立即重排。用 _reordering 做重入保护：重排过程中 Items.Move 又会触发绑定刷新，
    /// 期间再来一次重排就直接合并进当前这次，避免嵌套。
    /// 同步执行保证"勾选后立刻排到末尾"，不依赖消息循环（自检里没有 UI 循环也能过）。
    /// </summary>
    private void ScheduleReorder()
    {
        if (_reordering)
        {
            _reorderPending = true;
            return;
        }

        _reordering = true;
        try
        {
            do
            {
                _reorderPending = false;
                Reorder();
            }
            while (_reorderPending);
        }
        finally
        {
            _reordering = false;
        }
    }

    private void Attach(MemoItem item) => item.PropertyChanged += OnItemPropertyChanged;

    private void Detach(MemoItem item) => item.PropertyChanged -= OnItemPropertyChanged;

    /// <summary>跨天检测：日期一变就把列表按新的今天重新载入（顺延天数、今日视图都会跟着变）。</summary>
    public void RefreshIfDateChanged()
    {
        var now = DateOnly.FromDateTime(DateTime.Now);
        if (now != _today)
        {
            Reload();
        }
    }

    /// <summary>排序规则与 SQL 保持一致：未完成在前 -> 优先级高在前 -> 手动顺序 -> 创建顺序。</summary>
    private void Reorder()
    {
        var ordered = Items
            .OrderBy(i => i.IsDone)
            .ThenBy(i => 2 - i.Priority)
            .ThenBy(i => i.SortOrder)
            .ThenBy(i => i.Id)
            .ToList();

        for (var target = 0; target < ordered.Count; target++)
        {
            var current = Items.IndexOf(ordered[target]);
            if (current != target)
            {
                Items.Move(current, target);
            }
        }

        UpdateStats();
    }

    private void UpdateStats()
    {
        var total = Items.Count;
        var done = Items.Count(i => i.IsDone);
        HasItems = total > 0;
        HasNoDoneItems = done == 0;
        StatusText = total == 0 ? string.Empty : $"完成 {done} / {total}";
        CanUndoDelete = MemoStore.CountRecentlyDeleted() > 0;
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(HeaderSubText));
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
    }

    /// <summary>顶部大标题：正在看的那一天 + 星期，例如 10月20日 · 周二。</summary>
    public string HeaderText
    {
        get
        {
            var d = _historyDate ?? _today;
            return $"{d.Month}月{d.Day}日 · {WeekdayText(d)}";
        }
    }

    /// <summary>星期几的中文写法，星期一起头。</summary>
    private static string WeekdayText(DateOnly d) => d.DayOfWeek switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周日",
    };

    /// <summary>顶部副标题：只剩剩余数量（星期已经并进 HeaderText 了）。</summary>
    public string HeaderSubText
    {
        get
        {
            var remaining = Items.Count(i => !i.IsDone);

            // 过去的日子什么都没记就别报"已全部完成"，那是另一回事
            if (IsReadOnlyView && Items.Count == 0)
            {
                return string.Empty;
            }

            return remaining == 0
                ? (IsReadOnlyView ? "已全部完成" : string.Empty)
                : $"还剩 {remaining} 项";
        }
    }

    /// <summary>列表为空时的提示文字，今天 / 将来 / 过去各不相同。</summary>
    public string EmptyText => _historyDate switch
    {
        null => "今天还没有待办 ☕",
        { } d when d > _today => $"还没给 {d.Month}月{d.Day}日 安排事情",
        _ => "这天没有记录",
    };

    [RelayCommand]
    private void Add()
    {
        var text = NewContent.Trim();
        if (text.Length == 0)
        {
            return;
        }

        var item = new MemoItem
        {
            Content = text,
            Priority = NewPriority,
            IsDone = false,
            SortOrder = Items.Count == 0 ? 0 : Items.Max(i => i.SortOrder) + 1,
        };

        // 记在"正在看的这一天"，而不是一律记到今天 —— 提前安排就靠这个
        item.Id = MemoStore.Insert(item, EditingDate);

        Attach(item);
        Items.Add(item);
        NewContent = string.Empty;
        Reorder();
    }

    /// <summary>
    /// 切换完成状态。IsDone 的双向绑定已经负责落库和重排，
    /// 这里只在外部（命令）调用时翻转状态即可。
    /// </summary>
    [RelayCommand]
    private void ToggleDone(MemoItem? item)
    {
        if (item is null)
        {
            return;
        }

        item.IsDone = !item.IsDone;
    }

    /// <summary>把条目设为指定优先级（高/中/低），参数为 "2"/"1"/"0"。</summary>
    [RelayCommand]
    private void SetPriorityExact(object? parameter)
    {
        if (parameter is not object[] args || args.Length < 2 || args[0] is not MemoItem item)
        {
            return;
        }

        if (!int.TryParse(args[1]?.ToString(), out var target))
        {
            return;
        }

        item.Priority = Math.Clamp(target, 0, 2);
    }

    [RelayCommand]
    private void CyclePriority(MemoItem? item)
    {
        if (item is null)
        {
            return;
        }

        // 赋值后由 OnItemPropertyChanged 统一落库并重排
        item.Priority = item.Priority switch { 2 => 1, 1 => 0, _ => 2 };
    }

    [RelayCommand]
    private void BeginEdit(MemoItem? item)
    {
        if (item is null || item.IsDone)
        {
            return;
        }

        item.IsEditing = true;
        item.OriginalContent = item.Content;
    }

    [RelayCommand]
    private void CommitEdit(MemoItem? item)
    {
        if (item is null)
        {
            return;
        }

        item.IsEditing = false;
        var text = item.Content.Trim();
        if (text.Length == 0)
        {
            Delete(item);
            return;
        }

        item.Content = text;
        MemoStore.UpdateText(item);
    }

    /// <summary>失焦时结束编辑并保存，避免改完直接点别处丢内容。</summary>
    public void EndEdit(MemoItem? item)
    {
        if (item is { IsEditing: true })
        {
            CommitEdit(item);
        }
    }

    [RelayCommand]
    private void Delete(MemoItem? item)
    {
        if (item is null)
        {
            return;
        }

        MemoStore.Delete(item.Id);
        Detach(item);
        Items.Remove(item);
        UpdateStats();
    }

    /// <summary>清除所有已完成项。</summary>
    [RelayCommand]
    private void ClearDone()
    {
        var doneItems = Items.Where(i => i.IsDone).ToList();
        foreach (var item in doneItems)
        {
            MemoStore.Delete(item.Id);
            Detach(item);
            Items.Remove(item);
        }
        UpdateStats();
    }

    /// <summary>撤销最近一次删除，把记录还原回列表。</summary>
    [RelayCommand]
    private void UndoDelete()
    {
        if (MemoStore.UndoLastDelete() == 0)
        {
            return;
        }

        Reload();
    }

    public void SetNewPriority(int priority) => NewPriority = Math.Clamp(priority, 0, 2);
}
