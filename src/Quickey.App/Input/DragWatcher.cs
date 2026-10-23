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
/// <para>
/// The hooks live on their own thread with nothing but a message loop. Windows silently removes a
/// low-level hook whose thread doesn't answer within LowLevelHooksTimeout (about a second), and the
/// UI thread can easily stall that long (first wheel layout, a drop handler, Office starting up).
/// A watchdog also reinstalls the hooks if they disappear anyway (e.g. after sleep or a lock screen).
/// </para>
/// <para>
/// A low-level hook can't tell a file drag from a rubber-band selection, so the wheel may open for
/// either. That's harmless: with no files dropped on it, the wheel closes when the button is released.
/// </para>
/// </remarks>
public sealed class DragWatcher : IDisposable
{
    /// <summary>Top-level window classes of File Explorer and the desktop.</summary>
    private static readonly HashSet<string> ExplorerClasses = ["CabinetWClass", "ExploreWClass", "Progman", "WorkerW"];

    private const uint WatchdogMs = 2000;
    private const uint ReinstallMessage = Win32.WM_APP + 1;

    // Hook callbacks are invoked by Windows on the hook thread; the delegates must stay referenced.
    private readonly Win32.HookProc _mouseProc;
    private readonly Win32.HookProc _keyboardProc;
    private readonly Dispatcher _dispatcher;
    private readonly Func<bool> _explorerOnly;

    private Thread? _thread;
    private uint _threadId;
    private IntPtr _mouseHook;
    private IntPtr _keyboardHook;

    // Hook-thread state.
    private bool _buttonDown;
    private bool _dragging;
    private bool _sourceAllowed;
    private Win32.POINT _downAt;
    private readonly HashSet<uint> _heldKeys = [];
    private long _lastMouseEventTicks;
    private Win32.POINT _lastHookPoint;

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

    public bool IsRunning => _thread is not null;

    /// <summary>
    /// Lets the wheel window exclude itself: drags that start on it are not file drags.
    /// Called on the hook thread, so it must be thread-safe.
    /// </summary>
    public Func<Win32.POINT, bool>? IgnorePoint { get; set; }

    /// <summary>Starts the hook thread. Throws if Windows refuses the hooks.</summary>
    public void Start()
    {
        if (IsRunning)
            return;

        var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() => Run(started)) { IsBackground = true, Name = "Quickey input hooks" };
        _thread.Start();

        var error = started.Task.GetAwaiter().GetResult();
        if (error != 0)
        {
            _thread.Join();
            _thread = null;
            throw new InvalidOperationException($"Couldn't install input hooks (Win32 error {error}).");
        }
    }

    public void Stop()
    {
        if (_thread is null)
            return;
        Win32.PostThreadMessage(_threadId, Win32.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }

    /// <summary>Reinstalls the hooks, e.g. after resume from sleep. Safe to call any time.</summary>
    public void Reinstall()
    {
        if (_thread is not null)
            Win32.PostThreadMessage(_threadId, ReinstallMessage, IntPtr.Zero, IntPtr.Zero);
    }

    public void Dispose() => Stop();

    private void Run(TaskCompletionSource<int> started)
    {
        _threadId = Win32.GetCurrentThreadId();
        var error = Install();
        started.SetResult(error);
        if (error != 0)
            return;

        var timer = Win32.SetTimer(IntPtr.Zero, UIntPtr.Zero, WatchdogMs, IntPtr.Zero);
        while (Win32.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == Win32.WM_TIMER)
                Watchdog();
            else if (msg.message == ReinstallMessage)
                Install();
        }
        Win32.KillTimer(IntPtr.Zero, timer);
        Uninstall();
    }

    private int Install()
    {
        Uninstall();
        // For low-level hooks the module handle only has to be non-null; the process image is fine.
        var module = Win32.GetModuleHandle(null);
        _mouseHook = Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL, _mouseProc, module, 0);
        _keyboardHook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _keyboardProc, module, 0);
        if (_mouseHook != IntPtr.Zero && _keyboardHook != IntPtr.Zero)
        {
            _buttonDown = _dragging = false;
            _heldKeys.Clear();
            _lastMouseEventTicks = Environment.TickCount64;
            Win32.GetCursorPos(out _lastHookPoint);
            return 0;
        }
        var error = Marshal.GetLastWin32Error();
        Uninstall();
        return error == 0 ? -1 : error;
    }

    private void Uninstall()
    {
        if (_mouseHook != IntPtr.Zero)
            Win32.UnhookWindowsHookEx(_mouseHook);
        if (_keyboardHook != IntPtr.Zero)
            Win32.UnhookWindowsHookEx(_keyboardHook);
        _mouseHook = _keyboardHook = IntPtr.Zero;
    }

    /// <summary>
    /// If the pointer has moved but the mouse hook hasn't heard about it, Windows has dropped
    /// the hooks. There's no notification for that, so this is the only way to find out.
    /// </summary>
    private void Watchdog()
    {
        if (!Win32.GetCursorPos(out var cursor))
            return;
        var moved = cursor.X != _lastHookPoint.X || cursor.Y != _lastHookPoint.Y;
        var silentFor = Environment.TickCount64 - _lastMouseEventTicks;
        if (moved && silentFor > WatchdogMs)
            Install();
    }

    private IntPtr OnMouse(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
                HandleMouse((int)wParam, Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam));
        }
        catch (Exception)
        {
            // An exception must never escape into Windows' hook chain; just skip this event.
        }
        return Win32.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private void HandleMouse(int message, Win32.MSLLHOOKSTRUCT info)
    {
        _lastMouseEventTicks = Environment.TickCount64;
        _lastHookPoint = info.pt;

        switch (message)
        {
            case Win32.WM_LBUTTONDOWN:
                _buttonDown = true;
                _dragging = false;
                _downAt = info.pt;
                _sourceAllowed = IgnorePoint?.Invoke(info.pt) != true
                    && (!_explorerOnly() || ExplorerClasses.Contains(Win32.RootClassAt(info.pt)));
                ResyncHeldKeys();
                break;

            case Win32.WM_MOUSEMOVE when _buttonDown && !_dragging:
                // Same threshold Windows uses before it starts a drag.
                _dragging = Math.Abs(info.pt.X - _downAt.X) > Win32.GetSystemMetrics(Win32.SM_CXDRAG)
                         || Math.Abs(info.pt.Y - _downAt.Y) > Win32.GetSystemMetrics(Win32.SM_CYDRAG);
                break;

            case Win32.WM_LBUTTONUP:
                var wasDragging = _dragging;
                _buttonDown = _dragging = false;
                if (wasDragging)
                    _dispatcher.BeginInvoke(() => DragEnded?.Invoke(this, EventArgs.Empty));
                break;
        }
    }

    /// <summary>
    /// Forgets keys that are no longer physically down. A key-up can be missed (lock screen, UAC
    /// prompt, a moment without hooks), and a key stuck in the "held" set would never fire again.
    /// </summary>
    private void ResyncHeldKeys() =>
        _heldKeys.RemoveWhere(vk => (Win32.GetAsyncKeyState((int)vk) & 0x8000) == 0);

    private IntPtr OnKeyboard(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
                HandleKey((int)wParam, Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam));
        }
        catch (Exception)
        {
            // See OnMouse.
        }
        return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private void HandleKey(int message, Win32.KBDLLHOOKSTRUCT info)
    {
        if (message is Win32.WM_KEYUP or Win32.WM_SYSKEYUP)
        {
            _heldKeys.Remove(info.vkCode);
            return;
        }
        if (message is not (Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN))
            return;

        // Add returns false while a key is auto-repeating: only the first press counts.
        if (!_heldKeys.Add(info.vkCode) || !_dragging || !_sourceAllowed)
            return;

        if (info.vkCode == Win32.VK_ESCAPE)
        {
            _dispatcher.BeginInvoke(() => Cancelled?.Invoke(this, EventArgs.Empty));
        }
        else if (ToModifier(info.vkCode) is { } key)
        {
            var cursor = _lastHookPoint;
            _dispatcher.BeginInvoke(() => ModifierPressed?.Invoke(this, new ModifierPressedEventArgs(key, cursor)));
        }
    }

    public static ModifierKey? ToModifier(uint vk) => vk switch
    {
        Win32.VK_LSHIFT or Win32.VK_RSHIFT => ModifierKey.Shift,
        Win32.VK_LCONTROL or Win32.VK_RCONTROL => ModifierKey.Ctrl,
        Win32.VK_LMENU or Win32.VK_RMENU => ModifierKey.Alt,
        _ => null,
    };
}
