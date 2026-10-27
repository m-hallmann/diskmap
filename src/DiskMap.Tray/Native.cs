using System.Runtime.InteropServices;

namespace DiskMap.Tray;

internal static unsafe partial class Native
{
    public const uint WM_NULL = 0x0000, WM_DESTROY = 0x0002, WM_CLOSE = 0x0010, WM_TIMER = 0x0113, WM_CONTEXTMENU = 0x007B;
    public const uint WM_LBUTTONUP = 0x0202, WM_APP = 0x8000;
    public const uint NIN_SELECT = 0x0400, NIN_KEYSELECT = 0x0401, NIN_BALLOONUSERCLICK = 0x0405;
    public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    public const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    public const uint NIIF_WARNING = 0x2, NIIF_USER = 0x4, NIIF_LARGE_ICON = 0x20;
    public const uint MF_STRING = 0x0, MF_GRAYED = 0x1, MF_SEPARATOR = 0x800, MF_DEFAULT = 0x1000;
    public const uint TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 0x2, TPM_BOTTOMALIGN = 0x20;
    public const uint WS_POPUP = 0x80000000;

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public delegate* unmanaged<nint, uint, nint, nint, nint> lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public char* lpszMenuName, lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam, lParam;
        public uint time;
        public int x, y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public nint hIcon;
        public fixed char szTip[128];
        public uint dwState, dwStateMask;
        public fixed char szInfo[256];
        public uint uVersion;
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ICONINFO
    {
        public int fIcon, xHotspot, yHotspot;
        public nint hbmMask, hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW")]
    public static partial ushort RegisterClassEx(WNDCLASSEXW* wc);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int w, int h,
        nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    public static partial nint DefWindowProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    public static partial int GetMessage(MSG* msg, nint hwnd, uint min, uint max);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(MSG* msg);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    public static partial nint DispatchMessage(MSG* msg);

    [LibraryImport("user32.dll")]
    public static partial void PostQuitMessage(int code);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterWindowMessage(string name);

    [LibraryImport("user32.dll")]
    public static partial nuint SetTimer(nint hwnd, nuint id, uint ms, nint proc);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool KillTimer(nint hwnd, nuint id);

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Shell_NotifyIcon(uint message, NOTIFYICONDATAW* data);

    [LibraryImport("shell32.dll", EntryPoint = "ExtractIconExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint ExtractIconEx(string file, int index, nint* large, nint* small, uint count);

    [LibraryImport("user32.dll")]
    public static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AppendMenu(nint menu, uint flags, nuint id, string? text);

    [LibraryImport("user32.dll")]
    public static partial int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint tpm);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyMenu(nint menu);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(POINT* p);

    [LibraryImport("user32.dll")]
    public static partial nint CreateIconIndirect(ICONINFO* info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(nint icon);

    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetricsForDpi(int index, uint dpi);

    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForSystem();

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateDIBSection(nint hdc, BITMAPINFOHEADER* bmi, uint usage, void** bits, nint section, uint offset);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateBitmap(int w, int h, uint planes, uint bitCount, void* bits);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint obj);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW")]
    public static partial nint GetModuleHandle(nint name);

    public static void Copy(char* dest, int capacity, string text)
    {
        int n = Math.Min(text.Length, capacity - 1);
        for (int i = 0; i < n; i++) dest[i] = text[i];
        dest[n] = '\0';
    }
}
