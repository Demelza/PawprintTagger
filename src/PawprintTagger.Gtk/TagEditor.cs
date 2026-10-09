using Gtk;
using PawprintTagger.Core;

namespace PawprintTagger.Gtk;

internal sealed class TagEditor : Dialog
{
    private readonly Entry _name = new();
    private readonly ComboBoxText _parent = new();
    private readonly CheckButton _useColor = new("Color label");
    private readonly ColorButton _color = new() { UseAlpha = false };
    private readonly Label _error = Ui.Label("");
    private readonly LibraryStore _library;
    private readonly TagNode? _editing;
    private readonly bool _isCategory;
    internal TagNode? SavedTag { get; private set; }

    internal TagEditor(Window owner, LibraryStore library, TagNode? editing, bool category, Guid? parent)
        : base(editing is null ? category ? "New category" : "New tag" : category ? "Edit category" : "Edit tag",
            owner, DialogFlags.Modal | DialogFlags.DestroyWithParent)
    {
        _library = library;
        _editing = editing;
        _isCategory = category;
        SetDefaultSize(420, 240);
        Resizable = false;
        AddButton("Cancel", ResponseType.Cancel);
        AddButton("Save", ResponseType.Ok);
        DefaultResponse = ResponseType.Ok;
        _name.ActivatesDefault = true;
        _name.Text = editing?.Name ?? "";
        _parent.Append("root", "Library root");
        var snapshot = library.Snapshot;
        var excluded = editing is null ? new HashSet<Guid>() : snapshot.Descendants(editing.Id);
        foreach (var node in snapshot.Tags.Where(tag => tag.IsCategory && !excluded.Contains(tag.Id))
            .OrderBy(tag => snapshot.TagPath(tag.Id), StringComparer.CurrentCultureIgnoreCase))
            _parent.Append(node.Id.ToString(), snapshot.TagPath(node.Id));
        _parent.ActiveId = (editing?.ParentId ?? parent)?.ToString() ?? "root";
        if (_parent.Active < 0) _parent.ActiveId = "root";
        var rgba = new Gdk.RGBA();
        rgba.Parse(editing?.Color ?? "#729FCF");
        _color.Rgba = rgba;
        _useColor.Active = editing?.Color is not null;
        _color.Sensitive = _useColor.Active;
        _useColor.Toggled += (_, _) => _color.Sensitive = _useColor.Active;
        var grid = new Grid { RowSpacing = 10, ColumnSpacing = 12, BorderWidth = 12 };
        grid.Attach(Ui.Label("Name"), 0, 0, 1, 1);
        grid.Attach(_name, 1, 0, 1, 1);
        grid.Attach(Ui.Label("Category"), 0, 1, 1, 1);
        grid.Attach(_parent, 1, 1, 1, 1);
        grid.Attach(_useColor, 0, 2, 1, 1);
        grid.Attach(_color, 1, 2, 1, 1);
        grid.Attach(Ui.Label("Tags inherit their category's color unless you choose a color here."), 0, 3, 2, 1);
        grid.Attach(_error, 0, 4, 2, 1);
        ContentArea.PackStart(grid, true, true, 0);
        ShowAll();
        _name.GrabFocus();
    }

    internal TagNode? Edit()
    {
        while ((ResponseType)Run() == ResponseType.Ok)
        {
            try
            {
                Guid? parent = Guid.TryParse(_parent.ActiveId, out var id) ? id : null;
                string? color = null;
                if (_useColor.Active)
                {
                    var rgba = _color.Rgba;
                    color = $"#{(int)Math.Round(rgba.Red * 255):X2}{(int)Math.Round(rgba.Green * 255):X2}{(int)Math.Round(rgba.Blue * 255):X2}";
                }
                SavedTag = _library.SaveTag(_editing?.Id, _name.Text, parent, _isCategory, color);
                break;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            { _error.Text = ex.Message; }
        }
        Destroy();
        return SavedTag;
    }
}
