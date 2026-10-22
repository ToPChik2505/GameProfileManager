using System.Runtime.InteropServices;

namespace GameProfileManager;

/// <summary>Windows Shell notification icon for the WinUI HWND.</summary>
internal sealed class ShellTrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8000 + 42;
    private const uint NotifyAdd = 0;
    private const uint NotifyDelete = 2;
    private const uint NotifyMessage = 1;
    private const uint NotifyIcon = 2;
    private const uint NotifyTip = 4;
    private const uint LeftDoubleClick = 0x0203;
    private const uint RightButtonUp = 0x0205;
    private const uint GetMinMaxInfo = 0x0024;
    private const uint PopupReturnCommand = 0x0100;
    private readonly IntPtr _window;
    private readonly IntPtr _icon;
    private readonly SubclassProc _callback;
    private readonly Action _open;
    private readonly Action _verify;
    private readonly Action _apply;
    private readonly Action _exit;
    private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private bool _added;
    public bool IsAvailable => _added;

    public ShellTrayIcon(IntPtr window, string iconPath, Action open, Action verify, Action apply, Action exit)
    {
        _window = window;
        _open = open;
        _verify = verify;
        _apply = apply;
        _exit = exit;
        _icon = LoadImage(IntPtr.Zero, iconPath, 1, 0, 0, 0x10 | 0x40);
        _callback = OnWindowMessage;
        if (!SetWindowSubclass(window, _callback, (UIntPtr)1, UIntPtr.Zero))
            throw new InvalidOperationException("Не удалось подключить обработчик значка в трее.");
        AddIcon();
    }

    private void AddIcon()
    {
        var data = Data();
        data.Flags = NotifyMessage | NotifyIcon | NotifyTip;
        _added = Shell_NotifyIcon(NotifyAdd, ref data);
    }

    private NotificationData Data() => new()
    {
        Size = (uint)Marshal.SizeOf<NotificationData>(),
        Window = _window, Id = 1, Callback = CallbackMessage, Icon = _icon,
        Tip = "Game Profile Manager", Info = "", InfoTitle = ""
    };

    private IntPtr OnWindowMessage(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam,
        UIntPtr subclassId, UIntPtr reference)
    {
        if (message == GetMinMaxInfo)
        {
            IntPtr result = DefSubclassProc(window, message, wParam, lParam);
            var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            uint dpi = GetDpiForWindow(window);
            int minWidth = (int)Math.Ceiling(960d * dpi / 96d);
            int minHeight = (int)Math.Ceiling(560d * dpi / 96d);
            info.MinTrackSize.X = Math.Max(info.MinTrackSize.X, minWidth);
            info.MinTrackSize.Y = Math.Max(info.MinTrackSize.Y, minHeight);
            Marshal.StructureToPtr(info, lParam, false);
            return result;
        }
        if (message == _taskbarCreated) { AddIcon(); return IntPtr.Zero; }
        if (message == CallbackMessage)
        {
            uint mouseMessage = unchecked((uint)lParam.ToInt64()) & 0xffff;
            if (mouseMessage == LeftDoubleClick) _open();
            else if (mouseMessage == RightButtonUp) ShowMenu();
            return IntPtr.Zero;
        }
        return DefSubclassProc(window, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            AppendMenu(menu, 0, 1, "Открыть");
            AppendMenu(menu, 0, 2, "Проверить настройки");
            AppendMenu(menu, 0, 3, "Применить ко всем");
            AppendMenu(menu, 0, 4, "Выход");
            GetCursorPos(out Point position);
            SetForegroundWindow(_window);
            uint command = TrackPopupMenu(menu, PopupReturnCommand, position.X, position.Y,
                0, _window, IntPtr.Zero);
            switch (command)
            {
                case 1: _open(); break;
                case 2: _verify(); break;
                case 3: _apply(); break;
                case 4: _exit(); break;
            }
        }
        finally { DestroyMenu(menu); }
    }

    public void Dispose()
    {
        if (_added) { var data = Data(); Shell_NotifyIcon(NotifyDelete, ref data); _added = false; }
        RemoveWindowSubclass(_window, _callback, (UIntPtr)1);
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotificationData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public uint Flags;
        public uint Callback;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public IntPtr BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point Reserved;
        public Point MaxSize;
        public Point MaxPosition;
        public Point MinTrackSize;
        public Point MaxTrackSize;
    }
    private delegate IntPtr SubclassProc(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam,
        UIntPtr subclassId, UIntPtr reference);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref NotificationData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr window, SubclassProc callback, UIntPtr id, UIntPtr reference);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr window, SubclassProc callback, UIntPtr id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, uint id, string text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr window, IntPtr rectangle);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr window);
}
