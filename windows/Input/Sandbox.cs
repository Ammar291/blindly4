using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Forms;

namespace Blindly;

// BLINDLY4_SANDBOX=1 keeps an automation off the user's desktop. The Chrome that `open` launches is muted,
// told not to throttle a window nobody can see, and moved beyond the edge of the virtual screen. Nothing
// reaches it as real input: `hover` and `key` are window messages posted to that window, and `press`,
// `set-value` and `focus` are posted clicks and keystrokes too, because Chrome brings its window to the
// foreground whenever a UI Automation action (Invoke, SetValue, SetFocus) touches web content, which
// would take the user's keyboard. `activate` never takes the foreground; commands that need real input
// (click, type, paste, scroll, set-selected-text) are refused. UI Automation reads work as before.
internal static class Sandbox
{
    internal static readonly bool Active = Environment.GetEnvironmentVariable("BLINDLY4_SANDBOX") == "1";
    internal static readonly string[] ChromeFlags = [
        // CalculateNativeWinOcclusion is what suspends the renderer of a covered or off-screen window on
        // Windows, which stops the video advancing; the rest keep a background window's timers and
        // rendering live. Muted because the window is never meant to be heard while it plays unseen.
        "--disable-features=CalculateNativeWinOcclusion", "--disable-backgrounding-occluded-windows",
        "--disable-renderer-backgrounding", "--disable-background-timer-throttling", "--mute-audio"
    ];
    [StructLayout(LayoutKind.Sequential)] struct Rect { internal int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint handle, out Rect rect);
    [DllImport("user32.dll")] static extern bool SetWindowPos(nint handle, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] static extern bool PostMessage(nint handle, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] static extern bool ScreenToClient(nint handle, ref Native.Point point);
    const uint MouseMove = 0x0200, LButtonDown = 0x0201, LButtonUp = 0x0202, KeyDown = 0x0100, KeyUp = 0x0101, CharMessage = 0x0102;
    const uint NoSizeNoZOrderNoActivate = 0x0001 | 0x0004 | 0x0010;
    static readonly nint KeyUpFlags = (nint)unchecked((int)0xC0000001u);

    internal static CliError Refused(string command) =>
        CliError.Access($"Input blocked: {command} needs real input, which sandbox mode (BLINDLY4_SANDBOX) never sends");

    // Every window of the process goes beyond the left edge of the virtual screen, where nothing shows.
    internal static void Hide(int pid)
    {
        var screen = SystemInformation.VirtualScreen;
        foreach (var window in Native.Windows().Where(w => w.Pid == pid))
            if (GetWindowRect(window.Handle, out var r))
                SetWindowPos(window.Handle, 0, screen.Left - (r.Right - r.Left) - 64, screen.Top, 0, 0, NoSizeNoZOrderNoActivate);
    }
    static Native.Window Window(int pid, int x, int y)
    {
        var windows = Native.Windows().Where(w => w.Pid == pid).ToList();
        if (windows.Count == 0) throw CliError.Access($"No visible window for PID {pid}");
        return windows.FirstOrDefault(w => GetWindowRect(w.Handle, out var r) && x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom)
            ?? throw CliError.Access("Input blocked: the screen point is outside the application's windows");
    }
    static nint Client(nint handle, int x, int y)
    {
        var point = new Native.Point { X = x, Y = y };
        ScreenToClient(handle, ref point);
        return (nint)(int)(((point.Y & 0xFFFF) << 16) | (point.X & 0xFFFF));
    }
    internal static void Hover(int pid, int x, int y) => Move(Window(pid, x, y).Handle, x, y);
    // Two positions so the target always observes movement, as with the real pointer.
    static void Move(nint handle, int x, int y)
    {
        foreach (int dx in new[] { 1, 0 })
            if (!PostMessage(handle, MouseMove, 0, Client(handle, x + dx, y))) throw CliError.Access("Cannot post the pointer move");
    }
    internal static void Key(int pid, ushort[] keys)
    {
        var window = Native.Windows().FirstOrDefault(w => w.Pid == pid) ?? throw CliError.Access($"No visible window for PID {pid}");
        foreach (var key in keys) PostMessage(window.Handle, KeyDown, key, 1);
        foreach (var key in keys.Reverse()) PostMessage(window.Handle, KeyUp, key, KeyUpFlags);
    }
    static void Tap(nint handle, ushort key) { PostMessage(handle, KeyDown, key, 1); PostMessage(handle, KeyUp, key, KeyUpFlags); }

    static (int X, int Y) Centre(Node node)
    {
        var bounds = node.Element!.Current.BoundingRectangle;
        if (bounds.IsEmpty || !double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y))
            throw CliError.Access("Target has no on-screen bounds, so sandbox mode cannot click it");
        return ((int)Math.Round(bounds.X + bounds.Width / 2), (int)Math.Round(bounds.Y + bounds.Height / 2));
    }
    static bool Inside(int pid, int x, int y) { try { Window(pid, x, y); return true; } catch (CliError) { return false; } }
    // A click posted to the window at the element's centre. An element outside the window is scrolled into
    // view first, and the pointer arrives before the click so that controls shown on hover (a video's
    // control bar) are in place; the centre is read again after that.
    internal static void Click(Node node)
    {
        if (node.Element == null) throw new CliError("Choose a control path");
        var (x, y) = Centre(node);
        if (!Inside(node.Pid, x, y) && node.Pattern<ScrollItemPattern>(ScrollItemPattern.Pattern) is { } scroll)
        {
            scroll.ScrollIntoView();
            Thread.Sleep(300);
            (x, y) = Centre(node);
        }
        var handle = Window(node.Pid, x, y).Handle;
        Move(handle, x, y);
        Thread.Sleep(120);
        (x, y) = Centre(node);
        var at = Client(handle, x, y);
        if (!PostMessage(handle, MouseMove, 0, at) || !PostMessage(handle, LButtonDown, 1, at) || !PostMessage(handle, LButtonUp, 0, at))
            throw CliError.Access("Cannot post the click");
    }
    // Typing posted to the window: a click into the field, the old text deleted, one character message per
    // character. The result is read back through UI Automation and the whole thing retried, because a
    // keystroke can be lost while the page is busy.
    internal static void Type(Node node, string text)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            Click(node);
            Thread.Sleep(200);
            var (x, y) = Centre(node);
            var handle = Window(node.Pid, x, y).Handle;
            Tap(handle, 0x23); // End
            for (int i = (node.Value ?? "").Length + 2; i > 0; i--) Tap(handle, 0x08); // Backspace
            Thread.Sleep(300); // a character that follows the deletions at once is dropped
            foreach (char c in text) { PostMessage(handle, CharMessage, c, 1); Thread.Sleep(5); }
            for (int i = 0; i < 20; i++) { if (Guards.Exact(node.Value ?? "", text)) return; Thread.Sleep(100); }
        }
        throw CliError.Access($"The field reads \"{node.Value}\" after typing; set-value did not take");
    }
}
