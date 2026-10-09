using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace PawprintTagger.Core;

public sealed record BooruTag(string Name, string Category);

public sealed record BooruSource(string Site, string PostId, string PageUrl, string OriginalUrl)
{
    public void Validate()
    {
        if (Site is not ("danbooru" or "gelbooru") || PostId is null || !Regex.IsMatch(PostId, "^[1-9][0-9]{0,15}$"))
            throw new InvalidDataException("Unsupported booru or invalid post id.");
        var page = HttpsUrl(PageUrl);
        var original = HttpsUrl(OriginalUrl);
        if (Site == "danbooru")
        {
            if (page.Host is not ("danbooru.donmai.us" or "safebooru.donmai.us") ||
                page.AbsolutePath.TrimEnd('/') != "/posts/" + PostId || !IsDomain(original.Host, "donmai.us"))
                throw new InvalidDataException("The Danbooru post or original image URL is invalid.");
        }
        else
        {
            var query = page.Query.TrimStart('?').Split('&').Select(part => part.Split('=', 2))
                .Where(part => part.Length == 2).ToDictionary(part => part[0], part => part[1]);
            if (page.Host is not ("gelbooru.com" or "www.gelbooru.com") || page.AbsolutePath != "/index.php" ||
                query.GetValueOrDefault("page") != "post" || query.GetValueOrDefault("s") != "view" ||
                query.GetValueOrDefault("id") != PostId || !IsDomain(original.Host, "gelbooru.com"))
                throw new InvalidDataException("The Gelbooru post or original image URL is invalid.");
        }
        if (Regex.IsMatch(Uri.UnescapeDataString(original.AbsolutePath), "(?:^|/)(?:samples?|previews?|thumbnails?)(?:/|[-_])", RegexOptions.IgnoreCase))
            throw new InvalidDataException("A sample or thumbnail URL cannot be imported as the original image.");
    }

    private static bool IsDomain(string host, string domain) => host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);
    private static Uri HttpsUrl(string value)
    {
        if (value is null || value.Length > 8192 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || uri.UserInfo.Length > 0 || !uri.IsDefaultPort)
            throw new InvalidDataException("Booru URLs must use HTTPS without credentials or a custom port.");
        return uri;
    }
}

public sealed record BooruPost(string Site, string PostId, string PageUrl, string OriginalUrl,
    IReadOnlyList<BooruTag> Tags, string? Md5 = null)
{
    [JsonIgnore]
    public BooruSource Source => new(Site, PostId, PageUrl, OriginalUrl);
    public void Validate()
    {
        Source.Validate();
        if (Tags is null || Tags.Count is 0 or > 2048 || Tags.Any(tag => tag is null ||
            string.IsNullOrWhiteSpace(tag.Name) || tag.Name.Length > 128 || tag.Name.Any(char.IsControl) ||
            tag.Category is not ("general" or "artist" or "character" or "copyright" or "meta")))
            throw new InvalidDataException("The post must have valid tags and tag categories.");
        if (Md5 is not null && !Regex.IsMatch(Md5, "^[a-fA-F0-9]{32}$"))
            throw new InvalidDataException("The original image's MD5 is invalid.");
    }
}

public sealed record TaggedImportResult(Guid ImageId, bool IsNewImage, int TagCount);
public sealed record BrowserRequest(int Version, string Action, string? FilePath = null, string? FileName = null, BooruPost? Post = null);
public sealed record BrowserReply(bool Ok, string Status, string? Error = null, Guid? ImageId = null,
    int TagCount = 0, string? LibraryFolder = null)
{
    public static BrowserReply Failure(string error) => new(false, "error", error);
}
