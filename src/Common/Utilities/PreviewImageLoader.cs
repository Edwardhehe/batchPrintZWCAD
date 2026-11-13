using System;
using System.IO;
using System.Windows.Media.Imaging;

namespace ZwcadBatchPlot;

/// <summary>WPF 预览图加载：优先 BitmapImage，失败时用 GDI+ 解码再转 BitmapSource。</summary>
internal static class PreviewImageLoader
{
    public static BitmapSource? Load(string? path, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            error = "图片不存在。";
            return null;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(Path.GetFullPath(path));
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception primary)
        {
            try
            {
                using var bitmap = new System.Drawing.Bitmap(path);
                var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                    bitmap.GetHbitmap(),
                    IntPtr.Zero,
                    System.Windows.Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            catch (Exception fallback)
            {
                error = primary.Message + " / " + fallback.Message;
                return null;
            }
        }
    }
}
