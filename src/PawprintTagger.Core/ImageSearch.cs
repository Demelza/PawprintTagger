using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PawprintTagger.Core;

/// <summary>AND search with quoted phrases, exclusions, tag paths and numeric metadata.</summary>
public sealed class ImageSearch
{
    private readonly List<Func<LibraryImage, bool>> _predicates = new();
    public ImageSearch(string query, LibrarySnapshot snapshot, Guid? selectedNode = null)
    {
        var tags = snapshot.Tags.ToDictionary(tag => tag.Id, tag => Normalize(snapshot.TagPath(tag.Id)));
        if (selectedNode is { } selected)
        {
            var ids = snapshot.Descendants(selected);
            _predicates.Add(image => image.TagIds.Any(ids.Contains));
        }
        foreach (var token in Tokenize(query))
        {
            var excluded = token.StartsWith('-') && token.Length > 1;
            var value = excluded ? token[1..] : token;
            var separator = value.IndexOf(':');
            var field = separator > 0 ? value[..separator].ToLowerInvariant() : "";
            var term = Normalize(separator > 0 ? value[(separator + 1)..] : value);
            if (term.Length == 0) throw new ArgumentException($"Enter a value after {field}:.");
            Func<LibraryImage, bool> predicate = field switch
            {
                "tag" => image => image.TagIds.Any(id => tags.TryGetValue(id, out var path) && path.Contains(term)),
                "file" or "name" => image => Normalize(image.OriginalName).Contains(term),
                "ext" => image => Normalize(Path.GetExtension(image.OriginalName).TrimStart('.')) == term.TrimStart('.'),
                "is" when term == "untagged" => image => image.TagIds.Count == 0,
                "is" => throw new ArgumentException("Use is:untagged to find images without tags."),
                "width" => Numeric(value[(separator + 1)..], image => image.Width),
                "height" => Numeric(value[(separator + 1)..], image => image.Height),
                "size" => Numeric(value[(separator + 1)..], image => image.FileSize),
                "" => image => Normalize(image.OriginalName).Contains(term) ||
                    image.TagIds.Any(id => tags.TryGetValue(id, out var path) && path.Contains(term)),
                _ => throw new ArgumentException($"Unknown search field '{field}'. Use tag, file, ext, width, height, size or is.")
            };
            _predicates.Add(excluded ? image => !predicate(image) : predicate);
        }
    }

    public bool Matches(LibraryImage image) => _predicates.All(predicate => predicate(image));

    private static Func<LibraryImage, bool> Numeric(string value, Func<LibraryImage, long> getValue)
    {
        var match = Regex.Match(value, "^(>=|<=|>|<|=)?([0-9]+)$");
        if (!match.Success || !long.TryParse(match.Groups[2].Value, CultureInfo.InvariantCulture, out var number))
            throw new ArgumentException("Use a whole number, optionally with >, >=, <, <= or =. File sizes are in bytes.");
        return match.Groups[1].Value switch
        {
            ">" => image => getValue(image) > number,
            ">=" => image => getValue(image) >= number,
            "<" => image => getValue(image) < number,
            "<=" => image => getValue(image) <= number,
            _ => image => getValue(image) == number
        };
    }

    private static string Normalize(string value) => value.Replace('_', ' ').ToLowerInvariant();

    private static IEnumerable<string> Tokenize(string query)
    {
        var token = new StringBuilder();
        var quoted = false;
        foreach (var character in query)
        {
            if (character == '"') quoted = !quoted;
            else if (char.IsWhiteSpace(character) && !quoted)
            {
                if (token.Length == 0) continue;
                yield return token.ToString();
                token.Clear();
            }
            else token.Append(character);
        }
        if (quoted) throw new ArgumentException("Close the quotation mark in your search.");
        if (token.Length > 0) yield return token.ToString();
    }
}
