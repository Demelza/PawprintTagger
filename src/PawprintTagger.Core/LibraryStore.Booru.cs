using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace PawprintTagger.Core;

public sealed partial class LibraryStore
{
    /// <summary>Commit the owned image, booru groups, assignments and provenance together.</summary>
    public async Task<TaggedImportResult> ImportTaggedAsync(string path, string fileName, BooruPost post,
        Func<string, ImageDimensions> inspect, CancellationToken cancellationToken = default)
    {
        post.Validate();
        if (!Path.IsPathFullyQualified(path) || string.IsNullOrWhiteSpace(fileName) || fileName.Length > 240 ||
            fileName != Path.GetFileName(fileName) || fileName.Contains('\\') || fileName.Any(char.IsControl))
            throw new ArgumentException("The browser must supply a downloaded file and a plain image filename.");
        await _importGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var staged = Path.Combine(ImagesFolder, ".import-" + Guid.NewGuid().ToString("N"));
        string? ownedCopy = null;
        try
        {
            lock (_gate) ThrowIfDisposed();
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
            await using (var stream = File.OpenRead(staged))
                hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            if (post.Md5 is not null)
            {
                await using var stream = File.OpenRead(staged);
                var md5 = Convert.ToHexString(await MD5.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
                if (!md5.Equals(post.Md5, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The downloaded bytes do not match the booru's original image. The image and tags were not imported.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                ThrowIfDisposed();
                var existing = _snapshot.Images.SingleOrDefault(image => image.Sha256 == hash);
                var imageId = existing?.Id ?? Guid.NewGuid();
                var tags = _snapshot.Tags.ToList();
                var assigned = new HashSet<Guid>(existing?.TagIds ?? Array.Empty<Guid>());
                var importedTags = new HashSet<Guid>();
                Guid Group(string name, Guid? parent, string? color = null)
                {
                    var group = tags.SingleOrDefault(tag => tag.ParentId == parent &&
                        string.Equals(tag.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (group is not null)
                    {
                        if (!group.IsCategory) throw new InvalidDataException($"The library's '{name}' tag occupies a required booru group. Rename it or move it into a group first.");
                        return group.Id;
                    }
                    group = new(Guid.NewGuid(), name, parent, true, color);
                    tags.Add(group);
                    return group.Id;
                }
                var categories = new Dictionary<string, Guid>();
                foreach (var imported in post.Tags.DistinctBy(tag => (tag.Name, tag.Category)))
                {
                    // Reuse a uniquely named local tag, even after it has been moved to a custom group.
                    var normalized = imported.Name.Replace('_', ' ').Trim();
                    var matches = tags.Where(tag => !tag.IsCategory &&
                        string.Equals(tag.Name.Replace('_', ' ').Trim(), normalized, StringComparison.OrdinalIgnoreCase)).ToArray();
                    TagNode tag;
                    if (matches.Length == 1) tag = matches[0];
                    else
                    {
                        if (!categories.TryGetValue(imported.Category, out var parent))
                        {
                            var root = Group("Booru", null);
                            var site = Group(post.Site == "danbooru" ? "Danbooru" : "Gelbooru", root);
                            var (name, color) = imported.Category switch
                            {
                                "artist" => ("Artist", "#B26736"),
                                "character" => ("Character", "#478F55"),
                                "copyright" => ("Copyright", "#9975BF"),
                                "meta" => ("Meta", "#888888"),
                                _ => ("General", (string?)null)
                            };
                            parent = Group(name, site, color);
                            categories.Add(imported.Category, parent);
                        }
                        tag = tags.SingleOrDefault(tag => tag.ParentId == parent && !tag.IsCategory &&
                            string.Equals(tag.Name, imported.Name, StringComparison.OrdinalIgnoreCase))
                            ?? new TagNode(Guid.NewGuid(), imported.Name.Trim(), parent, false);
                        if (!tags.Any(node => node.Id == tag.Id)) tags.Add(tag);
                    }
                    assigned.Add(tag.Id);
                    importedTags.Add(tag.Id);
                }
                LibraryImage image;
                if (existing is not null)
                    image = existing with { TagIds = assigned.ToArray(), Sources = existing.Sources.Append(post.Source).Distinct().ToArray() };
                else
                {
                    var extension = Path.GetExtension(fileName).ToLowerInvariant();
                    if (!Regex.IsMatch(extension, "^\\.[a-z0-9]{1,12}$")) extension = ".image";
                    image = new(imageId, fileName, imageId.ToString("N") + extension, hash,
                        dimensions.Width, dimensions.Height, new FileInfo(staged).Length, DateTimeOffset.UtcNow, assigned.ToArray())
                        { Sources = new[] { post.Source } };
                    ownedCopy = ImagePath(image);
                    File.Move(staged, ownedCopy);
                }
                Commit(_snapshot with { Tags = tags.ToArray(),
                    Images = _snapshot.Images.Where(candidate => candidate.Id != imageId).Append(image).ToArray() });
                ownedCopy = null;
                return new(imageId, existing is null, importedTags.Count);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(staged)) File.Delete(staged);
                if (ownedCopy is not null && File.Exists(ownedCopy)) File.Delete(ownedCopy);
            }
            finally { _importGate.Release(); }
        }
    }
}
