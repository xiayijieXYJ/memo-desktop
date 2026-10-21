using System;
using System.Windows;

namespace Memo.Services;

/// <summary>
/// 主题切换。做法是把 App.xaml 里 MergedDictionaries 的第 0 项（主题字典）
/// 整个换掉，界面颜色全部走 DynamicResource，换完立刻生效，不用重启。
/// </summary>
public static class ThemeManager
{
    public const string Dark = "Dark";
    public const string Light = "Light";
    public const string Sepia = "Sepia";

    /// <summary>主题目录，字典文件名与主题名一一对应。</summary>
    private const string DictionaryPathFormat = "Themes/{0}.xaml";

    /// <summary>按这个顺序轮换：深色 → 浅色 → 浅黄色 → 深色。</summary>
    private static readonly string[] Order = { Dark, Light, Sepia };

    /// <summary>主题切换后触发，给需要手动刷新的地方（如托盘图标）用。</summary>
    public static event Action? ThemeChanged;

    public static string Current { get; private set; } = Dark;

    public static bool IsLight => string.Equals(Current, Light, StringComparison.OrdinalIgnoreCase);

    public static bool IsSepia => string.Equals(Current, Sepia, StringComparison.OrdinalIgnoreCase);

    /// <summary>把配置里的主题名规范成已知值，脏数据一律回落深色。</summary>
    public static string Normalize(string? theme)
    {
        if (string.Equals(theme, Light, StringComparison.OrdinalIgnoreCase))
        {
            return Light;
        }

        if (string.Equals(theme, Sepia, StringComparison.OrdinalIgnoreCase))
        {
            return Sepia;
        }

        return Dark;
    }

    /// <summary>
    /// 应用主题。应用资源还没建立时（比如只有 --selftest 的控制台流程）直接跳过，
    /// 只记下当前值，不当成错误。
    /// </summary>
    public static void Apply(string? theme)
    {
        var wanted = Normalize(theme);
        Current = wanted;

        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        try
        {
            var dict = new ResourceDictionary
            {
                Source = new Uri(DictionaryPathFormat.Replace("{0}", wanted), UriKind.Relative),
            };

            var merged = app.Resources.MergedDictionaries;
            if (merged.Count == 0)
            {
                merged.Add(dict);
            }
            else
            {
                merged[0] = dict;
            }
        }
        catch
        {
            // 字典加载失败不应该让程序起不来；界面会退回透明/默认色，但不崩。
        }

        ThemeChanged?.Invoke();
    }

    /// <summary>按 Order 往后轮一个主题，返回切换后的主题名。</summary>
    public static string Toggle()
    {
        var idx = Array.FindIndex(Order, t => string.Equals(t, Current, StringComparison.OrdinalIgnoreCase));
        var next = Order[(idx < 0 ? 0 : idx + 1) % Order.Length];
        Apply(next);
        return Current;
    }
}
