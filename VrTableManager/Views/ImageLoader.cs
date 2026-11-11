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
    static string CacheDir =>
        Directory.CreateDirectory(Path.Combine(AppSettings.DataDir, "img-cache")).FullName;

    static ImageLoader()
    {
        UrlProperty.Changed.AddClassHandler<Image>(
            async (img, e) =>
            {
                img.Source = null;
                if (e.NewValue is not string url || url == "")
                {
                    return;
                }

                var load = Memory.GetOrAdd(url, LoadAsync);
                var bmp = await load;
                if (bmp == null)
                {
                    // Don't remember a failure (a network blip, a timeout): the next time the card is shown it tries again.
                    Memory.TryRemove(new KeyValuePair<string, Task<Bitmap?>>(url, load));
                }
                if (GetUrl(img) == url)
                {
                    img.Source = bmp; // still the same item after recycling?
                }
            }
        );
    }

    // The shared client allows 30 minutes for big downloads; a card picture that hasn't come in a minute isn't coming,
    // and a stuck one would hold one of the six download slots the whole time.
    static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(60);

    static async Task<Bitmap?> LoadAsync(string url)
    {
        // A picture you chose (UserArtwork): read straight from disk, never cached.
        var own = UserArtwork.PathOf(url);
        var file =
            own
            ?? Path.Combine(
                CacheDir,
                Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))) + ".img"
            );
        try
        {
            if (own == null && !File.Exists(file))
            {
                await Throttle.WaitAsync();
                try
                {
                    using var timeout = new CancellationTokenSource(FetchTimeout);
                    var bytes = await Http.Client.GetByteArrayAsync(url, timeout.Token);
                    // Written whole, then moved into place: a download cut short never sits in the cache as the picture.
                    var tmp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    await File.WriteAllBytesAsync(tmp, bytes);
                    File.Move(tmp, file, overwrite: true);
                }
                finally
                {
                    Throttle.Release();
                }
            }
            var bmp = await Task.Run(() =>
            {
                using var s = File.OpenRead(file);
                return Bitmap.DecodeToWidth(s, 520);
            });
            return bmp ?? Discard();
        }
        catch
        {
            return Discard();
        }

        // A cached file that won't open as a picture is thrown away, so the next try downloads it again.
        Bitmap? Discard()
        {
            if (own == null)
            {
                try
                {
                    File.Delete(file);
                }
                catch { }
            }
            return null;
        }
    }
}
