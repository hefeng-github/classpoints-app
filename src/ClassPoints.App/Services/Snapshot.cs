using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace ClassPoints.App.Services;

/// <summary>
/// 验收用自截图：直接把 XAML 视觉树渲染成 PNG，不经过窗口呈现。
/// 远程会话 / 虚拟显示器上，桌面截图拿不到 WinUI 的合成结果（只会得到白窗口），
/// 所以验收图只能由程序自己渲染。也用来给每一页留一张"交付时该长什么样"的基准图。
/// </summary>
public static class Snapshot
{
    public static async Task<bool> SaveAsync(UIElement element, string path)
    {
        try
        {
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(element);

            var pixels = await bitmap.GetPixelsAsync();
            var reader = DataReader.FromBuffer(pixels);
            var raw = new byte[pixels.Length];
            reader.ReadBytes(raw);

            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth,
                (uint)bitmap.PixelHeight,
                96,
                96,
                raw);
            await encoder.FlushAsync();

            var png = new MemoryStream();
            using (var input = stream.GetInputStreamAt(0))
                await input.AsStreamForRead().CopyToAsync(png);

            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(path, png.ToArray());
            return true;
        }
        catch (Exception ex)
        {
            App.Services?.LogError("snapshot", ex);
            return false;
        }
    }
}
