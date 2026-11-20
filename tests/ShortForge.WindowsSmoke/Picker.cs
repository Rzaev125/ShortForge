using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace ShortForge.WindowsSmoke;

internal static class WindowDpi
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    internal static uint Measure(IntPtr handle, int? expected, string surface)
    {
        var actual = GetDpiForWindow(handle);
        Console.WriteLine($"{surface} DPI={actual} expected={expected?.ToString() ?? "any"}");
        if (actual == 0 || (expected.HasValue && actual != expected.Value))
            throw new InvalidOperationException($"{surface}: measured {actual} DPI, requested {expected} DPI. Select the requested Windows display scale before running this test.");
        return actual;
    }
}

internal static class Picker
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string title);
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr window);

    private static IntPtr WaitForDialog(IntPtr owner, string title = "Выберите локальное видео")
    {
        var watch = Stopwatch.StartNew();
        IntPtr dialog = IntPtr.Zero;
        while (watch.Elapsed < TimeSpan.FromSeconds(15))
        {
            var next = FindWindowEx(IntPtr.Zero, dialog, "#32770", title);
            if (next != IntPtr.Zero && GetWindow(next, 4 /* GW_OWNER */) == owner)
            {
                return next;
            }
            dialog = next;
            Thread.Sleep(100);
        }
        throw new TimeoutException("Expected native file picker owned by this test window.");
    }

    public static void Cancel(IntPtr owner, int? nativeDpi)
    {
        var dialog = WaitForDialog(owner);
        try { WindowDpi.Measure(dialog, nativeDpi, "OpenFileDialog (cancel)"); }
        finally { PostMessage(dialog, 0x10 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero); }
        if (!SpinWait.SpinUntil(() => !IsWindow(dialog), TimeSpan.FromSeconds(5)))
            throw new TimeoutException("Cancelled file picker did not close.");
    }

    public static void AcceptFolder(IntPtr owner, string folder)
    {
        var dialog = WaitForDialog(owner, "Папка для экспорта");
        try
        {
            var root = AutomationElement.FromHandle(dialog);
            var toolbar = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ToolBar);
            if (!SpinWait.SpinUntil(() => root.FindAll(TreeScope.Descendants, toolbar).Cast<AutomationElement>()
                    .Any(t => t.Current.Name.Contains(folder, StringComparison.OrdinalIgnoreCase)), TimeSpan.FromSeconds(6)))
                throw new InvalidDataException("Native folder picker did not open the isolated export directory.");
            var accept = root.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.AutomationIdProperty, "1")));
            ((InvokePattern)accept.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            if (!SpinWait.SpinUntil(() => !IsWindow(dialog), TimeSpan.FromSeconds(6)))
                throw new TimeoutException("Native folder picker stayed open.");
        }
        catch { PostMessage(dialog, 0x10, IntPtr.Zero, IntPtr.Zero); throw; }
    }

    public static void Choose(IntPtr owner, string path, int? nativeDpi)
    {
        var dialog = WaitForDialog(owner);
        try
        {
            WindowDpi.Measure(dialog, nativeDpi, "OpenFileDialog");
            var root = AutomationElement.FromHandle(dialog);
            AutomationElement? item = null;
            var directory = Path.GetFileName(Path.GetDirectoryName(path))!;
            var name = Path.GetFileName(path);
            var stem = Path.GetFileNameWithoutExtension(path);
            var itemCondition = new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                new OrCondition(ByName(name), ByName(stem)));
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(8) && item == null)
            {
                var toolbars = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ToolBar));
                if (toolbars.Cast<AutomationElement>().Any(t => t.Current.Name.Contains(directory, StringComparison.Ordinal)))
                    item = root.FindFirst(TreeScope.Descendants, itemCondition);
                if (item == null) Thread.Sleep(100);
            }
            if (item == null) throw new InvalidOperationException("Could not verify the unique fixture directory and requested file. Picker cancelled.");
            item.SetFocus();
            var selection = (SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern);
            selection.Select();
            if (!selection.Current.IsSelected) throw new InvalidOperationException("Fixture selection not confirmed.");
            // Invoke the verified list item itself. Invoking the default Open button
            // can accept the Explorer shell's previously focused item during refresh.
            ((InvokePattern)item.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            watch.Restart();
            while (IsWindow(dialog) && watch.Elapsed < TimeSpan.FromSeconds(5)) Thread.Sleep(100);
            if (IsWindow(dialog)) throw new InvalidOperationException("Open did not dismiss the native picker.");
        }
        catch
        {
            PostMessage(dialog, 0x10 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
            throw;
        }
    }

    private static Condition ByName(string name) => new PropertyCondition(AutomationElement.NameProperty, name);
}
