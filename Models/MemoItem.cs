using CommunityToolkit.Mvvm.ComponentModel;

namespace Memo.Models;

/// <summary>
/// 一条待办。优先级约定：2 = 高、1 = 中、0 = 低。
/// </summary>
public partial class MemoItem : ObservableObject
{
    public long Id { get; set; }

    public int SortOrder { get; set; }

    public string CreatedAt { get; set; } = string.Empty;

    public string? DoneAt { get; set; }

    /// <summary>这条事安排在哪一天（yyyy-MM-dd）。月度总结里要显示"哪天的安排"。</summary>
    public string? CreatedDate { get; set; }

    /// <summary>哪一天完成的（yyyy-MM-dd）；未完成时为 null。</summary>
    public string? DoneDate { get; set; }

    [ObservableProperty]
    private string _content = string.Empty;

    [ObservableProperty]
    private int _priority = 1;

    [ObservableProperty]
    private bool _isDone;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private int _carriedDays;

    /// <summary>进入编辑时的原文快照，按 Esc 取消时用它还原。</summary>
    public string OriginalContent { get; set; } = string.Empty;

    public string PriorityText => Priority switch
    {
        2 => "高",
        0 => "低",
        _ => "中",
    };

    public string PriorityKey => Priority switch
    {
        2 => "High",
        0 => "Low",
        _ => "Normal",
    };

    /// <summary>该条目创建于今天之前且尚未完成时，显示"顺延 N 天"。</summary>
    public string CarriedText => !IsDone && CarriedDays > 0 ? $"顺延{CarriedDays}天" : string.Empty;

    partial void OnPriorityChanged(int value)
    {
        OnPropertyChanged(nameof(PriorityText));
        OnPropertyChanged(nameof(PriorityKey));
    }

    partial void OnCarriedDaysChanged(int value) => OnPropertyChanged(nameof(CarriedText));

    partial void OnIsDoneChanged(bool value) => OnPropertyChanged(nameof(CarriedText));
}
