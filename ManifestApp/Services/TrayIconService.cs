using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace ManifestApp.Services;

/// <summary>
/// Native Win32 system-tray icon using Shell_NotifyIcon.
/// Runs a hidden HWND_MESSAGE window on a dedicated background STA thread.
/// Double-click the icon to open the app; right-click for the context menu.
/// </summary>
internal sealed class TrayIconService : IDisposable
{
    // ── Win32 constants ───────────────────────────────────────────────────────
    private const uint WM_TRAYICON      = 0x8001; // WM_APP + 1
    private const uint WM_DESTROY       = 0x0002;
    private const uint NIM_ADD          = 0x0000;
    private const uint NIM_DELETE       = 0x0002;
    private const uint NIF_MESSAGE      = 0x0001;
    private const uint NIF_ICON         = 0x0002;
    private const uint NIF_TIP          = 0x0004;
    private const uint WM_RBUTTONUP     = 0x0205;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint MF_STRING        = 0x0000;
    private const uint MF_SEPARATOR     = 0x0800;
    private const uint TPM_RETURNCMD    = 0x0100;
    private const uint TPM_RIGHTBUTTON  = 0x0002;
    private const uint IMAGE_ICON       = 0x0001;
    private const uint LR_LOADFROMFILE  = 0x0010;
    private const nint HWND_MESSAGE     = -3;

    private const uint IDM_OPEN         = 1;
    private const uint IDM_CHECKUPDATES = 2;
    private const uint IDM_EXIT         = 3;

    // ── P/Invoke structs ──────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint  cbSize;
        public nint  hWnd;
        public uint  uID;
        public uint  uFlags;
        public uint  uCallbackMessage;
        public nint  hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint  dwState;
        public uint  dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint  uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]  public string szInfoTitle;
        public uint  dwInfoFlags;
        public Guid  guidItem;
        public nint  hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint   cbSize;
        public uint   style;
        public nint   lpfnWndProc;
        public int    cbClsExtra;
        public int    cbWndExtra;
        public nint   hInstance;
        public nint   hIcon;
        public nint   hCursor;
        public nint   hbrBackground;
        public nint   lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public nint   hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint  hwnd;
        public uint  message;
        public nint  wParam;
        public nint  lParam;
        public uint  time;
        public POINT pt;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate nint WndProcDelegate(nint hWnd, uint uMsg, nint wParam, nint lParam);

    // ── P/Invoke declarations ─────────────────────────────────────────────────

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATA pnid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProcW(nint hWnd, uint uMsg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG lpMsg, nint hWnd, uint wMin, uint wMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadImageW(nint hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint hIcon);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(nint hMenu, uint uFlags, nint uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(nint hMenu, uint uFlags, int x, int y,
        int nReserved, nint hWnd, nint prcRect);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(nint hMenu);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? lpModuleName);

    // ── Fields ────────────────────────────────────────────────────────────────

    private readonly DispatcherQueue _uiQueue;
    private readonly Action          _onOpen;
    private readonly Action          _onCheckUpdates;
    private readonly Action          _onExit;

    private nint     _hwnd;
    private nint     _hIcon;
    private GCHandle _wndProcHandle;
    private bool     _disposed;

    // ── Constructor ───────────────────────────────────────────────────────────

    internal TrayIconService(DispatcherQueue uiQueue,
        Action onOpen, Action onCheckUpdates, Action onExit)
    {
        _uiQueue        = uiQueue;
        _onOpen         = onOpen;
        _onCheckUpdates = onCheckUpdates;
        _onExit         = onExit;

        var thread = new Thread(RunMessageLoop)
        {
            IsBackground = true,
            Name         = "TrayIconMsgPump",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    // ── Background STA message pump ───────────────────────────────────────────

    private void RunMessageLoop()
    {
        var iconPath = AppIconPaths.ResolveIconPath();
        _hIcon = File.Exists(iconPath)
            ? LoadImageW(0, iconPath, IMAGE_ICON, 16, 16, LR_LOADFROMFILE)
            : 0;

        var hInstance = GetModuleHandleW(null);
        var className = $"TrayWnd_{Environment.ProcessId}";

        WndProcDelegate wndProc = WndProc;
        _wndProcHandle = GCHandle.Alloc(wndProc); // prevent GC collection

        var wc = new WNDCLASSEXW
        {
            cbSize        = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc   = Marshal.GetFunctionPointerForDelegate(wndProc),
            hInstance     = hInstance,
            lpszClassName = className,
        };

        if (RegisterClassExW(ref wc) == 0)
        {
            _wndProcHandle.Free();
            return;
        }

        // HWND_MESSAGE: message-only window — never visible, no taskbar entry
        _hwnd = CreateWindowExW(0, className, "TrayMsgWnd", 0,
            0, 0, 0, 0, HWND_MESSAGE, 0, hInstance, 0);

        if (_hwnd == 0)
        {
            _wndProcHandle.Free();
            return;
        }

        var nid = BuildNid();
        Shell_NotifyIconW(NIM_ADD, ref nid);

        while (GetMessageW(out var msg, 0, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }

        // Cleanup after the loop exits (triggered by WM_DESTROY → PostQuitMessage)
        Shell_NotifyIconW(NIM_DELETE, ref nid);
        if (_hIcon != 0) DestroyIcon(_hIcon);
        _wndProcHandle.Free();
    }

    private NOTIFYICONDATA BuildNid() => new()
    {
        cbSize           = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd             = _hwnd,
        uID              = 1,
        uFlags           = NIF_MESSAGE | NIF_ICON | NIF_TIP,
        uCallbackMessage = WM_TRAYICON,
        hIcon            = _hIcon,
        szTip            = "OpenSteam App",
    };

    // ── Window procedure ──────────────────────────────────────────────────────

    private nint WndProc(nint hWnd, uint uMsg, nint wParam, nint lParam)
    {
        if (uMsg == WM_TRAYICON)
        {
            var ev = (uint)(lParam.ToInt64() & 0xFFFF);

            if (ev == WM_LBUTTONDBLCLK)
                _uiQueue.TryEnqueue(() => _onOpen());
            else if (ev == WM_RBUTTONUP)
                ShowContextMenu(hWnd);

            return 0;
        }

        if (uMsg == WM_DESTROY)
        {
            PostQuitMessage(0);
            return 0;
        }

        return DefWindowProcW(hWnd, uMsg, wParam, lParam);
    }

    private void ShowContextMenu(nint hWnd)
    {
        GetCursorPos(out var pt);
        SetForegroundWindow(hWnd); // required so the menu dismisses on click-away

        var hMenu = CreatePopupMenu();
        AppendMenuW(hMenu, MF_STRING,    (nint)IDM_OPEN,         "Open OpenSteam App");
        AppendMenuW(hMenu, MF_STRING,    (nint)IDM_CHECKUPDATES, "Check for updates");
        AppendMenuW(hMenu, MF_SEPARATOR, 0,                       null);
        AppendMenuW(hMenu, MF_STRING,    (nint)IDM_EXIT,          "Exit");

        var cmd = TrackPopupMenu(hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON,
            pt.x, pt.y, 0, hWnd, 0);
        DestroyMenu(hMenu);

        switch (cmd)
        {
            case IDM_OPEN:         _uiQueue.TryEnqueue(() => _onOpen());         break;
            case IDM_CHECKUPDATES: _uiQueue.TryEnqueue(() => _onCheckUpdates()); break;
            case IDM_EXIT:         _uiQueue.TryEnqueue(() => _onExit());         break;
        }
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var hwnd = _hwnd;
        if (hwnd != 0)
            PostMessageW(hwnd, WM_DESTROY, 0, 0); // tells the message pump to quit
    }
}
