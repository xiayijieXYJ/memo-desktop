namespace Memo.Models;

/// <summary>
/// 月度总结里的一行。只读快照：文字和日期在生成时一次性格式化好，
/// XAML 直接绑，不用为「M月d日」「顺延N天」这几个写法各写一个转换器。
/// </summary>
public sealed class SummaryEntry
{
    public string Content { get; init; } = string.Empty;

    /// <summary>优先级徽章文字：高 / 中 / 低。</summary>
    public string PriorityText { get; init; } = "中";

    /// <summary>优先级样式键 High / Normal / Low，XAML 用 DataTrigger 换徽章配色。</summary>
    public string PriorityKey { get; init; } = "Normal";

    /// <summary>右侧那一小列：完成的是「10月21日」，没完成的是「今天」「顺延3天」或「10月25日」。</summary>
    public string DateText { get; init; } = string.Empty;

    public bool IsDone { get; init; }

    /// <summary>
    /// ItemsControl 给每个行容器取可访问性名字时用的是数据对象的 ToString()，
    /// 不重写的话读屏和 UIA 里那一行会显示成 "Memo.Models.SummaryEntry"。
    /// </summary>
    public override string ToString() => Content;
}
