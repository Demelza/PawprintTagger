using Gtk;

namespace PawprintTagger.Gtk;

internal sealed class ImageCanvas : DrawingArea
{
    private Gdk.Pixbuf? _image;
    internal string? Message { get; set; }
    internal bool HasImage => _image is not null;

    internal ImageCanvas()
    {
        Hexpand = true;
        Vexpand = true;
        Drawn += (_, args) =>
        {
            var context = args.Cr;
            if (_image is null)
            {
                if (Message is not { } message) return;
                using var text = CreatePangoLayout(message);
                text.GetPixelSize(out var textWidth, out var textHeight);
                StyleContext.RenderLayout(context, Math.Max(8, (Allocation.Width - textWidth) / 2),
                    Math.Max(8, (Allocation.Height - textHeight) / 2), text);
                return;
            }
            var scale = Math.Min((double)Allocation.Width / _image.Width, (double)Allocation.Height / _image.Height);
            if (scale <= 0) return;
            context.Save();
            context.Translate((Allocation.Width - _image.Width * scale) / 2, (Allocation.Height - _image.Height * scale) / 2);
            context.Scale(scale, scale);
            Gdk.CairoHelper.SetSourcePixbuf(context, _image, 0, 0);
            context.Paint();
            context.Restore();
        };
        Destroyed += (_, _) => { _image?.Dispose(); _image = null; };
    }

    internal void SetImage(Gdk.Pixbuf? image, string? message = null)
    {
        _image?.Dispose();
        _image = image;
        Message = message;
        QueueDraw();
    }
}
