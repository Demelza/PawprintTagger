using Gdk;
using PawprintTagger.Core;

namespace PawprintTagger.Gtk;

internal sealed class ImageLoader
{
    // GdkPixbuf decoding is independent of the GTK widget thread. Bound its concurrent memory use.
    private readonly SemaphoreSlim _workers = new(2, 2);
    internal static ImageDimensions Inspect(string path)
    {
        try
        {
            var format = Pixbuf.GetFileInfo(path, out var width, out var height);
            if (format is null || width <= 0 || height <= 0) throw new InvalidDataException("Unsupported or damaged image.");
            using var validation = new Pixbuf(path, 32, 32, true);
            if (validation.GetOption("orientation") is "5" or "6" or "7" or "8") (width, height) = (height, width);
            return new(width, height);
        }
        catch (GLib.GException ex) { throw new InvalidDataException("Unsupported or damaged image: " + ex.Message, ex); }
    }

    internal async Task<Pixbuf> LoadAsync(string path, int width, int height, CancellationToken token,
        string? cachePath = null)
    {
        await _workers.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                if (cachePath is not null && File.Exists(cachePath))
                {
                    try { return new Pixbuf(cachePath); }
                    catch (GLib.GException) { /* Rebuild a damaged thumbnail from its owned image. */ }
                }
                Pixbuf result;
                using (var source = new Pixbuf(path, Math.Max(1, width), Math.Max(1, height), true))
                using (var oriented = source.ApplyEmbeddedOrientation())
                    // GtkSharp can return the same managed wrapper when no rotation is needed.
                    // An independent copy survives disposal of both decoding wrappers.
                    result = oriented.Copy();
                if (token.IsCancellationRequested) { result.Dispose(); token.ThrowIfCancellationRequested(); }
                if (cachePath is not null)
                {
                    // Uniform transparent tiles keep gallery rows stable while thumbnails arrive.
                    using (var oriented = result)
                    {
                        var tile = new Pixbuf(Colorspace.Rgb, true, 8, width, height);
                        tile.Fill(0x00000000);
                        using var fit = result.ScaleSimple(
                            Math.Max(1, (int)Math.Round(oriented.Width * Math.Min((double)width / oriented.Width, (double)height / oriented.Height))),
                            Math.Max(1, (int)Math.Round(oriented.Height * Math.Min((double)width / oriented.Width, (double)height / oriented.Height))),
                            InterpType.Bilinear);
                        fit.CopyArea(0, 0, fit.Width, fit.Height, tile, (width - fit.Width) / 2, (height - fit.Height) / 2);
                        result = tile;
                    }
                    var temporary = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        result.Save(temporary, "png");
                        File.Move(temporary, cachePath, overwrite: true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or GLib.GException)
                    { /* A read-only thumbnail cache must not prevent viewing. */ }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
                return result;
            }, token).ConfigureAwait(false);
        }
        finally { _workers.Release(); }
    }
}
