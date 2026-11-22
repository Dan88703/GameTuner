using System.Runtime.InteropServices;

namespace GameTuner.App.Services;

/// <summary>
/// Notification-area (tray) icon built directly on the Win32 API.
///
/// WinUI 3 has no tray icon control. Instead of a third-party package, this
/// class does what every Win32 app does:
///   1. registers a window class and creates a hidden window — Windows sends
///      tray clicks to a window, so we need one to receive them;
///   2. calls Shell_NotifyIcon(NIM_ADD) with that window's handle and a custom
///      callback message id;
///   3. in the window procedure, reacts to that message (left click = open,
///      right click = context menu).
/// The hidden window is created on the UI thread, so its messages arrive on
/// the UI thread too (WinUI runs a normal Win32 message loop underneath).
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WM_APP_TRAY = 0x8000 + 1;   // WM_APP + 1: our callback message id
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_NULL = 0x0000;

    private const int MenuOpen = 1, MenuAutoMode = 2, MenuExit = 3;

    private readonly Action _onOpen;
    private readonly Func<bool> _isAutoModeOn;
    private readonly Action _onToggleAutoMode;
    private readonly Action _onExit;
    private readonly string _tooltip;

    // The delegate must be stored in a field: Windows keeps a raw pointer to it,
    // and if the GC collected it the app would crash on the next message.
    private readonly WndProc _wndProc;
    private readonly IntPtr _hwnd;
    private readonly IntPtr _icon;
    private readonly uint _taskbarCreatedMsg;
    private bool _disposed;

    public TrayIcon(string iconPath, string tooltip, Action onOpen, Func<bool> isAutoModeOn,
                    Action onToggleAutoMode, Action onExit)
    {
        _tooltip = tooltip;
        _onOpen = onOpen;
        _isAutoModeOn = isAutoModeOn;
        _onToggleAutoMode = onToggleAutoMode;
        _onExit = onExit;
        _wndProc = WindowProc;

        // Explorer broadcasts this message after it restarts (e.g. after a crash);
        // the tray is empty then, so we must add our icon again.
        _taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");

        var hInstance = GetModuleHandle(null);
        string className = "GameTunerTrayWindow_" + Environment.ProcessId;
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            hInstance = hInstance,
            lpszClassName = className
        };
        RegisterClassEx(ref wc);
        _hwnd = CreateWindowEx(0, className, "GameTuner Tray", 0, 0, 0, 0, 0,
                               IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        _icon = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
        AddIcon();
    }

    /// <summary>Shows a Windows notification next to the tray icon. Safe from any thread.</summary>
    public void ShowNotification(string title, string text)
    {
        if (_disposed) return;
        var data = CreateData();
        data.uFlags = NIF_INFO;
        data.szInfoTitle = Truncate(title, 63);
        data.szInfo = Truncate(text, 255);
        data.dwInfoFlags = NIIF_INFO;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private void AddIcon()
    {
        var data = CreateData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.uCallbackMessage = WM_APP_TRAY;
        data.hIcon = _icon;
        data.szTip = Truncate(_tooltip, 127);
        Shell_NotifyIcon(NIM_ADD, ref data);
    }

    private NOTIFYICONDATA CreateData() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        szTip = "",
        szInfo = "",
        szInfoTitle = ""
    };

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_APP_TRAY)
        {
            // In the legacy (pre-version-4) mode, lParam holds the mouse message.
            switch ((int)lParam)
            {
                case WM_LBUTTONUP:
                    _onOpen();
                    break;
                case WM_RBUTTONUP:
                    ShowMenu();
                    break;
            }
            return IntPtr.Zero;
        }

        if (msg == _taskbarCreatedMsg && _taskbarCreatedMsg != 0)
        {
            AddIcon();
            return IntPtr.Zero;
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        IntPtr menu = CreatePopupMenu();
        AppendMenu(menu, MF_STRING, MenuOpen, "Открыть GameTuner");
        AppendMenu(menu, MF_STRING | (_isAutoModeOn() ? MF_CHECKED : 0), MenuAutoMode, "Авто-режим");
        AppendMenu(menu, MF_SEPARATOR, 0, null);
        AppendMenu(menu, MF_STRING, MenuExit, "Выход");

        GetCursorPos(out POINT p);

        // Documented quirk: without SetForegroundWindow the menu does not close
        // when the user clicks elsewhere, and without the WM_NULL afterwards
        // it may need two clicks next time.
        SetForegroundWindow(_hwnd);
        int command = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, p.X, p.Y, 0, _hwnd, IntPtr.Zero);
        PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(menu);

        switch (command)
        {
            case MenuOpen: _onOpen(); break;
            case MenuAutoMode: _onToggleAutoMode(); break;
            case MenuExit: _onExit(); break;
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var data = CreateData();
        Shell_NotifyIcon(NIM_DELETE, ref data);
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
        DestroyWindow(_hwnd);
    }

    // ------------------------------------------------------------------
    // Win32 interop
    // ------------------------------------------------------------------

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const int NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10;
    private const int NIIF_INFO = 0x1;
    private const uint IMAGE_ICON = 1, LR_LOADFROMFILE = 0x10, LR_DEFAULTSIZE = 0x40;
    private const uint MF_STRING = 0x0, MF_CHECKED = 0x8, MF_SEPARATOR = 0x800;
    private const uint TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 0x2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint load);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, int id, string? text);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hWnd, IntPtr rect);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
