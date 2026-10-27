using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DiskMap.Core;
using DiskMap.Core.History;
using DiskMap.Core.Native;
using DiskMap.Core.Settings;
using Microsoft.Win32;
using static DiskMap.Tray.Native;

namespace DiskMap.Tray;

/// <summary>
/// The tray helper: a hidden window, a notification-area icon and a timer that checks free space.
/// When a drive drops below its limit it shows a Windows notification; clicking it opens diskmap.
/// </summary>
internal static unsafe class Program
{
    private const string WindowClass = "diskmap.tray";
    private const uint CallbackMessage = WM_APP + 1;
    private const uint MsgTestAlert = WM_APP + 2;
    private const uint MsgReloadSettings = WM_APP + 3;
    private const nuint CheckTimer = 1;

    private const int CmdOpen = 1, CmdCheck = 2, CmdSnooze = 3, CmdSettings = 4, CmdExit = 5;

    private static nint s_hwnd;
    private static uint s_taskbarCreated;
    private static nint s_icon;
    private static nint s_appIcon;
    private static string s_tip = "diskmap";

    [STAThread]
    private static int Main()
    {
        using var mutex = new Mutex(true, @"Local\diskmap.tray", out bool first);
        if (!first) return 0;

        var hInstance = GetModuleHandle(0);
        fixed (char* className = WindowClass)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = hInstance,
                lpszClassName = className,
            };
            RegisterClassEx(&wc);
        }
        s_hwnd = CreateWindowEx(0, WindowClass, "diskmap tray", WS_POPUP, 0, 0, 0, 0, 0, 0, hInstance, 0);
        s_taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        nint large = 0;
        if (Environment.ProcessPath is { } exe) ExtractIconEx(exe, 0, &large, null, 1);
        s_appIcon = large;

        AddIcon();
        Check(test: false);
        StartTimer();

        MSG msg;
        while (GetMessage(&msg, 0, 0, 0) > 0)
        {
            TranslateMessage(&msg);
            DispatchMessage(&msg);
        }
        return 0;
    }

    private static void StartTimer()
    {
        var settings = AppSettings.Load();
        KillTimer(s_hwnd, CheckTimer);
        SetTimer(s_hwnd, CheckTimer, (uint)Math.Clamp(settings.CheckIntervalMinutes, 1, 24 * 60) * 60_000, 0);
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            switch (msg)
            {
                case CallbackMessage:
                    switch ((uint)(lParam & 0xFFFF))
                    {
                        case WM_LBUTTONUP:
                        case NIN_SELECT:
                        case NIN_KEYSELECT:
                            OpenApp("");
                            break;
                        case NIN_BALLOONUSERCLICK:
                            OpenApp("--growth");
                            break;
                        case WM_CONTEXTMENU:
                            ShowMenu();
                            break;
                    }
                    return 0;
                case WM_TIMER:
                    Check(test: false);
                    return 0;
                case MsgTestAlert:
                    Check(test: true);
                    return 0;
                case MsgReloadSettings:
                    StartTimer();
                    Check(test: false);
                    return 0;
                case WM_CLOSE:
                    DestroyWindow(hwnd);
                    return 0;
                case WM_DESTROY:
                    RemoveIcon();
                    PostQuitMessage(0);
                    return 0;
            }
            if (msg == s_taskbarCreated && msg != 0)
            {
                AddIcon(); // Explorer restarted
                return 0;
            }
        }
        catch (Exception)
        {
            // Never let an exception escape into the window procedure.
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    // ---- Notification icon ------------------------------------------------------------------

    private static NOTIFYICONDATAW NewData()
    {
        return new NOTIFYICONDATAW { cbSize = (uint)sizeof(NOTIFYICONDATAW), hWnd = s_hwnd, uID = 1 };
    }

    private static void AddIcon()
    {
        var d = NewData();
        d.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
        d.uCallbackMessage = CallbackMessage;
        d.hIcon = s_icon != 0 ? s_icon : s_appIcon;
        Copy(d.szTip, 128, s_tip);
        Shell_NotifyIcon(NIM_ADD, &d);
        d.uVersion = 4;
        Shell_NotifyIcon(NIM_SETVERSION, &d);
    }

    private static void RemoveIcon()
    {
        var d = NewData();
        Shell_NotifyIcon(NIM_DELETE, &d);
    }

    private static void UpdateIcon(double usedFraction, bool warn, string tip)
    {
        var old = s_icon;
        s_icon = RingIcon.Create(usedFraction, warn);
        s_tip = tip;
        var d = NewData();
        d.uFlags = NIF_ICON | NIF_TIP | NIF_SHOWTIP;
        d.hIcon = s_icon;
        Copy(d.szTip, 128, tip);
        Shell_NotifyIcon(NIM_MODIFY, &d);
        if (old != 0) DestroyIcon(old);
    }

    private static void ShowNotification(string title, string text)
    {
        var d = NewData();
        d.uFlags = NIF_INFO;
        Copy(d.szInfoTitle, 64, title);
        Copy(d.szInfo, 256, text);
        d.dwInfoFlags = s_appIcon != 0 ? NIIF_USER | NIIF_LARGE_ICON : NIIF_WARNING;
        d.hBalloonIcon = s_appIcon;
        Shell_NotifyIcon(NIM_MODIFY, &d);
    }

    // ---- Free-space check -------------------------------------------------------------------

    private static void Check(bool test)
    {
        var settings = AppSettings.Load();
        var state = TrayState.Load();
        var log = FreeSpaceLog.Load();
        var now = DateTime.Now;
        bool logChanged = false;
        var tip = new List<string>();
        (double Fraction, bool Warn, double Pressure) worst = (0, false, -1);
        string systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        bool alerted = false;

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
            string root = drive.RootDirectory.FullName;
            if (!Volume.TryGetFreeSpace(root, out long free, out long total) || total == 0) continue;
            var alert = settings.AlertFor(root, total);
            string key = AppPaths.DriveKey(root);
            if (!state.Drives.TryGetValue(key, out var ds)) state.Drives[key] = ds = new DriveAlertState();
            long limit = alert.ThresholdBytes(total);
            string letter = root.TrimEnd('\\');

            if (AlertPolicy.ShouldAlert(alert, ds, free, total, settings.Remind, now, state.SnoozedUntil) && !alerted)
            {
                ShowNotification($"{letter} is getting full", AlertText(free, alert, log, root, now));
                AlertPolicy.MarkAlerted(ds, free, now);
                alerted = true;
            }
            else if (test && string.Equals(root, systemRoot, StringComparison.OrdinalIgnoreCase))
            {
                ShowNotification($"{letter}: test alert", AlertText(free, alert, log, root, now));
            }
            logChanged |= log.Record(root, free, now, settings.HistoryDays);

            tip.Add($"{letter} {Format.Bytes(free)} free");
            double pressure = alert.Enabled && limit > 0 ? (double)limit / Math.Max(1, free) : (double)(total - free) / total;
            if (pressure > worst.Pressure) worst = ((double)(total - free) / total, alert.Enabled && free < limit, pressure);
        }

        try
        {
            state.Save();
            if (logChanged) log.Save();
        }
        catch (IOException) { }

        string snoozed = state.SnoozedUntil is { } until && until > now ? $"\nAlerts snoozed until {until:t}" : "";
        UpdateIcon(worst.Fraction, worst.Warn, "diskmap\n" + string.Join("\n", tip) + snoozed);
    }

    private static string AlertText(long free, DriveAlert alert, FreeSpaceLog log, string root, DateTime now)
    {
        var text = $"{Format.Bytes(free)} free. Your alert is set at {alert.Describe()}.";
        if (log.FreeAround(root, now.AddDays(-1)) is { } yesterday && yesterday - free > 512L << 20)
        {
            text += $"\nDown {Format.Bytes(yesterday - free)} in the last 24 hours.";
        }
        return text + "\nClick to see what grew.";
    }

    // ---- Menu and launching -----------------------------------------------------------------

    private static void ShowMenu()
    {
        var state = TrayState.Load();
        bool snoozed = state.SnoozedUntil is { } until && until > DateTime.Now;
        var menu = CreatePopupMenu();
        foreach (var line in s_tip.Split('\n').Skip(1))
        {
            AppendMenu(menu, MF_STRING | MF_GRAYED, 0, line);
        }
        AppendMenu(menu, MF_SEPARATOR, 0, null);
        AppendMenu(menu, MF_STRING | MF_DEFAULT, CmdOpen, "Open diskmap");
        AppendMenu(menu, MF_STRING, CmdCheck, "Check now");
        AppendMenu(menu, MF_STRING, CmdSnooze, snoozed ? "Resume alerts" : "Snooze alerts for a day");
        AppendMenu(menu, MF_STRING, CmdSettings, "Settings…");
        AppendMenu(menu, MF_SEPARATOR, 0, null);
        AppendMenu(menu, MF_STRING, CmdExit, "Exit");

        POINT p;
        GetCursorPos(&p);
        SetForegroundWindow(s_hwnd);
        int cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN, p.X, p.Y, s_hwnd, 0);
        PostMessage(s_hwnd, WM_NULL, 0, 0);
        DestroyMenu(menu);

        switch (cmd)
        {
            case CmdOpen:
                OpenApp("");
                break;
            case CmdCheck:
                Check(test: false);
                break;
            case CmdSnooze:
                state.SnoozedUntil = snoozed ? null : DateTime.Now.AddDays(1);
                state.Save();
                Check(test: false);
                break;
            case CmdSettings:
                OpenApp("--settings");
                break;
            case CmdExit:
                DestroyWindow(s_hwnd);
                break;
        }
    }

    private static void OpenApp(string args)
    {
        var exe = FindApp();
        if (exe == null) return;
        try
        {
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static string? FindApp()
    {
        var sibling = Path.Combine(AppContext.BaseDirectory, "DiskMap.exe");
        if (File.Exists(sibling)) return sibling;
        if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\diskmap", "AppPath", null) is string path && File.Exists(path)) return path;
        return null;
    }
}

/// <summary>Draws the tray icon: a ring that fills as the drive fills, yellow when below the limit.</summary>
internal static unsafe class RingIcon
{
    public static nint Create(double used, bool warn)
    {
        uint dpi = GetDpiForSystem();
        int size = Math.Max(16, GetSystemMetricsForDpi(49 /* SM_CXSMICON */, dpi));
        bool lightTaskbar = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "SystemUsesLightTheme", 0) is int v && v == 1;
        uint fg = lightTaskbar ? 0x1A1A1Au : 0xFFFFFFu;
        uint fill = warn ? (lightTaskbar ? 0x9D5D00u : 0xFCE100u) : fg;

        var bmi = new BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = size, biHeight = -size, biPlanes = 1, biBitCount = 32,
        };
        void* bits;
        nint color = CreateDIBSection(0, &bmi, 0, &bits, 0, 0);
        nint mask = CreateBitmap(size, size, 1, 1, null);
        var px = new Span<uint>(bits, size * size);

        double c = size / 2.0, outer = size * 0.47, inner = size * 0.29;
        double sweep = Math.Clamp(used, 0, 1) * 2 * Math.PI;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double dx = x + 0.5 - c, dy = y + 0.5 - c;
                double d = Math.Sqrt(dx * dx + dy * dy);
                double coverage = Math.Clamp(outer - d + 0.5, 0, 1) * Math.Clamp(d - inner + 0.5, 0, 1);
                if (coverage <= 0)
                {
                    px[y * size + x] = 0;
                    continue;
                }
                double angle = Math.Atan2(dx, -dy);
                if (angle < 0) angle += 2 * Math.PI;
                bool filled = angle <= sweep;
                uint rgb = filled ? fill : fg;
                double alpha = coverage * (filled ? 1 : 0.32);
                px[y * size + x] = ((uint)(alpha * 255) << 24) | rgb;
            }
        }

        var info = new ICONINFO { fIcon = 1, hbmMask = mask, hbmColor = color };
        nint icon = CreateIconIndirect(&info);
        DeleteObject(color);
        DeleteObject(mask);
        return icon;
    }
}
