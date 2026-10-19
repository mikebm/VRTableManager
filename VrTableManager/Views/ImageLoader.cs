using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using VrTableManager.Services;

namespace VrTableManager.Views;

/// <summary>
/// Attached property <c>ImageLoader.Url</c> that fetches an image asynchronously with a disk cache,
/// so the catalog list can show table artwork without blocking.
/// </summary>
public sealed class ImageLoader : AvaloniaObject
{
    public static readonly AttachedProperty<string?> UrlProperty =
        AvaloniaProperty.RegisterAttached<ImageLoader, Image, string?>("Url");

    public static string? GetUrl(Image img) => img.GetValue(UrlProperty);
    public static void SetUrl(Image img, string? value) => img.SetValue(UrlProperty, value);

    static readonly ConcurrentDictionary<string, Task<Bitmap?>> Memory = new();
    static readonly SemaphoreSlim Throttle = new(6);
    static string CacheDir => Directory.CreateDirectory(Path.Combine(AppSettings.DataDir, "img-cache")).FullName;

    static ImageLoader()
    {
        UrlProperty.Changed.AddClassHandler<Image>(async (img, e) =>
        {
            img.Source = null;
            if (e.NewValue is not string url || url == "") return;
            var bmp = await Memory.GetOrAdd(url, LoadAsync);
            if (GetUrl(img) == url) img.Source = bmp; // still the same item after recycling?
        });
    }

    static async Task<Bitmap?> LoadAsync(string url)
    {
        try
        {
            var file = Path.Combine(CacheDir, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))) + ".img");
            if (!File.Exists(file))
            {
                await Throttle.WaitAsync();
                try { await File.WriteAllBytesAsync(file, await Http.Client.GetByteArrayAsync(url)); }
                finally { Throttle.Release(); }
            }
            return await Task.Run(() =>
            {
                using var s = File.OpenRead(file);
                return Bitmap.DecodeToWidth(s, 520);
            });
        }
        catch
        {
            return null;
        }
    }
}
