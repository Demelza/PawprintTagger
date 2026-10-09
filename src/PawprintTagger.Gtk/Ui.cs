using Gtk;

namespace PawprintTagger.Gtk;

internal static class Ui
{
    internal static Label Label(string text) => new(text) { Xalign = 0, Wrap = true };

    internal static void Clear(Container container)
    {
        foreach (var child in container.Children)
        {
            container.Remove(child);
            child.Destroy();
            child.Dispose();
        }
    }

    internal static void Error(Window parent, string title, string detail)
    {
        using var dialog = new MessageDialog(parent, DialogFlags.Modal | DialogFlags.DestroyWithParent,
            MessageType.Error, ButtonsType.Close, false, "{0}", title) { SecondaryText = detail };
        dialog.Run();
        dialog.Destroy();
    }

    internal static bool Confirm(Window parent, string title, string detail)
    {
        using var dialog = new MessageDialog(parent, DialogFlags.Modal | DialogFlags.DestroyWithParent,
            MessageType.Question, ButtonsType.YesNo, false, "{0}", title) { SecondaryText = detail };
        var response = (ResponseType)dialog.Run();
        dialog.Destroy();
        return response == ResponseType.Yes;
    }
}
