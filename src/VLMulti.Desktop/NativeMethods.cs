using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace VLMulti.Desktop;

internal static class NativeMethods
{
    private const uint FLASHW_ALL = 3;
    private const uint FLASHW_TIMERNOFG = 12; // nháy tới khi cửa sổ được đưa lên trước

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    // Nháy biểu tượng trên taskbar để báo có cảnh báo khi bạn đang dùng cửa sổ khác
    public static void FlashTaskbar(Window window)
    {
        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = new WindowInteropHelper(window).Handle,
            dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
        };
        FlashWindowEx(ref info);
    }
}
