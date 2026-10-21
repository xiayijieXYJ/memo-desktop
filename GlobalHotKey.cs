using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Memo;

/// <summary>
/// 全局热键 Ctrl+Alt+M：无论当前在哪个程序里，都能把卡片唤到最前面。
/// 用 RegisterHotKey + HwndSource 钩 WM_HOTKEY。
/// </summary>
public sealed class GlobalHotKey : IDisposable
{
    private const int WmHotKey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;

    private HwndSource? _source;
    private readonly int _id;
    private bool _registered;

    public event Action? Pressed;

    public bool IsRegistered => _registered;

    public GlobalHotKey(int id = 0xA17E)
    {
        _id = id;
    }

    public void Register(Window window)
    {
        var helper = new WindowInteropHelper(window);
        _source = HwndSource.FromHwnd(helper.Handle);
        if (_source is null)
        {
            return;
        }

        _source.AddHook(WndProc);
        _registered = RegisterHotKey(helper.Handle, _id, ModControl | ModAlt | ModNoRepeat, 0x4D /* M */);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotKey && wParam.ToInt32() == _id)
        {
            Pressed?.Invoke();
            handled = true;
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public void Dispose()
    {
        if (_source is not null)
        {
            if (_registered)
            {
                UnregisterHotKey(_source.Handle, _id);
                _registered = false;
            }
            _source.RemoveHook(WndProc);
            _source = null;
        }
    }
}
