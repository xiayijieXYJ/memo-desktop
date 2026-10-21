using System.Drawing;
using System.Windows.Forms;

namespace Memo;

/// <summary>
/// 托盘图标 + 右键菜单。用 WinForms 的 NotifyIcon（WPF 没有原生托盘 API），
/// 图标用代码画，避免额外资源文件。
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private readonly NotifyIcon _icon;
    private Icon? _ownedIcon;

    public event Action? ShowRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;

    public bool IsPinned { get; set; }

    public TrayIconManager()
    {
        _ownedIcon = CreateIcon();
        _icon = new NotifyIcon
        {
            Icon = _ownedIcon,
            Text = "Memo · 今天的待办",
            Visible = true,
        };

        var menu = new ContextMenuStrip();
        var showItem = new ToolStripMenuItem("显示卡片    Ctrl+Alt+M", null, (_, _) => ShowRequested?.Invoke());
        showItem.Font = new Font(showItem.Font, FontStyle.Bold);
        menu.Items.Add(showItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("设置…", null, (_, _) => SettingsRequested?.Invoke()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => ExitRequested?.Invoke()));

        _icon.ContextMenuStrip = menu;
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                // 左键单击 = 显示/隐藏切换，符合桌面便签的使用习惯
                ToggleRequested?.Invoke();
            }
        };
    }

    public event Action? ToggleRequested;

    public void ShowBalloon(string title, string text)
    {
        try
        {
            _icon.ShowBalloonTip(2000, title, text, ToolTipIcon.None);
        }
        catch
        {
            // 通知中心被关闭时可能失败，忽略
        }
    }

    /// <summary>
    /// 优先用 Assets/app.ico（和 exe 图标同一份），这样任务栏、托盘、窗口标题栏三处观感一致。
    /// 资源读不到时才退回 GDI+ 现画的图标，保证托盘不会空着。
    /// </summary>
    private static Icon CreateIcon()
    {
        if (TryLoadPackIcon(out var loaded))
        {
            return loaded;
        }

        return DrawFallbackIcon();
    }

    private static bool TryLoadPackIcon(out Icon icon)
    {
        icon = null!;
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute);
            var info = System.Windows.Application.GetResourceStream(uri);
            if (info is null)
            {
                return false;
            }

            using var stream = info.Stream;
            // 指定尺寸让 ico 里挑最合适的那一帧，避免缩小后发糊
            icon = new Icon(stream, SystemInformation.SmallIconSize);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>兜底：用 GDI+ 画一个圆角便签图标（深黄底 + 三行待办 + 右上角高亮点），配色跟 Assets/app.ico 一致。</summary>
    private static Icon DrawFallbackIcon()
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // 深黄底 D4A017
            using var back = new SolidBrush(Color.FromArgb(255, 212, 160, 23));
            using var path = RoundedRect(new Rectangle(1, 1, size - 2, size - 2), 7);
            g.FillPath(back, path);

            // 奶白待办线 FFFDF2
            using var lineBrush = new SolidBrush(Color.FromArgb(255, 255, 253, 242));
            // 三条待办短线
            g.FillRectangle(lineBrush, 7, 9, 12, 2.2f);
            g.FillRectangle(lineBrush, 7, 15, 16, 2.2f);

            using var doneBrush = new SolidBrush(Color.FromArgb(200, 255, 255, 255));
            g.FillRectangle(doneBrush, 7, 21, 9, 2.2f);

            // 右上角优先级高亮（深褐 7C2D12）
            using var dot = new SolidBrush(Color.FromArgb(255, 124, 45, 18));
            g.FillEllipse(dot, 23, 7, 5, 5);
        }

        // GetHicon 拿到的句柄要自己 DestroyIcon，所以先转成托管 Icon 再销毁
        var handle = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle rect, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        var d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _ownedIcon?.Dispose();
        _ownedIcon = null;
    }
}
