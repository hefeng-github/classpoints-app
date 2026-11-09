using System.Management;
using Microsoft.UI.Xaml.Controls;

namespace ClassPoints.App.Services;

/// <summary>
/// 打印：一份 HTML，预览和打印是同一份（方案 10.6）。
/// 不引 PDF 库 —— WebView2 就是浏览器，Chrome 打出来什么样，这里就是什么样。
///
/// WinUI 版 WebView2 没有 ShowPrintUIAsync（那是 WPF/Win32 版的 API），
/// 所以打印机选择由我们自己做：先列本机打印机，再按名字静默打印。
/// 好处是页眉页脚能关掉，纸面干净。
/// </summary>
public static class PrintService
{
    public sealed record PrinterInfo(string Name, bool IsDefault)
    {
        public override string ToString() => IsDefault ? Name + "（默认）" : Name;
    }

    /// <summary>本机打印机列表，默认打印机排第一。</summary>
    public static List<PrinterInfo> ListPrinters()
    {
        var list = new List<PrinterInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, Default FROM Win32_Printer");
            foreach (var item in searcher.Get())
            {
                var name = item["Name"] as string;
                if (string.IsNullOrWhiteSpace(name)) continue;
                var isDefault = item["Default"] is bool b && b;
                list.Add(new PrinterInfo(name, isDefault));
            }
        }
        catch (Exception ex)
        {
            App.Services?.LogError("list-printers", ex);
        }

        return list
            .OrderByDescending(p => p.IsDefault)
            .ThenBy(p => p.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>
    /// 按指定打印机打印，且关掉页眉页脚。
    /// printerName 为空时用系统默认打印机。
    /// </summary>
    public static async Task<(bool Ok, string Message)> PrintAsync(WebView2 view, string? printerName)
    {
        try
        {
            var core = await EnsureAsync(view);
            if (core is null) return (false, "WebView2 运行时没有初始化成功");

            var settings = core.Environment.CreatePrintSettings();
            settings.ShouldPrintHeaderAndFooter = false;
            settings.ShouldPrintBackgrounds = true;
            settings.MarginTop = 0.35;
            settings.MarginBottom = 0.35;
            settings.MarginLeft = 0.35;
            settings.MarginRight = 0.35;

            if (!string.IsNullOrWhiteSpace(printerName)) settings.PrinterName = printerName;

            var status = await core.PrintAsync(settings);
            return status == Microsoft.Web.WebView2.Core.CoreWebView2PrintStatus.Succeeded
                ? (true, "已发送到打印机")
                : (false, status switch
                {
                    Microsoft.Web.WebView2.Core.CoreWebView2PrintStatus.PrinterUnavailable => "打印机不可用（没开机或没连上）",
                    Microsoft.Web.WebView2.Core.CoreWebView2PrintStatus.OtherError => "打印出错，换个打印机试试",
                    _ => "打印没有完成：" + status,
                });
        }
        catch (Exception ex)
        {
            App.Services?.LogError("print", ex);
            return (false, "打印失败：" + ex.Message);
        }
    }

    /// <summary>另存 HTML：教务要电子版、或者想拿去 WPS 里再调时用。</summary>
    public static async Task<string?> SaveHtmlAsync(string html, string suggestedName)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = suggestedName };
            picker.FileTypeChoices.Add("网页（可打印）", new List<string> { ".html" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker,
                WinRT.Interop.WindowNative.GetWindowHandle(App.Window!));

            var file = await picker.PickSaveFileAsync();
            if (file is null) return null;

            await File.WriteAllTextAsync(file.Path, html, System.Text.Encoding.UTF8);
            return file.Path;
        }
        catch (Exception ex)
        {
            App.Services?.LogError("print-save-html", ex);
            return null;
        }
    }

    private static async Task<Microsoft.Web.WebView2.Core.CoreWebView2?> EnsureAsync(WebView2 view)
    {
        if (view.CoreWebView2 is null) await view.EnsureCoreWebView2Async();

        if (view.CoreWebView2 is null)
        {
            App.Services?.Log("打印失败：WebView2 运行时没有初始化成功（检查是否装了 Edge WebView2 Runtime）");
            return null;
        }

        view.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        view.CoreWebView2.Settings.IsStatusBarEnabled = false;
        return view.CoreWebView2;
    }
}
