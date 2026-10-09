using PawprintTagger.Core;

namespace PawprintTagger.Gtk;

internal static class BrowserHost
{
    internal const string HostName = "org.pawprint_tagger.bridge";
    internal const string ExtensionId = "pawprint-tagger@pawprint.local";

    internal static async Task<int> RunAsync(Stream input, Stream output, string settingsPath)
    {
        BrowserReply reply;
        try
        {
            var request = await BrowserMessageCodec.ReadAsync<BrowserRequest>(input).ConfigureAwait(false);
            if (request is null) return 0;
            reply = await HandleAsync(request, settingsPath).ConfigureAwait(false);
        }
        catch (Exception ex) { reply = BrowserReply.Failure(ex.Message); }
        await BrowserMessageCodec.WriteAsync(output, reply).ConfigureAwait(false);
        return reply.Ok ? 0 : 1;
    }

    internal static void Validate(BrowserRequest request)
    {
        if (request.Version != 1 || request.Action is not ("ping" or "import"))
            throw new InvalidDataException("Unsupported browser bridge protocol or action. Update Pawprint Tagger and its extension together.");
        if (request.Action == "import")
        {
            if (request.FilePath is null || request.FilePath.Length > 8192 || !Path.IsPathFullyQualified(request.FilePath) ||
                request.FileName is null || request.FileName.Length is 0 or > 240 ||
                request.FileName != Path.GetFileName(request.FileName) || request.FileName.Contains('\\') ||
                request.FileName.Any(char.IsControl) || request.Post is null)
                throw new InvalidDataException("The browser did not supply a completed image download and its post metadata.");
            request.Post.Validate();
        }
    }

    internal static async Task<BrowserReply> HandleAsync(BrowserRequest request, string settingsPath, string? pipeName = null)
    {
        Validate(request);
        var active = await BrowserImportServer.TrySendAsync(pipeName ?? BrowserImportServer.PipeName(settingsPath), request)
            .ConfigureAwait(false);
        if (active is not null) return active;
        var settings = AppSettings.Load(settingsPath);
        if (request.Action == "ping") return new(true, "ready", LibraryFolder: settings.LibraryFolder);
        using var library = new LibraryStore(settings.LibraryFolder);
        return await ImportAsync(library, request).ConfigureAwait(false);
    }

    internal static async Task<BrowserReply> ImportAsync(LibraryStore library, BrowserRequest request)
    {
        Validate(request);
        if (request.Action == "ping") return new(true, "ready", LibraryFolder: library.Folder);
        var result = await library.ImportTaggedAsync(request.FilePath!, request.FileName!, request.Post!, ImageLoader.Inspect)
            .ConfigureAwait(false);
        return new(true, result.IsNewImage ? "imported" : "updated", ImageId: result.ImageId,
            TagCount: result.TagCount, LibraryFolder: library.Folder);
    }
}
