using System.Windows;

namespace Memo.Services;

/// <summary>
/// 往剪贴板写文字。
///
/// 剪贴板是全局独占的，别的进程正开着（远程桌面、输入法、各种同步工具都爱占）时
/// Clipboard.SetText 会抛 COMException，这不是"不该发生"，是正常会发生。
/// 所以统一在这里兜住，调用方只关心成没成，不用各自写 try/catch。
/// </summary>
public static class ClipboardHelper
{
    /// <summary>把文字放进剪贴板；空字符串直接算失败，免得把剪贴板清空。</summary>
    public static bool TrySetText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
