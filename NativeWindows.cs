using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexPeek
{
    /// <summary>只读取操作系统窗口元数据，并移动本工具自己的悬浮窗。</summary>
    public static class NativeWindows
    {
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const uint GwOwner = 4;
        private const int GwlExStyle = -20;
        private const int WsExToolWindow = 0x00000080;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpNoOwnerZOrder = 0x0200;
        private const int DwmwaCloaked = 14;
        private const int WmNcLeftButtonDown = 0x00A1;
        private const int HtCaption = 2;
        private static readonly IntPtr HwndTopMost = new IntPtr(-1);
        private static readonly IntPtr HwndNotTopMost = new IntPtr(-2);
        private static IntPtr lastCodexWindow;

        public static void EnableDpiAwareness()
        {
            // 清单通常已设置 DPI 感知；调用失败时继续兼容旧 Windows。
            try
            {
                if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return;
                if (Marshal.GetLastWin32Error() == 5) return;
            }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }
            try
            {
                int result = SetProcessDpiAwareness(2);
                if (result == 0 || result == unchecked((int)0x80070005)) return;
            }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }
            try { SetProcessDPIAware(); }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }
        }

        public static IntPtr FindCodexWindow()
        {
            List<IntPtr> candidates = FindCodexWindows(false);
            IntPtr foreground = GetForegroundWindow();
            if (candidates.Contains(foreground))
            {
                lastCodexWindow = foreground;
                return foreground;
            }
            // 用户查看其他应用或悬浮窗时，不在多个 Codex 窗口间来回跳转。
            if (candidates.Contains(lastCodexWindow)) return lastCodexWindow;
            lastCodexWindow = candidates.Count > 0 ? candidates[0] : IntPtr.Zero;
            return lastCodexWindow;
        }

        public static bool IsCodexRunning()
        {
            // 最小化的桌面端仍在运行，但独立 codex CLI 进程不计入。
            return FindCodexWindows(true).Count > 0;
        }

        public static bool IsUsableWindow(IntPtr handle)
        {
            return handle != IntPtr.Zero && IsWindow(handle) &&
                IsWindowVisible(handle) && !IsIconic(handle) && !IsCloaked(handle);
        }

        public static bool IsForegroundWindow(IntPtr handle)
        {
            return handle != IntPtr.Zero && GetForegroundWindow() == handle;
        }

        public static bool TryGetWindowBounds(IntPtr handle, out Rectangle bounds)
        {
            bounds = Rectangle.Empty;
            NativeRect rect;
            if (!IsUsableWindow(handle) || !GetWindowRect(handle, out rect)) return false;
            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0) return false;
            // 允许负坐标，以兼容主屏左侧或上方的显示器。
            bounds = new Rectangle(rect.Left, rect.Top, width, height);
            return true;
        }

        public static void PlaceOverlay(IntPtr overlay, Rectangle bounds, bool topMost)
        {
            if (overlay == IntPtr.Zero || !IsWindow(overlay) ||
                bounds.Width <= 0 || bounds.Height <= 0) return;
            SetWindowPos(overlay, topMost ? HwndTopMost : HwndNotTopMost,
                bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                SwpNoActivate | SwpNoOwnerZOrder);
        }

        public static void BeginDrag(IntPtr overlay)
        {
            if (overlay == IntPtr.Zero || !IsWindow(overlay)) return;
            ReleaseCapture();
            SendMessage(overlay, WmNcLeftButtonDown, new IntPtr(HtCaption), IntPtr.Zero);
        }

        public static int GetDpi(IntPtr handle)
        {
            try
            {
                uint dpi = GetDpiForWindow(handle);
                if (dpi > 0) return (int)dpi;
            }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }
            try
            {
                uint horizontal, vertical;
                IntPtr monitor = MonitorFromWindow(handle, 2);
                if (monitor != IntPtr.Zero &&
                    GetDpiForMonitor(monitor, 0, out horizontal, out vertical) == 0 &&
                    horizontal > 0) return (int)horizontal;
            }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }
            return 96;
        }

        private static List<IntPtr> FindCodexWindows(bool includeMinimized)
        {
            List<IntPtr> matches = new List<IntPtr>();
            Dictionary<uint, bool> processMatches = new Dictionary<uint, bool>();
            EnumWindows(delegate(IntPtr window, IntPtr unused)
            {
                if (!IsWindowVisible(window) || GetWindow(window, GwOwner) != IntPtr.Zero ||
                    (GetWindowLong(window, GwlExStyle) & WsExToolWindow) != 0 ||
                    IsCloaked(window) || (!includeMinimized && IsIconic(window))) return true;

                // Electron 主窗口使用此类名。控制台程序即使命名为 codex.exe 也不会匹配。
                StringBuilder className = new StringBuilder(128);
                if (GetClassName(window, className, className.Capacity) == 0 ||
                    !className.ToString().StartsWith("Chrome_WidgetWin_", StringComparison.Ordinal))
                    return true;

                uint processId;
                GetWindowThreadProcessId(window, out processId);
                if (processId == 0) return true;
                bool processMatch;
                if (!processMatches.TryGetValue(processId, out processMatch))
                {
                    processMatch = IsCodexDesktopProcess(processId);
                    processMatches.Add(processId, processMatch);
                }
                if (processMatch) matches.Add(window);
                return true;
            }, IntPtr.Zero);
            return matches;
        }

        private static bool IsCodexDesktopProcess(uint processId)
        {
            IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (process == IntPtr.Zero) return false;
            try
            {
                StringBuilder path = new StringBuilder(32768);
                uint capacity = (uint)path.Capacity;
                if (!QueryFullProcessImageName(process, 0, path, ref capacity)) return false;
                string fileName = Path.GetFileName(path.ToString());
                return String.Equals(fileName, "Codex.exe", StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(fileName, "ChatGPT.exe", StringComparison.OrdinalIgnoreCase);
            }
            finally { CloseHandle(process); }
        }

        private static bool IsCloaked(IntPtr window)
        {
            try
            {
                int cloaked;
                return DwmGetWindowAttribute(window, DwmwaCloaked, out cloaked, sizeof(int)) == 0 && cloaked != 0;
            }
            catch (EntryPointNotFoundException) { return false; }
            catch (DllNotFoundException) { return false; }
        }

        private delegate bool EnumWindowsCallback(IntPtr handle, IntPtr parameter);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr handle);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr handle);
        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr handle);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr handle, uint command);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong(IntPtr handle, int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr handle, StringBuilder className, int maximumCount);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder fileName, ref uint size);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out int value, int size);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr awarenessContext);
        [DllImport("shcore.dll")]
        private static extern int SetProcessDpiAwareness(int awareness);
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr handle);
        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);
        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
    }
}
