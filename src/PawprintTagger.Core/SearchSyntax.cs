using System.Text;

namespace PawprintTagger.Core;

internal static class SearchSyntax
{
    internal readonly record struct Token(int Start, int End, string Value, bool Closed);

    // Preserve source spans for completion while decoding the same syntax used by image search.
    internal static IEnumerable<Token> Scan(string query)
    {
        var value = new StringBuilder();
        var start = -1;
        var quoted = false;
        for (var index = 0; index < query.Length; index++)
        {
            var character = query[index];
            if (char.IsWhiteSpace(character) && !quoted)
            {
                if (start >= 0) yield return new(start, index, value.ToString(), true);
                value.Clear();
                start = -1;
                continue;
            }
            if (start < 0) start = index;
            if (character == '\\' && index + 1 < query.Length && query[index + 1] is '"' or '\\')
                value.Append(query[++index]);
            else if (character == '"') quoted = !quoted;
            else value.Append(character);
        }
        if (start >= 0) yield return new(start, query.Length, value.ToString(), !quoted);
    }

    internal static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
