using System.Text;
using Gtk;
using PawprintTagger.Core;

namespace PawprintTagger.Gtk;

/// <summary>Use GTK's themed, keyboard-accessible completion popup for library tags.</summary>
internal sealed class TagSearchCompletion : IDisposable
{
    private readonly Entry _entry;
    private readonly ListStore _model = new(typeof(string), typeof(string));
    private TagSearchSuggestions _suggestions = new(LibrarySnapshot.Empty);
    private readonly Dictionary<Guid, TagSuggestion> _tags = new();
    private HashSet<Guid> _matches = new();
    private string? _lastQuery;
    private int _lastCursor = -1;
    internal IReadOnlyList<TagSuggestion> CurrentSuggestions => _matches.Select(id => _tags[id]).ToArray();
    internal EntryCompletion Completion { get; } = new()
    {
        MinimumKeyLength = 1, PopupCompletion = true, PopupSingleMatch = true,
        InlineCompletion = false, InlineSelection = false, PopupSetWidth = true
    };

    internal TagSearchCompletion(Entry entry)
    {
        _entry = entry;
        Completion.Model = _model;
        Completion.TextColumn = 0;
        Completion.Clear();
        var text = new CellRendererText { Ellipsize = Pango.EllipsizeMode.End };
        Completion.PackStart(text, true);
        Completion.AddAttribute(text, "text", 0);
        Completion.MatchFunc = (_, _, iter) =>
        {
            var query = _entry.Text;
            var cursor = CursorOffset();
            if (query != _lastQuery || cursor != _lastCursor)
            {
                _lastQuery = query;
                _lastCursor = cursor;
                _matches = _suggestions.Find(query, cursor).Select(tag => tag.Id).ToHashSet();
            }
            // AppendValues briefly emits an empty row before assigning its columns.
            return _model.GetValue(iter, 1) is string value && Guid.TryParse(value, out var id) && _matches.Contains(id);
        };
        Completion.MatchSelected += OnMatchSelected;
        _entry.Completion = Completion;
    }

    // Run before GTK's default handler, which would replace the entire query with the display text.
    [GLib.ConnectBefore]
    private void OnMatchSelected(object sender, MatchSelectedArgs args)
    {
        var id = Guid.Parse((string)args.Model.GetValue(args.Iter, 1));
        var completed = _suggestions.Complete(_entry.Text, CursorOffset(), _tags[id]);
        _entry.Text = completed.Text;
        _entry.Position = completed.Text[..completed.Cursor].EnumerateRunes().Count();
        args.RetVal = true;
    }

    internal void Refresh(LibrarySnapshot snapshot)
    {
        _lastQuery = null;
        _matches.Clear();
        _suggestions = new(snapshot);
        _tags.Clear();
        _model.Clear();
        foreach (var tag in _suggestions.Tags)
        {
            _tags.Add(tag.Id, tag);
            _model.AppendValues(tag.Path, tag.Id.ToString());
        }
    }

    private int CursorOffset()
    {
        // GTK uses Unicode characters; C# string spans use UTF-16 code units.
        var position = _entry.Position;
        var offset = 0;
        foreach (var rune in _entry.Text.EnumerateRunes())
        {
            if (position-- <= 0) break;
            offset += rune.Utf16SequenceLength;
        }
        return offset;
    }

    public void Dispose()
    {
        _entry.Completion = null;
        Completion.Dispose();
        _model.Dispose();
    }
}
