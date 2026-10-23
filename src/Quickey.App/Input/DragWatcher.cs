using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Quickey.App.Native;
using Quickey.Core.Settings;

namespace Quickey.App.Input;

public sealed class ModifierPressedEventArgs(ModifierKey key, Win32.POINT cursor) : EventArgs
{
    public ModifierKey Key { get; } = key;
    public Win32.POINT Cursor { get; } = cursor;
}

/// <summary>
/// Watches system-wide input with low-level hooks to spot "the user is dragging something and just
/// tapped Shift/Ctrl/Alt". It never swallows input: Explorer still sees every key and click.
/// </summary>
/// <remarks>
/// A low-level hook can't tell a file drag from a rubber-band selection, so the wheel may open for
/// either. That's harmless: with no files dropped on it, the wheel closes when the button is released.
/// </remarks>
public sealed class DragWatcher : IDisposable
{
    /// <summary>Top-level window classes of File Explorer and the desktop.</summary>
    private static readonly HashSet<string> ExplorerClasses = ["CabinetWClass", "ExploreWClass", "Progman", "WorkerW"];

    // Hook callbacks are invoked by Windows on this thread; the delegates must stay referenced.
    private readonly Win32.HookProc _mouseProc;
    private readonly Win32.HookProc _keyboardProc;
    private readonly Dispatcher _dispatcher;
    private readonly Func<bool> _explorerOnly;
    private IntPtr _mouseHook;
    private IntPtr _keyboardHook;

    private bool _buttonDown;
    private bool _dragging;
    private bool _sourceAllowed;
    private Win32.POINT _downAt;
    private readonly HashSet<uint> _heldKeys = [];

    public DragWatcher(Dispatcher dispatcher, Func<bool> explorerOnly)
    {
        _dispatcher = dispatcher;
        _explorerOnly = explorerOnly;
        _mouseProc = OnMouse;
        _keyboardProc = OnKeyboard;
    }

    /// <summary>A modifier was tapped mid-drag. Raised on the UI thread.</summary>
    public event EventHandler<ModifierPressedEventArgs>? ModifierPressed;

    /// <summary>The mouse button was released, ending any drag. Raised on the UI thread.</summary>
    public event EventHandler? DragEnded;

    /// <summary>Escape was pressed mid-drag. Raised on the UI thread.</summary>
    public event EventHandler? Cancelled;

    public bool IsRunning => _mouseHook != IntPtr.Zero;

    /// <summary>Lets the wheel window exclude itself: drags that start on it are not file drags.</summary>
    public Func<Win32.POINT, bool>? IgnorePoint { get; set; }

    public void Start()
    {
        if (IsRunning)
            return;
        var module = Win32.GetModuleHandle(Process.GetCurrentProcess().MainModule?.ModuleName);
        _mouseHook = Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL, _mouseProc, module, 0);
        _keyboardHook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _keyboardProc, module, 0);
        if (_mouseHook == IntPtr.Zero || _keyboardHook == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            Stop();
            throw new InvalidOperationException($"Couldn't install input hooks (Win32 error {error}).");
        }
    }

    public void Stop()
    {
        if (_mouseHook != IntPtr.Zero)
            Win32.UnhookWindowsHookEx(_mouseHook);
        if (_keyboardHook != IntPtr.Zero)
            Win32.UnhookWindowsHookEx(_keyboardHook);
        _mouseHook = _keyboardHook = IntPtr.Zero;
        _buttonDown = _dragging = false;
    }

    public void Dispose() => Stop();

    private IntPtr OnMouse(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);
            switch ((int)wParam)
            {
                case Win32.WM_LBUTTONDOWN:
                    _buttonDown = true;
                    _dragging = false;
                    _downAt = info.pt;
                    _sourceAllowed = IgnorePoint?.Invoke(info.pt) != true
                        && (!_explorerOnly() || ExplorerClasses.Contains(Win32.RootClassAt(info.pt)));
                    break;

                case Win32.WM_MOUSEMOVE when _buttonDown && !_dragging:
                    // Same threshold Windows uses before it starts a drag.
                    var dx = Math.Abs(info.pt.X - _downAt.X);
                    var dy = Math.Abs(info.pt.Y - _downAt.Y);
                    _dragging = dx > System.Windows.SystemParameters.MinimumHorizontalDragDistance
                             || dy > System.Windows.SystemParameters.MinimumVerticalDragDistance;
                    break;

                case Win32.WM_LBUTTONUP:
                    var wasDragging = _dragging;
                    _buttonDown = _dragging = false;
                    if (wasDragging)
                        _dispatcher.BeginInvoke(() => DragEnded?.Invoke(this, EventArgs.Empty));
                    break;
            }
        }
        return Win32.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private IntPtr OnKeyboard(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
            var message = (int)wParam;
            var isDown = message is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN;
            var isUp = message is Win32.WM_KEYUP or Win32.WM_SYSKEYUP;

            if (isUp)
            {
                _heldKeys.Remove(info.vkCode);
            }
            else if (isDown && _heldKeys.Add(info.vkCode) && _dragging && _sourceAllowed)
            {
                // _heldKeys.Add returning false means auto-repeat of a key that's already down.
                if (info.vkCode == Win32.VK_ESCAPE)
                {
                    _dispatcher.BeginInvoke(() => Cancelled?.Invoke(this, EventArgs.Empty));
                }
                else if (ToModifier(info.vkCode) is { } key)
                {
                    Win32.GetCursorPos(out var cursor);
                    _dispatcher.BeginInvoke(() => ModifierPressed?.Invoke(this, new ModifierPressedEventArgs(key, cursor)));
                }
            }
        }
        return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    public static ModifierKey? ToModifier(uint vk) => vk switch
    {
        Win32.VK_LSHIFT or Win32.VK_RSHIFT => ModifierKey.Shift,
        Win32.VK_LCONTROL or Win32.VK_RCONTROL => ModifierKey.Ctrl,
        Win32.VK_LMENU or Win32.VK_RMENU => ModifierKey.Alt,
        _ => null,
    };
}
