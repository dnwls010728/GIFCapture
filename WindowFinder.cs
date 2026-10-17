namespace GifCapture;

sealed record WindowInfo(IntPtr Handle, string Title, Rectangle Bounds);

static class WindowFinder
{
    static readonly HashSet<string> IgnoredClasses = ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"];

    /// <summary>Visible top-level windows of other processes, front to back.</summary>
    public static List<WindowInfo> Enumerate()
    {
        uint ownPid = (uint)Environment.ProcessId;
        var result = new List<WindowInfo>();

        Native.EnumWindows((hWnd, _) =>
        {
            if (!Native.IsWindowVisible(hWnd) || Native.IsIconic(hWnd) || Native.IsCloaked(hWnd))
                return true;
            Native.GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == ownPid)
                return true;
            if (((long)Native.GetWindowLongPtr(hWnd, Native.GWL_EXSTYLE) & Native.WS_EX_TOOLWINDOW) != 0)
                return true;
            if (IgnoredClasses.Contains(Native.GetClassName(hWnd)))
                return true;

            var title = Native.GetWindowTitle(hWnd);
            if (title.Length == 0 || !Native.TryGetWindowBounds(hWnd, out var bounds))
                return true;
            if (bounds.Width < 40 || bounds.Height < 40)
                return true;

            result.Add(new WindowInfo(hWnd, title, bounds));
            return true;
        }, IntPtr.Zero);

        return result;
    }
}
