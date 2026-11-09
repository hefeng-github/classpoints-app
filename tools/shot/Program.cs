using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace ShotTool;

/// <summary>
/// 验收用抓图工具：按进程名抓主窗口，或抓整屏。
/// 用法：
///   shot &lt;输出路径&gt; [进程名]   抓窗口
///   shot &lt;输出路径&gt;              抓整屏
///   shot --tree [进程名]          打印窗口树（含子窗口，排查"白屏"用）
/// </summary>
internal static class Program
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr param);

    private delegate bool EnumProc(IntPtr hWnd, IntPtr param);

    private const int SwRestore = 9;

    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("用法: shot <输出路径> [进程名] | shot --tree [进程名]");
            return 2;
        }

        if (args[0] == "--crop")
        {
            // shot --crop <源图> <x> <y> <w> <h> <倍数> <输出>
            using var source = new Bitmap(args[1]);
            var rect = new Rectangle(int.Parse(args[2]), int.Parse(args[3]),
                int.Parse(args[4]), int.Parse(args[5]));
            var scale = int.Parse(args[6]);
            using var crop = new Bitmap(rect.Width * scale, rect.Height * scale);
            using (var g = Graphics.FromImage(crop))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                g.DrawImage(source, new Rectangle(0, 0, crop.Width, crop.Height), rect,
                    GraphicsUnit.Pixel);
            }
            crop.Save(args[7], ImageFormat.Png);
            Console.WriteLine($"{args[7]}  {crop.Width}x{crop.Height}");
            return 0;
        }

        if (args[0] == "--tree")
        {
            var target = args.Length > 1 ? args[1] : "ClassPoints";
            var p = Process.GetProcessesByName(target).FirstOrDefault();
            if (p is null)
            {
                Console.WriteLine($"没找到进程 {target}");
                return 1;
            }

            foreach (var hwnd in TopLevelOf(p.Id))
            {
                Dump(hwnd, 0);
            }
            return 0;
        }

        var output = args[0];
        var dir = Path.GetDirectoryName(Path.GetFullPath(output));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        Rectangle bounds;
        if (args.Length > 1)
        {
            var proc = Process.GetProcessesByName(args[1]).FirstOrDefault(x => x.MainWindowHandle != IntPtr.Zero);
            if (proc is null)
            {
                Console.WriteLine($"没找到进程 {args[1]} 的窗口");
                return 1;
            }

            var handle = proc.MainWindowHandle;
            ShowWindow(handle, SwRestore);
            SetForegroundWindow(handle);
            Thread.Sleep(600);

            if (!GetWindowRect(handle, out var rect))
            {
                Console.WriteLine("取窗口位置失败");
                return 1;
            }
            bounds = new Rectangle(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        }
        else
        {
            bounds = SystemInformation.VirtualScreen;
        }

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            Console.WriteLine("区域为空");
            return 1;
        }

        using var bmp = new Bitmap(bounds.Width, bounds.Height);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bmp.Size);

        bmp.Save(output, ImageFormat.Png);
        Console.WriteLine($"{output}  {bounds.Width}x{bounds.Height}");
        return 0;
    }

    /// <summary>按进程 id 找顶层窗口（MainWindowHandle 只给一个，这里要全部）。</summary>
    private static List<IntPtr> TopLevelOf(int pid)
    {
        var found = new List<IntPtr>();
        var current = IntPtr.Zero;
        while (true)
        {
            current = FindWindowEx(IntPtr.Zero, current, null, null);
            if (current == IntPtr.Zero) break;
            GetWindowThreadProcessId(current, out var owner);
            if (owner == pid) found.Add(current);
        }
        return found;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);

    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int pid);

    private static void Dump(IntPtr hwnd, int depth)
    {
        var cls = new StringBuilder(256);
        GetClassName(hwnd, cls, cls.Capacity);
        var title = new StringBuilder(256);
        GetWindowText(hwnd, title, title.Capacity);
        GetWindowRect(hwnd, out var r);

        Console.WriteLine($"{new string(' ', depth * 2)}[{cls}] \"{title}\" " +
                          $"{(r.Right - r.Left)}x{(r.Bottom - r.Top)} at ({r.Left},{r.Top}) vis={IsWindowVisible(hwnd)}");

        EnumChildWindows(hwnd, (child, _) =>
        {
            Dump(child, depth + 1);
            return true;
        }, IntPtr.Zero);
    }
}
