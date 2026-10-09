namespace PawprintTagger.Core;

public sealed record TagNode(Guid Id, string Name, Guid? ParentId, bool IsCategory, string? Color = null);

public sealed record LibraryImage(Guid Id, string OriginalName, string StoredName, string Sha256,
    int Width, int Height, long FileSize, DateTimeOffset ImportedAt, IReadOnlyList<Guid> TagIds);

public sealed record LibrarySnapshot(int Version, IReadOnlyList<TagNode> Tags, IReadOnlyList<LibraryImage> Images)
{
    public static LibrarySnapshot Empty { get; } = new(1, Array.Empty<TagNode>(), Array.Empty<LibraryImage>());

    public string TagPath(Guid id)
    {
        var names = new List<string>();
        var lookup = Tags.ToDictionary(tag => tag.Id);
        var seen = new HashSet<Guid>();
        while (lookup.TryGetValue(id, out var tag) && seen.Add(id))
        {
            names.Add(tag.Name);
            if (tag.ParentId is not { } parent) break;
            id = parent;
        }
        names.Reverse();
        return string.Join('/', names);
    }

    public HashSet<Guid> Descendants(Guid id)
    {
        var result = new HashSet<Guid> { id };
        var pending = new Queue<Guid>();
        pending.Enqueue(id);
        while (pending.TryDequeue(out var parent))
            foreach (var child in Tags.Where(tag => tag.ParentId == parent))
                if (result.Add(child.Id)) pending.Enqueue(child.Id);
        return result;
    }

    public string? EffectiveColor(Guid id)
    {
        var lookup = Tags.ToDictionary(tag => tag.Id);
        var seen = new HashSet<Guid>();
        while (lookup.TryGetValue(id, out var tag) && seen.Add(id))
        {
            if (tag.Color is { } color) return color;
            if (tag.ParentId is not { } parent) break;
            id = parent;
        }
        return null;
    }
}

public readonly record struct ImageDimensions(int Width, int Height);
public sealed record ImportFailure(string FileName, string Error);
public sealed record ImportReport(int Imported, int Duplicates, IReadOnlyList<ImportFailure> Failures, bool Cancelled);
public readonly record struct ImportProgress(int Completed, int Total, string FileName);
