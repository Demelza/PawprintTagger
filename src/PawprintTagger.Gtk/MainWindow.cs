using Gtk;
using PawprintTagger.Core;
using Task = System.Threading.Tasks.Task;
using IOPath = System.IO.Path;

namespace PawprintTagger.Gtk;

internal sealed class MainWindow : Window
{
    private LibraryStore _library;
    private readonly string? _settingsPath;
    private readonly ImageLoader _loader = new();
    private readonly Stack _pages = new();
    private readonly TreeStore _tagModel = new(typeof(string), typeof(string), typeof(string), typeof(bool), typeof(string), typeof(string));
    private readonly TreeView _tagTree;
    private readonly ListStore _imageModel = new(typeof(Gdk.Pixbuf), typeof(string), typeof(string), typeof(string));
    private readonly IconView _images;
    private readonly ScrolledWindow _imageScroll = new();
    private readonly ListStore _assignmentModel = new(typeof(bool), typeof(string), typeof(string), typeof(string));
    private readonly TreeView _assignments;
    private readonly CellRendererToggle _assignmentToggle = new() { Activatable = true };
    private readonly SearchEntry _search = new() { PlaceholderText = "Search tags or filenames…" };
    private readonly Label _results = Ui.Label("");
    private readonly Label _empty = new("Import images to start your library.") { Wrap = true };
    private readonly Stack _gridPages = new();
    private readonly Label _status = Ui.Label("Ready");
    private readonly Label _details = Ui.Label("Select an image to see its details.");
    private readonly Label _selectedName = Ui.Label("Image details");
    private readonly ImageCanvas _preview = new();
    private readonly ImageCanvas _viewer = new();
    private readonly Label _viewerTitle = Ui.Label("");
    private readonly Button _importButton = new("Import images…");
    private readonly Button _folderButton = new("Library folder…");
    private readonly Button _editTag = new("Edit");
    private readonly Button _deleteTag = new("Delete");
    private readonly Button _previous = new("Previous");
    private readonly Button _next = new("Next");
    private readonly Button _cancelImport = new("Cancel import") { NoShowAll = true };
    private readonly ProgressBar _progress = new() { NoShowAll = true };
    private readonly Gdk.Pixbuf _placeholder = new(Gdk.Colorspace.Rgb, true, 8, 160, 120);
    private readonly HashSet<Guid> _loaded = new();
    private readonly HashSet<Guid> _pending = new();
    private readonly Dictionary<Guid, int> _rows = new();
    private LibraryImage[] _filtered = Array.Empty<LibraryImage>();
    private Guid? _selectedNode;
    private Guid? _selectedImage;
    private CancellationTokenSource _thumbnailsCancel = new();
    private CancellationTokenSource? _previewCancel;
    private CancellationTokenSource? _viewerCancel;
    private CancellationTokenSource? _importCancel;
    private System.Threading.Timer? _searchTimer;
    private System.Threading.Timer? _viewerTimer;
    private int _generation;
    private int _visibleFirst;
    private int _visibleLast = -1;
    private bool _refreshing;
    private bool _thumbnailUpdateQueued;
    private bool _closing;
    private BrowserImportServer? _browserServer;
    private int _browserImports;

    internal string LibraryFolder => _library.Folder;
    internal int VisibleImageCount => _filtered.Length;
    internal bool ViewerVisible => _pages.VisibleChildName == "viewer";
    internal bool ViewerHasImage => _viewer.HasImage;
    internal int LoadedThumbnailCount => _loaded.Count;
    internal bool IsImportRunning => _importCancel is not null || _browserImports > 0;
    internal IconView ImageGrid => _images;
    internal CellRendererToggle AssignmentToggle => _assignmentToggle;
    internal bool GalleryFitsViewport => _imageScroll.Hadjustment.Upper <= _imageScroll.Hadjustment.PageSize + 1;
    internal string SearchText { get => _search.Text; set { _search.Text = value; RefreshImages(); } }

    internal MainWindow(LibraryStore library, string? settingsPath) : base("Pawprint Tagger")
    {
        _library = library;
        _settingsPath = settingsPath;
        SetDefaultSize(1280, 800);
        SetSizeRequest(900, 540);
        IconName = "image-x-generic";
        _placeholder.Fill(0x00000000);
        _tagTree = new TreeView(_tagModel) { HeadersVisible = false, TooltipColumn = 5 };
        _images = new IconView(_imageModel)
        {
            TooltipColumn = 3, SelectionMode = SelectionMode.Single,
            Columns = -1, ItemWidth = 160, Margin = 12, ItemPadding = 6, ColumnSpacing = 12, RowSpacing = 12,
            ItemOrientation = Orientation.Vertical, Hexpand = true, Vexpand = true
        };
        var thumbnailCell = new CellRendererPixbuf { Xalign = 0.5f, Width = 160, Height = 120 };
        var filenameCell = new CellRendererText
        {
            Xalign = 0.5f, Yalign = 0.5f, Width = 160, Height = 32, WrapWidth = 160,
            WrapMode = Pango.WrapMode.WordChar, Ellipsize = Pango.EllipsizeMode.End,
            Alignment = Pango.Alignment.Center
        };
        _images.PackStart(thumbnailCell, false);
        _images.AddAttribute(thumbnailCell, "pixbuf", 0);
        _images.PackStart(filenameCell, false);
        _images.AddAttribute(filenameCell, "text", 1);
        _assignments = new TreeView(_assignmentModel) { HeadersVisible = false, TooltipColumn = 3 };
        BuildTagTree();
        BuildAssignmentTree();
        _images.SelectionChanged += (_, _) => SelectImageFromGrid();
        _images.ItemActivated += (_, args) =>
        {
            if (_imageModel.GetIter(out var iter, args.Path)) OpenImage(Guid.Parse((string)_imageModel.GetValue(iter, 2)));
        };
        _images.SizeAllocated += (_, _) => QueueThumbnails();
        _imageScroll.Vadjustment.ValueChanged += (_, _) => QueueThumbnails();
        _imageScroll.Vadjustment.Changed += (_, _) => QueueThumbnails();
        _importButton.Clicked += (_, _) => ChooseImages();
        _folderButton.Clicked += (_, _) => ChooseLibrary();
        _editTag.Clicked += (_, _) => EditTag();
        _deleteTag.Clicked += (_, _) => DeleteTag();
        _cancelImport.Clicked += (_, _) => _importCancel?.Cancel();
        _search.Changed += (_, _) =>
        {
            _searchTimer?.Dispose();
            _searchTimer = new System.Threading.Timer(_ => GtkDispatcher.Post(() =>
                { if (!_closing) RefreshImages(); }), null, 180, System.Threading.Timeout.Infinite);
        };
        _search.Activated += (_, _) => RefreshImages();
        Add(_pages);
        _pages.AddNamed(BuildLibraryView(), "library");
        _pages.AddNamed(BuildViewer(), "viewer");
        _pages.VisibleChildName = "library";
        KeyPressEvent += OnKeyPress;
        DeleteEvent += (_, args) =>
        {
            args.RetVal = true;
            if (_importCancel is not null)
            {
                _importCancel.Cancel();
                _status.Text = "Finishing the current import before closing…";
                _closing = true;
            }
            else CloseApp();
        };
        RefreshAll();
        try
        {
            // Use the user's normal settings identity even for --library windows, so imports reach the open library.
            _browserServer = new BrowserImportServer(BrowserImportServer.PipeName(settingsPath ?? AppSettings.DefaultSettingsPath),
                HandleBrowserRequest);
        }
        catch (IOException ex)
        { _status.Text = "Browser imports are handled by another Pawprint Tagger window: " + ex.Message; }
    }

    private Widget BuildLibraryView()
    {
        var root = new Box(Orientation.Vertical, 0);
        // Twenty homogeneous columns keep the tag rail at exactly 15% as the window resizes.
        var columns = new Grid { ColumnHomogeneous = true, Hexpand = true, Vexpand = true };
        var left = new Box(Orientation.Vertical, 6) { BorderWidth = 6 };
        left.PackStart(Ui.Label("Tags & categories"), false, false, 2);
        var all = new Button("All images");
        all.Clicked += (_, _) => { _tagTree.Selection.UnselectAll(); _selectedNode = null; RefreshImages(); };
        left.PackStart(all, false, false, 0);
        var addTag = new Button("+ Tag") { TooltipText = "Add a tag" };
        addTag.Clicked += (_, _) => AddTag(false);
        var addCategory = new Button("+ Group") { TooltipText = "Add a tag group" };
        addCategory.Clicked += (_, _) => AddTag(true);
        _editTag.TooltipText = "Edit or move the selected tag or group";
        _deleteTag.TooltipText = "Delete the selected tag or group";
        var tagActions = new Box(Orientation.Horizontal, 0) { Homogeneous = true };
        // Keep the narrow tag rail usable while preserving themed buttons and the system font.
        using var compactButtons = new CssProvider();
        compactButtons.LoadFromData("button { min-width: 0; padding: 0; }");
        foreach (var button in new[] { addTag, addCategory, _editTag, _deleteTag })
        {
            button.StyleContext.AddProvider(compactButtons, 600);
            if (button.Child is Label label) label.Ellipsize = Pango.EllipsizeMode.End;
            tagActions.PackStart(button, true, true, 0);
        }
        left.PackStart(tagActions, false, false, 0);
        var treeScroll = new ScrolledWindow();
        treeScroll.SetPolicy(PolicyType.Never, PolicyType.Automatic);
        treeScroll.Add(_tagTree);
        left.PackStart(treeScroll, true, true, 0);
        left.PackStart(new Separator(Orientation.Horizontal), false, false, 4);
        left.PackStart(_folderButton, false, false, 0);
        columns.Attach(left, 0, 0, 3, 1);

        var middle = new Box(Orientation.Vertical, 8) { BorderWidth = 8, Hexpand = true, Vexpand = true };
        var toolbar = new Box(Orientation.Horizontal, 6);
        toolbar.PackStart(_search, true, true, 0);
        var help = new Button("?") { TooltipText = "Search syntax" };
        help.Clicked += (_, _) => ShowSearchHelp();
        toolbar.PackStart(help, false, false, 0);
        toolbar.PackStart(_importButton, false, false, 0);
        middle.PackStart(toolbar, false, false, 0);
        middle.PackStart(_results, false, false, 0);
        _imageScroll.SetPolicy(PolicyType.Automatic, PolicyType.Automatic);
        _imageScroll.Add(_images);
        _gridPages.AddNamed(_imageScroll, "images");
        _gridPages.AddNamed(_empty, "empty");
        middle.PackStart(_gridPages, true, true, 0);
        var importRow = new Box(Orientation.Horizontal, 6);
        importRow.PackStart(_progress, true, true, 0);
        importRow.PackStart(_cancelImport, false, false, 0);
        middle.PackStart(importRow, false, false, 0);
        columns.Attach(middle, 3, 0, 12, 1);

        // Five equal rows keep the preview section at 40% as the window resizes.
        var right = new Grid { RowHomogeneous = true, RowSpacing = 8, BorderWidth = 8, Vexpand = true };
        var previewSection = new Box(Orientation.Vertical, 8);
        _selectedName.Ellipsize = Pango.EllipsizeMode.End;
        _selectedName.Wrap = false;
        previewSection.PackStart(_selectedName, false, false, 0);
        previewSection.PackStart(_preview, true, true, 0);
        right.Attach(previewSection, 0, 0, 1, 2);
        var imageInfo = new Box(Orientation.Vertical, 8);
        _details.Selectable = true;
        imageInfo.PackStart(_details, false, false, 0);
        imageInfo.PackStart(new Separator(Orientation.Horizontal), false, false, 0);
        imageInfo.PackStart(Ui.Label("Image tags"), false, false, 0);
        imageInfo.PackStart(Ui.Label("Check tags to assign them to this image."), false, false, 0);
        var assignmentScroll = new ScrolledWindow();
        assignmentScroll.SetPolicy(PolicyType.Never, PolicyType.Automatic);
        assignmentScroll.Add(_assignments);
        imageInfo.PackStart(assignmentScroll, true, true, 0);
        right.Attach(imageInfo, 0, 2, 1, 3);
        columns.Attach(right, 15, 0, 5, 1);
        root.PackStart(columns, true, true, 0);
        root.PackStart(new Separator(Orientation.Horizontal), false, false, 0);
        _status.Ellipsize = Pango.EllipsizeMode.End;
        _status.Wrap = false;
        _status.Margin = 8;
        _status.TooltipText = _library.Folder;
        root.PackStart(_status, false, false, 0);
        return root;
    }

    private Widget BuildViewer()
    {
        var page = new Box(Orientation.Vertical, 6) { BorderWidth = 8 };
        var bar = new Box(Orientation.Horizontal, 8);
        var back = new Button("← Back");
        back.Clicked += (_, _) => BackToLibrary();
        _previous.Clicked += (_, _) => NavigateImage(-1);
        _next.Clicked += (_, _) => NavigateImage(1);
        _viewerTitle.Ellipsize = Pango.EllipsizeMode.End;
        _viewerTitle.Wrap = false;
        bar.PackStart(back, false, false, 0);
        bar.PackStart(_viewerTitle, true, true, 0);
        bar.PackStart(_previous, false, false, 0);
        bar.PackStart(_next, false, false, 0);
        page.PackStart(bar, false, false, 0);
        page.PackStart(_viewer, true, true, 0);
        _viewer.SizeAllocated += (_, _) =>
        {
            _viewerTimer?.Dispose();
            _viewerTimer = new System.Threading.Timer(_ => GtkDispatcher.Post(() =>
            {
                if (!_closing && ViewerVisible && _selectedImage is { } id) LoadViewer(id, clear: false);
            }), null, 150, System.Threading.Timeout.Infinite);
        };
        return page;
    }

    private void BuildTagTree()
    {
        var column = new TreeViewColumn();
        var dot = new CellRendererText();
        column.PackStart(dot, false);
        column.AddAttribute(dot, "text", 0);
        column.AddAttribute(dot, "foreground", 2);
        column.AddAttribute(dot, "foreground-set", 3);
        var name = new CellRendererText { Ellipsize = Pango.EllipsizeMode.End };
        column.PackStart(name, true);
        column.AddAttribute(name, "text", 1);
        column.Expand = true;
        _tagTree.AppendColumn(column);
        _tagTree.Selection.Changed += (_, _) =>
        {
            if (_refreshing) return;
            _selectedNode = _tagTree.Selection.GetSelected(out var model, out var iter) &&
                Guid.TryParse((string)model.GetValue(iter, 4), out var id) ? id : null;
            UpdateTagButtons();
            RefreshImages();
        };
    }

    private void BuildAssignmentTree()
    {
        var toggle = _assignmentToggle;
        toggle.Toggled += (_, args) =>
        {
            if (_selectedImage is not { } image || !_assignmentModel.GetIterFromString(out var iter, args.Path)) return;
            var tag = Guid.Parse((string)_assignmentModel.GetValue(iter, 2));
            var assigned = (bool)_assignmentModel.GetValue(iter, 0);
            try
            {
                _library.SetImageTag(image, tag, !assigned);
                RefreshAll();
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            { Ui.Error(this, "Could not save image tags", ex.Message); }
        };
        _assignments.AppendColumn("Assigned", toggle, "active", 0);
        var name = new CellRendererText { Ellipsize = Pango.EllipsizeMode.End };
        _assignments.AppendColumn("Tag", name, "text", 1).Expand = true;
    }

    private void UpdateTagButtons()
    {
        _editTag.Sensitive = _selectedNode.HasValue;
        _deleteTag.Sensitive = _selectedNode.HasValue;
    }

    internal void FilterCategory(Guid? id)
    {
        _selectedNode = id;
        RefreshImages();
    }

    internal void RefreshAll()
    {
        _refreshing = true;
        try
        {
            var snapshot = _library.Snapshot;
            var expanded = new HashSet<Guid>();
            _tagTree.MapExpandedRows((_, path) =>
            {
                if (_tagModel.GetIter(out var iter, path) && Guid.TryParse((string)_tagModel.GetValue(iter, 4), out var id))
                    expanded.Add(id);
            });
            _tagModel.Clear();
            var counts = snapshot.Tags.ToDictionary(tag => tag.Id, _ => 0);
            var lookup = snapshot.Tags.ToDictionary(tag => tag.Id);
            foreach (var image in snapshot.Images)
            {
                var counted = new HashSet<Guid>();
                foreach (var id in image.TagIds)
                {
                    var ancestor = (Guid?)id;
                    while (ancestor is { } current && counted.Add(current)) ancestor = lookup[current].ParentId;
                }
                foreach (var id in counted) counts[id]++;
            }
            TreePath? selection = null;
            void Append(Guid? parent, TreeIter? parentIter)
            {
                foreach (var tag in snapshot.Tags.Where(tag => tag.ParentId == parent)
                    .OrderByDescending(tag => tag.IsCategory).ThenBy(tag => tag.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    var color = snapshot.EffectiveColor(tag.Id);
                    object[] values = { tag.IsCategory ? "▸" : "●", $"{tag.Name} ({counts[tag.Id]})",
                        color ?? "#000000", color is not null, tag.Id.ToString(), snapshot.TagPath(tag.Id) };
                    var iter = parentIter is { } pi ? _tagModel.AppendValues(pi, values) : _tagModel.AppendValues(values);
                    if (_selectedNode == tag.Id) selection = _tagModel.GetPath(iter);
                    Append(tag.Id, iter);
                    if (expanded.Contains(tag.Id)) { using var path = _tagModel.GetPath(iter); _tagTree.ExpandRow(path, false); }
                }
            }
            Append(null, null);
            if (selection is not null)
            {
                using (selection) { _tagTree.ExpandToPath(selection); _tagTree.Selection.SelectPath(selection); }
            }
            else _selectedNode = null;
            UpdateTagButtons();
        }
        finally { _refreshing = false; }
        RefreshImages();
    }

    internal void RefreshImages()
    {
        if (_closing) return;
        var snapshot = _library.Snapshot;
        ImageSearch query;
        try { query = new(_search.Text, snapshot, _selectedNode); }
        catch (ArgumentException ex) { _results.Text = ex.Message; return; }
        _thumbnailsCancel.Cancel();
        _thumbnailsCancel.Dispose();
        _thumbnailsCancel = new();
        _generation++;
        _visibleFirst = 0;
        _visibleLast = -1;
        _pending.Clear();
        _loaded.Clear();
        _rows.Clear();
        _filtered = snapshot.Images.Where(query.Matches).OrderByDescending(image => image.ImportedAt).ThenBy(image => image.Id).ToArray();
        _refreshing = true;
        _imageModel.Clear();
        for (var index = 0; index < _filtered.Length; index++)
        {
            var image = _filtered[index];
            _rows.Add(image.Id, index);
            _imageModel.AppendValues(_placeholder, image.OriginalName, image.Id.ToString(),
                $"{image.OriginalName}\n{image.Width} × {image.Height}");
        }
        _refreshing = false;
        _results.Text = $"{_filtered.Length:N0} of {snapshot.Images.Count:N0} images" +
            (_selectedNode is { } node ? " · " + snapshot.TagPath(node) : "");
        _empty.Text = snapshot.Images.Count == 0 ? "Import images to start your library." : "No images match this search.";
        _gridPages.VisibleChildName = _filtered.Length == 0 ? "empty" : "images";
        if (_selectedImage is { } selected && _rows.TryGetValue(selected, out var row))
        {
            using var path = new TreePath(row.ToString());
            _images.SelectPath(path);
        }
        else _selectedImage = null;
        RefreshDetails();
        QueueThumbnails();
    }

    private void SelectImageFromGrid()
    {
        if (_refreshing) return;
        var paths = _images.SelectedItems;
        try
        {
            _selectedImage = paths.Length > 0 && _imageModel.GetIter(out var iter, paths[0])
                ? Guid.Parse((string)_imageModel.GetValue(iter, 2)) : null;
        }
        finally { foreach (var path in paths) path.Dispose(); }
        RefreshDetails();
    }

    private void RefreshDetails()
    {
        _previewCancel?.Cancel();
        _previewCancel?.Dispose();
        _previewCancel = new();
        var token = _previewCancel.Token;
        var snapshot = _library.Snapshot;
        var image = snapshot.Images.SingleOrDefault(image => image.Id == _selectedImage);
        _assignmentModel.Clear();
        _assignments.Sensitive = image is not null;
        if (image is null)
        {
            _selectedName.Text = "Image details";
            _details.Text = "Select an image to see its details.";
            _preview.SetImage(null);
            return;
        }
        _selectedName.Text = image.OriginalName;
        _selectedName.TooltipText = image.OriginalName;
        _details.Text = $"{image.Width:N0} × {image.Height:N0} pixels\n{FormatSize(image.FileSize)} · " +
            $"{IOPath.GetExtension(image.OriginalName).TrimStart('.').ToUpperInvariant()}\n" +
            $"Added {image.ImportedAt.ToLocalTime():g}\n{image.TagIds.Count} tags";
        foreach (var tag in snapshot.Tags.Where(tag => !tag.IsCategory)
            .OrderBy(tag => snapshot.TagPath(tag.Id), StringComparer.CurrentCultureIgnoreCase))
        {
            var path = snapshot.TagPath(tag.Id);
            _assignmentModel.AppendValues(image.TagIds.Contains(tag.Id), path, tag.Id.ToString(), path);
        }
        _preview.SetImage(null, "Loading…");
        _ = LoadPreview(image, token);
    }

    private async Task LoadPreview(LibraryImage image, CancellationToken token)
    {
        try
        {
            var pixbuf = await _loader.LoadAsync(_library.ImagePath(image), 400, 320, token).ConfigureAwait(false);
            GtkDispatcher.Post(() =>
            {
                if (_closing || token.IsCancellationRequested) pixbuf.Dispose();
                else _preview.SetImage(pixbuf);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is GLib.GException or IOException or UnauthorizedAccessException)
        { GtkDispatcher.Post(() => { if (!_closing && !token.IsCancellationRequested) _preview.SetImage(null, "Image unavailable"); }); }
    }

    private void QueueThumbnails()
    {
        if (_closing || _thumbnailUpdateQueued) return;
        _thumbnailUpdateQueued = true;
        GtkDispatcher.Post(() =>
        {
            _thumbnailUpdateQueued = false;
            if (!_closing) UpdateThumbnails();
        });
    }

    private void UpdateThumbnails()
    {
        if (!_images.GetVisibleRange(out var start, out var end)) return;
        int first, last;
        using (start) using (end)
        {
            first = Math.Max(0, start.Indices[0] - 12);
            last = Math.Min(_filtered.Length - 1, end.Indices[0] + 12);
        }
        _visibleFirst = first;
        _visibleLast = last;
        // Release images outside the viewport; the whole library is scrollable without retaining every bitmap.
        foreach (var id in _loaded.Where(id => !_rows.TryGetValue(id, out var row) || row < first || row > last).ToArray())
        {
            if (_rows.TryGetValue(id, out var row) && _imageModel.GetIterFromString(out var iter, row.ToString()))
                _imageModel.SetValue(iter, 0, _placeholder);
            _loaded.Remove(id);
        }
        for (var row = first; row <= last; row++)
        {
            var image = _filtered[row];
            if (_loaded.Contains(image.Id) || !_pending.Add(image.Id)) continue;
            _ = LoadThumbnail(image, row, _generation, _thumbnailsCancel.Token);
        }
    }

    private async Task LoadThumbnail(LibraryImage image, int row, int generation, CancellationToken token)
    {
        try
        {
            var pixbuf = await _loader.LoadAsync(_library.ImagePath(image), 160, 120, token, _library.ThumbnailPath(image))
                .ConfigureAwait(false);
            GtkDispatcher.Post(() =>
            {
                using (pixbuf)
                {
                    if (_closing || token.IsCancellationRequested || generation != _generation) return;
                    _pending.Remove(image.Id);
                    if (row < _visibleFirst || row > _visibleLast) return;
                    if (!_imageModel.GetIterFromString(out var iter, row.ToString())) return;
                    _imageModel.SetValue(iter, 0, pixbuf);
                    _loaded.Add(image.Id);
                }
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is GLib.GException or IOException or UnauthorizedAccessException)
        {
            GtkDispatcher.Post(() =>
            {
                if (_closing || generation != _generation) return;
                _pending.Remove(image.Id);
                _loaded.Add(image.Id); // Do not retry a missing image on every scroll event.
                if (_imageModel.GetIterFromString(out var iter, row.ToString()))
                    _imageModel.SetValue(iter, 3, image.OriginalName + "\nImage unavailable: " + ex.Message);
            });
        }
    }

    internal void OpenImage(Guid id)
    {
        _selectedImage = id;
        _pages.VisibleChildName = "viewer";
        LoadViewer(id, clear: true);
    }

    private void LoadViewer(Guid id, bool clear)
    {
        var image = _library.Snapshot.Images.SingleOrDefault(image => image.Id == id);
        if (image is null) return;
        _viewerCancel?.Cancel();
        _viewerCancel?.Dispose();
        _viewerCancel = new();
        var token = _viewerCancel.Token;
        _viewerTitle.Text = $"{image.OriginalName} · {image.Width} × {image.Height}";
        var index = Array.FindIndex(_filtered, item => item.Id == id);
        _previous.Sensitive = index > 0;
        _next.Sensitive = index >= 0 && index < _filtered.Length - 1;
        if (clear) _viewer.SetImage(null, "Loading…");
        _ = LoadViewerImage(image, Math.Max(640, _viewer.Allocation.Width), Math.Max(480, _viewer.Allocation.Height), token);
    }

    private async Task LoadViewerImage(LibraryImage image, int width, int height, CancellationToken token)
    {
        try
        {
            var pixbuf = await _loader.LoadAsync(_library.ImagePath(image), width, height, token).ConfigureAwait(false);
            GtkDispatcher.Post(() =>
            {
                if (_closing || token.IsCancellationRequested) pixbuf.Dispose();
                else _viewer.SetImage(pixbuf);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is GLib.GException or IOException or UnauthorizedAccessException)
        { GtkDispatcher.Post(() => { if (!_closing && !token.IsCancellationRequested) _viewer.SetImage(null, "Image unavailable: " + ex.Message); }); }
    }

    internal void BackToLibrary()
    {
        _viewerCancel?.Cancel();
        _viewerTimer?.Dispose();
        _viewer.SetImage(null);
        _pages.VisibleChildName = "library";
        if (_selectedImage is { } id && _rows.TryGetValue(id, out var row))
        {
            using var path = new TreePath(row.ToString());
            _images.SelectPath(path);
            _images.ScrollToPath(path, 0.5f, 0.5f);
        }
        RefreshDetails();
        _images.GrabFocus();
        QueueThumbnails();
    }

    private void NavigateImage(int offset)
    {
        var index = Array.FindIndex(_filtered, image => image.Id == _selectedImage) + offset;
        if (index >= 0 && index < _filtered.Length) OpenImage(_filtered[index].Id);
    }

    private void OnKeyPress(object sender, KeyPressEventArgs args)
    {
        if (ViewerVisible)
        {
            switch (args.Event.Key)
            {
                case Gdk.Key.Escape: BackToLibrary(); args.RetVal = true; break;
                case Gdk.Key.Left: NavigateImage(-1); args.RetVal = true; break;
                case Gdk.Key.Right: NavigateImage(1); args.RetVal = true; break;
            }
        }
        else if ((args.Event.State & Gdk.ModifierType.ControlMask) != 0)
        {
            if (args.Event.Key is Gdk.Key.f or Gdk.Key.F) { _search.GrabFocus(); args.RetVal = true; }
            if (args.Event.Key is Gdk.Key.o or Gdk.Key.O && !IsImportRunning) { ChooseImages(); args.RetVal = true; }
        }
    }

    private Guid? NewTagParent()
    {
        var selected = _library.Snapshot.Tags.SingleOrDefault(tag => tag.Id == _selectedNode);
        return selected is null ? null : selected.IsCategory ? selected.Id : selected.ParentId;
    }

    private void AddTag(bool category)
    {
        using var editor = new TagEditor(this, _library, null, category, NewTagParent());
        if (editor.Edit() is not null) RefreshAll();
    }

    private void EditTag()
    {
        var tag = _library.Snapshot.Tags.SingleOrDefault(tag => tag.Id == _selectedNode);
        if (tag is null) return;
        using var editor = new TagEditor(this, _library, tag, tag.IsCategory, tag.ParentId);
        if (editor.Edit() is not null) RefreshAll();
    }

    private void DeleteTag()
    {
        var tag = _library.Snapshot.Tags.SingleOrDefault(tag => tag.Id == _selectedNode);
        if (tag is null) return;
        var detail = tag.IsCategory ? "This removes the category, its child categories and tags, and their image assignments."
            : "This removes the tag and its image assignments.";
        if (!Ui.Confirm(this, $"Delete '{tag.Name}'?", detail)) return;
        try { _library.DeleteTag(tag.Id); _selectedNode = null; RefreshAll(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Ui.Error(this, "Could not delete tag", ex.Message); }
    }

    private void ChooseImages()
    {
        using var dialog = new FileChooserDialog("Import images", this, FileChooserAction.Open,
            "Cancel", ResponseType.Cancel, "Import", ResponseType.Accept) { SelectMultiple = true };
        var filter = new FileFilter { Name = "Images supported by GTK" };
        filter.AddPixbufFormats();
        dialog.AddFilter(filter);
        var all = new FileFilter { Name = "All files" };
        all.AddPattern("*");
        dialog.AddFilter(all);
        var accepted = (ResponseType)dialog.Run() == ResponseType.Accept;
        var files = accepted ? dialog.Filenames : Array.Empty<string>();
        dialog.Destroy();
        if (files.Length > 0) StartImport(files);
    }

    internal void StartImport(string[] files)
    {
        if (_importCancel is not null || _browserImports > 0) return;
        _importCancel = new();
        _importButton.Sensitive = false;
        _folderButton.Sensitive = false;
        _progress.Fraction = 0;
        _progress.Show();
        _cancelImport.Show();
        _status.Text = "Copying images into the library…";
        _ = ImportFiles(files, _importCancel.Token);
    }

    private async Task ImportFiles(string[] files, CancellationToken token)
    {
        ImportReport? report = null;
        Exception? error = null;
        try
        {
            report = await Task.Run(() => _library.ImportAsync(files, ImageLoader.Inspect,
                new ImportUiProgress(value => GtkDispatcher.Post(() =>
                {
                    if (_closing) return;
                    _progress.Fraction = (double)value.Completed / value.Total;
                    _status.Text = $"Importing {value.Completed:N0}/{value.Total:N0}: {value.FileName}";
                })), token)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { error = ex; }
        GtkDispatcher.Post(() =>
        {
            _importCancel?.Dispose();
            _importCancel = null;
            if (_closing) { CloseApp(); return; }
            _importButton.Sensitive = _browserImports == 0;
            _folderButton.Sensitive = _browserImports == 0;
            _progress.Hide();
            _cancelImport.Hide();
            RefreshAll();
            if (error is not null) { Ui.Error(this, "Import failed", error.Message); return; }
            if (report is null) { _status.Text = "Import cancelled."; return; }
            _status.Text = $"{report.Imported:N0} imported · {report.Duplicates:N0} duplicates skipped · {report.Failures.Count:N0} failed" +
                (report.Cancelled ? " · Import cancelled" : "");
            if (report.Failures.Count > 0)
                Ui.Error(this, "Some images could not be imported", string.Join('\n', report.Failures.Take(10)
                    .Select(failure => failure.FileName + ": " + failure.Error)) +
                    (report.Failures.Count > 10 ? $"\n… and {report.Failures.Count - 10} more." : ""));
        });
    }

    private sealed class ImportUiProgress(Action<ImportProgress> report) : IProgress<ImportProgress>
    { public void Report(ImportProgress value) => report(value); }

    private Task<BrowserReply> HandleBrowserRequest(BrowserRequest request)
    {
        var completion = new TaskCompletionSource<BrowserReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        GtkDispatcher.Post(() => _ = ImportFromBrowser(request, completion));
        return completion.Task;
    }

    private async Task ImportFromBrowser(BrowserRequest request, TaskCompletionSource<BrowserReply> completion)
    {
        if (_closing) { completion.SetResult(BrowserReply.Failure("Pawprint Tagger is closing. Try again after it closes.")); return; }
        try { BrowserHost.Validate(request); }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
        { completion.SetResult(BrowserReply.Failure(ex.Message)); return; }
        if (request.Action == "ping")
        { completion.SetResult(new(true, "ready", LibraryFolder: _library.Folder)); return; }
        _browserImports++;
        _importButton.Sensitive = false;
        _folderButton.Sensitive = false;
        _status.Text = "Importing original image and tags from " + request.Post!.Site + "…";
        var library = _library;
        BrowserReply reply;
        try { reply = await Task.Run(() => BrowserHost.ImportAsync(library, request)).ConfigureAwait(false); }
        catch (Exception ex) { reply = BrowserReply.Failure(ex.Message); }
        GtkDispatcher.Post(() =>
        {
            _browserImports--;
            completion.SetResult(reply);
            if (_closing)
            {
                if (_browserImports == 0 && _importCancel is null) CloseApp();
                return;
            }
            _importButton.Sensitive = _importCancel is null && _browserImports == 0;
            _folderButton.Sensitive = _importButton.Sensitive;
            if (reply.Ok)
            {
                RefreshAll();
                _status.Text = $"{(reply.Status == "imported" ? "Imported" : "Updated")} from {request.Post!.Site} · {reply.TagCount} booru tags";
            }
            else _status.Text = "Browser import failed: " + reply.Error;
        });
    }

    private void ChooseLibrary()
    {
        if (IsImportRunning || _closing) return;
        using var dialog = new FileChooserDialog("Open or create a library folder", this, FileChooserAction.SelectFolder,
            "Cancel", ResponseType.Cancel, "Use folder", ResponseType.Accept);
        dialog.SetCurrentFolder(_library.Folder);
        var note = Ui.Label("Each folder is a separate library. Existing libraries and original files stay where they are.");
        note.Margin = 8;
        dialog.ExtraWidget = note;
        note.Show();
        var accepted = (ResponseType)dialog.Run() == ResponseType.Accept;
        var folder = dialog.Filename;
        dialog.Destroy();
        if (!accepted || string.IsNullOrEmpty(folder) || IOPath.GetFullPath(folder) == _library.Folder) return;
        // A browser import can start while the modal folder chooser runs its GTK loop.
        if (IsImportRunning || _closing)
        {
            _status.Text = "Finish the current import before switching libraries.";
            return;
        }
        LibraryStore? next = null;
        try
        {
            next = new(folder);
            if (_settingsPath is not null) new AppSettings(next.Folder).Save(_settingsPath);
            _thumbnailsCancel.Cancel();
            _previewCancel?.Cancel();
            _viewerCancel?.Cancel();
            _library.Dispose();
            _library = next;
            next = null;
            _selectedImage = null;
            _selectedNode = null;
            _search.Text = "";
            _status.Text = "Library: " + _library.Folder;
            _status.TooltipText = _library.Folder;
            RefreshAll();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        { Ui.Error(this, "Could not open library", ex.Message); }
        finally { next?.Dispose(); }
    }

    private void ShowSearchHelp()
    {
        using var dialog = new MessageDialog(this, DialogFlags.Modal | DialogFlags.DestroyWithParent,
            MessageType.Info, ButtonsType.Close, false, "Search your library")
        {
            SecondaryText = "Words match filenames and tag paths. Every term must match. Use quotation marks for spaces, " +
                "underscores in tag names, or a minus sign to exclude a term. Selecting a category includes all its tags.\n\n" +
                "tag:\"Blonde hair\"\nAppearance/Hair/Blonde_hair\nfile:holiday -tag:portrait\next:png width:>=1920\nis:untagged\n\n" +
                "Fields: tag, file, name, ext, width, height, size (bytes), is:untagged.\n\n" +
                "Ctrl+F: search · Ctrl+O: import\nIn image view: Escape: Back · Left/Right: previous/next"
        };
        dialog.Run();
        dialog.Destroy();
    }

    private static string FormatSize(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024):0.##} MiB"
        : bytes >= 1024 ? $"{bytes / 1024.0:0.##} KiB" : $"{bytes} bytes";

    internal void CloseApp()
    {
        _closing = true;
        if (_browserImports > 0 || _importCancel is not null)
        {
            _importCancel?.Cancel();
            _status.Text = "Finishing the current import before closing…";
            return;
        }
        _browserServer?.Dispose();
        _searchTimer?.Dispose();
        _viewerTimer?.Dispose();
        _thumbnailsCancel.Cancel();
        _previewCancel?.Cancel();
        _viewerCancel?.Cancel();
        _imageModel.Clear();
        _placeholder.Dispose();
        _library.Dispose();
        Destroy();
        Application.Quit();
    }
}
