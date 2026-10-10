namespace PawprintTagger.Core;

public sealed record TagSuggestion(Guid Id, string Name, string Path);
public readonly record struct CompletedSearch(string Text, int Cursor);

/// <summary>Complete the tag term at the cursor without replacing other search fields.</summary>
public sealed class TagSearchSuggestions
{
    private readonly record struct Candidate(TagSuggestion Tag, string Name, string Path);
    private readonly record struct Context(int Start, int End, string Fragment, bool Excluded);
    private readonly Candidate[] _candidates;
    public IReadOnlyList<TagSuggestion> Tags { get; }

    public TagSearchSuggestions(LibrarySnapshot snapshot)
    {
        var lookup = snapshot.Tags.ToDictionary(tag => tag.Id);
        var paths = new Dictionary<Guid, string>();
        string PathFor(TagNode tag)
        {
            if (paths.TryGetValue(tag.Id, out var path)) return path;
            path = tag.ParentId is { } parent ? PathFor(lookup[parent]) + "/" + tag.Name : tag.Name;
            paths.Add(tag.Id, path);
            return path;
        }
        _candidates = snapshot.Tags.Where(tag => !tag.IsCategory)
            .Select(tag => new TagSuggestion(tag.Id, tag.Name, PathFor(tag)))
            .OrderBy(tag => tag.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(tag => tag.Path, StringComparer.CurrentCultureIgnoreCase)
            .Select(tag => new Candidate(tag, Normalize(tag.Name), Normalize(tag.Path))).ToArray();
        Tags = Array.AsReadOnly(_candidates.Select(candidate => candidate.Tag).ToArray());
    }

    public IReadOnlyList<TagSuggestion> Find(string query, int cursor, int limit = 20)
    {
        if (GetContext(query, cursor) is not { } context || limit <= 0) return Array.Empty<TagSuggestion>();
        return _candidates.Where(candidate => candidate.Path.Contains(context.Fragment))
            .OrderBy(candidate => candidate.Name.StartsWith(context.Fragment) ? 0 : candidate.Path.StartsWith(context.Fragment) ? 1 : 2)
            .Take(limit).Select(candidate => candidate.Tag).ToArray();
    }

    public CompletedSearch Complete(string query, int cursor, TagSuggestion suggestion)
    {
        if (GetContext(query, cursor) is not { } context || !Tags.Any(tag => tag == suggestion))
            throw new ArgumentException("Choose a tag suggestion for the current search term.");
        var replacement = (context.Excluded ? "-tag:" : "tag:") + SearchSyntax.Quote(suggestion.Path);
        // At the end of a query, leave the cursor ready for another AND term.
        if (context.End == query.Length) replacement += " ";
        return new(query[..context.Start] + replacement + query[context.End..], context.Start + replacement.Length);
    }

    private static Context? GetContext(string query, int cursor)
    {
        cursor = Math.Clamp(cursor, 0, query.Length);
        foreach (var token in SearchSyntax.Scan(query))
        {
            if (cursor < token.Start || cursor > token.End) continue;
            var fragment = SearchSyntax.Scan(query[token.Start..cursor]).FirstOrDefault().Value ?? "";
            var excluded = fragment.StartsWith('-');
            if (excluded) fragment = fragment[1..];
            var colon = fragment.IndexOf(':');
            if (colon >= 0)
            {
                if (!fragment[..colon].Equals("tag", StringComparison.OrdinalIgnoreCase)) return null;
                fragment = fragment[(colon + 1)..];
            }
            else if (fragment.Length == 0) return null;
            return new(token.Start, token.End, Normalize(fragment), excluded);
        }
        return null;
    }

    private static string Normalize(string value) => value.Replace('_', ' ').ToLowerInvariant();
}
