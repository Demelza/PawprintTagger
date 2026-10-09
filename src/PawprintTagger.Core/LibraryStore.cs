using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PawprintTagger.Core;

/// <summary>A portable catalog and owned image copies, guarded against concurrent writers.</summary>
public sealed class LibraryStore : IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _importGate = new(1, 1);
    private readonly FileStream _lock;
    private LibrarySnapshot _snapshot;
    private bool _disposed;
    public string Folder { get; }
    public string ImagesFolder => Path.Combine(Folder, "images");
    public string ThumbnailsFolder => Path.Combine(Folder, "thumbnails");
    public string CatalogPath => Path.Combine(Folder, "library.json");
    public LibrarySnapshot Snapshot { get { lock (_gate) return _snapshot; } }

    public LibraryStore(string folder)
    {
        Folder = Path.GetFullPath(folder);
        Directory.CreateDirectory(Folder);
        try
        {
            _lock = new FileStream(Path.Combine(Folder, ".pawprint.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new IOException("This library is already open or its lock file is unavailable. Close its other window and try again.", ex);
        }
        try
        {
            _snapshot = File.Exists(CatalogPath)
                ? JsonSerializer.Deserialize<LibrarySnapshot>(File.ReadAllText(CatalogPath), AtomicJson.Options)
                    ?? throw new InvalidDataException("The library catalog is empty.")
                : LibrarySnapshot.Empty;
            ValidateCatalog(_snapshot);
            _snapshot = Freeze(_snapshot);
            Directory.CreateDirectory(ImagesFolder);
            Directory.CreateDirectory(ThumbnailsFolder);
            if (!File.Exists(CatalogPath)) AtomicJson.Write(CatalogPath, _snapshot);
        }
        catch { _lock.Dispose(); throw; }
    }

    public string ImagePath(LibraryImage image)
    {
        ValidateStoredName(image.StoredName);
        return Path.Combine(ImagesFolder, image.StoredName);
    }

    public string ThumbnailPath(LibraryImage image) => Path.Combine(ThumbnailsFolder, image.Id.ToString("N") + ".png");

    public TagNode SaveTag(Guid? id, string name, Guid? parentId, bool isCategory, string? color)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            name = name.Trim();
            if (name.Length is 0 or > 128 || name.Contains('/') || name.Any(char.IsControl))
                throw new ArgumentException("Use a name of 1–128 characters, without slashes or control characters.");
            if (color is not null && !Regex.IsMatch(color, "^#[0-9a-fA-F]{6}$"))
                throw new ArgumentException("Choose a valid color label.");
            if (parentId is { } parent && !_snapshot.Tags.Any(tag => tag.Id == parent && tag.IsCategory))
                throw new ArgumentException("The parent must be an existing category.");
            var existing = id is { } value ? _snapshot.Tags.SingleOrDefault(tag => tag.Id == value)
                ?? throw new ArgumentException("This tag no longer exists.") : null;
            if (existing is not null && existing.IsCategory != isCategory)
                throw new ArgumentException("A tag's kind cannot be changed.");
            if (id is { } tagId && parentId is { } target && _snapshot.Descendants(tagId).Contains(target))
                throw new ArgumentException("A category cannot be moved inside itself or its children.");
            if (_snapshot.Tags.Any(tag => tag.Id != id && tag.ParentId == parentId &&
                string.Equals(tag.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("That name already exists in this category.");
            var result = new TagNode(id ?? Guid.NewGuid(), name, parentId, isCategory, color?.ToUpperInvariant());
            Commit(_snapshot with { Tags = _snapshot.Tags.Where(tag => tag.Id != result.Id).Append(result).ToArray() });
            return result;
        }
    }

    public void DeleteTag(Guid id)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var ids = _snapshot.Descendants(id);
            Commit(_snapshot with
            {
                Tags = _snapshot.Tags.Where(tag => !ids.Contains(tag.Id)).ToArray(),
                Images = _snapshot.Images.Select(image => image with
                    { TagIds = image.TagIds.Where(tag => !ids.Contains(tag)).ToArray() }).ToArray()
            });
        }
    }

    public void SetImageTag(Guid imageId, Guid tagId, bool assigned)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_snapshot.Tags.Any(tag => tag.Id == tagId && !tag.IsCategory))
                throw new ArgumentException("Only tags can be assigned to images. Choose a tag inside the category.");
            if (!_snapshot.Images.Any(image => image.Id == imageId)) throw new ArgumentException("This image no longer exists.");
            Commit(_snapshot with { Images = _snapshot.Images.Select(image => image.Id != imageId ? image : image with
            {
                TagIds = assigned ? image.TagIds.Append(tagId).Distinct().ToArray()
                    : image.TagIds.Where(id => id != tagId).ToArray()
            }).ToArray() });
        }
    }

    public async Task<ImportReport> ImportAsync(IEnumerable<string> sources, Func<string, ImageDimensions> inspect,
        IProgress<ImportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        await _importGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var paths = sources.Distinct(StringComparer.Ordinal).ToArray();
            var imported = 0;
            var duplicates = 0;
            var cancelled = false;
            var failures = new List<ImportFailure>();
            for (var index = 0; index < paths.Length; index++)
            {
                var path = paths[index];
                var staged = Path.Combine(ImagesFolder, ".import-" + Guid.NewGuid().ToString("N"));
                string? ownedCopy = null;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    lock (_gate) ThrowIfDisposed();
                    // Decode the exact bytes copied, rather than a source that could change during import.
                    await using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
                    await using (var copy = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                    {
                        await source.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
                        await copy.FlushAsync(cancellationToken).ConfigureAwait(false);
                        copy.Flush(flushToDisk: true);
                    }
                    var dimensions = inspect(staged);
                    if (dimensions.Width <= 0 || dimensions.Height <= 0) throw new InvalidDataException("Invalid image dimensions.");
                    string hash;
                    await using (var copy = File.OpenRead(staged))
                        hash = Convert.ToHexString(await SHA256.HashDataAsync(copy, cancellationToken).ConfigureAwait(false));
                    cancellationToken.ThrowIfCancellationRequested();
                    lock (_gate)
                    {
                        ThrowIfDisposed();
                        if (_snapshot.Images.Any(image => image.Sha256 == hash)) duplicates++;
                        else
                        {
                            var id = Guid.NewGuid();
                            var extension = Path.GetExtension(path).ToLowerInvariant();
                            if (!Regex.IsMatch(extension, "^\\.[a-z0-9]{1,12}$")) extension = ".image";
                            var image = new LibraryImage(id, Path.GetFileName(path), id.ToString("N") + extension,
                                hash, dimensions.Width, dimensions.Height, new FileInfo(staged).Length,
                                DateTimeOffset.UtcNow, Array.Empty<Guid>());
                            ownedCopy = ImagePath(image);
                            File.Move(staged, ownedCopy);
                            Commit(_snapshot with { Images = _snapshot.Images.Append(image).ToArray() });
                            ownedCopy = null;
                            imported++;
                        }
                    }
                }
                catch (OperationCanceledException) { cancelled = true; break; }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
                {
                    failures.Add(new(Path.GetFileName(path), ex.Message));
                }
                finally
                {
                    if (File.Exists(staged)) File.Delete(staged);
                    if (ownedCopy is not null && File.Exists(ownedCopy)) File.Delete(ownedCopy);
                }
                progress?.Report(new(index + 1, paths.Length, Path.GetFileName(path)));
            }
            return new(imported, duplicates, failures.AsReadOnly(), cancelled);
        }
        finally { _importGate.Release(); }
    }

    private void Commit(LibrarySnapshot value)
    {
        var frozen = Freeze(value);
        AtomicJson.Write(CatalogPath, frozen);
        _snapshot = frozen;
    }

    private static LibrarySnapshot Freeze(LibrarySnapshot value) => value with
    {
        Tags = Array.AsReadOnly(value.Tags.ToArray()),
        Images = Array.AsReadOnly(value.Images.Select(image => image with
            { TagIds = Array.AsReadOnly(image.TagIds.ToArray()) }).ToArray())
    };

    private static void ValidateStoredName(string name)
    {
        if (string.IsNullOrEmpty(name) || !Regex.IsMatch(name, "^[0-9a-f]{32}\\.[a-z0-9]{1,12}$"))
            throw new InvalidDataException("The catalog contains an unsafe image filename.");
    }

    private static void ValidateCatalog(LibrarySnapshot value)
    {
        if (value.Version != 1 || value.Tags is null || value.Images is null)
            throw new InvalidDataException("This library format is unsupported or damaged.");
        if (value.Tags.Any(tag => tag is null) || value.Images.Any(image => image is null) ||
            value.Tags.Select(tag => tag.Id).Distinct().Count() != value.Tags.Count ||
            value.Images.Select(image => image.Id).Distinct().Count() != value.Images.Count)
            throw new InvalidDataException("The library contains invalid or duplicate records.");
        var tags = value.Tags.ToDictionary(tag => tag.Id);
        foreach (var tag in value.Tags)
        {
            if (tag.Id == Guid.Empty || string.IsNullOrWhiteSpace(tag.Name) || tag.Name.Contains('/') ||
                tag.Name.Any(char.IsControl) || tag.Name.Length > 128 ||
                tag.Color is not null && !Regex.IsMatch(tag.Color, "^#[0-9a-fA-F]{6}$") ||
                tag.ParentId is { } parent && (!tags.TryGetValue(parent, out var parentTag) || !parentTag.IsCategory))
                throw new InvalidDataException("The library contains an invalid tag or category.");
            var seen = new HashSet<Guid> { tag.Id };
            var ancestor = tag.ParentId;
            while (ancestor is { } id)
            {
                if (!seen.Add(id)) throw new InvalidDataException("The category hierarchy contains a cycle.");
                ancestor = tags[id].ParentId;
            }
        }
        if (value.Tags.GroupBy(tag => (tag.ParentId, tag.Name.ToUpperInvariant())).Any(group => group.Count() > 1))
            throw new InvalidDataException("The library contains duplicate tag names in a category.");
        foreach (var image in value.Images)
        {
            ValidateStoredName(image.StoredName);
            if (image.Id == Guid.Empty || string.IsNullOrEmpty(image.OriginalName) || image.Width <= 0 || image.Height <= 0 ||
                image.FileSize <= 0 || image.Sha256 is null || !Regex.IsMatch(image.Sha256, "^[0-9A-F]{64}$") ||
                image.TagIds is null || image.TagIds.Any(id => !tags.TryGetValue(id, out var tag) || tag.IsCategory))
                throw new InvalidDataException("The library contains invalid image metadata.");
        }
        if (value.Images.Select(image => image.StoredName).Distinct().Count() != value.Images.Count ||
            value.Images.Select(image => image.Sha256).Distinct().Count() != value.Images.Count)
            throw new InvalidDataException("The library contains duplicate image records.");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _lock.Dispose();
        }
    }
}
