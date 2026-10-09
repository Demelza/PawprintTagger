using System.Security.Cryptography;
using System.Text.Json;
using PawprintTagger.Core;
using PawprintTagger.Gtk;

internal static class BrowserChecks
{
    internal static BooruPost Post(string site, string id, IReadOnlyList<BooruTag> tags, string? md5 = null) => new(
        site, id, site == "danbooru" ? $"https://danbooru.donmai.us/posts/{id}"
            : $"https://gelbooru.com/index.php?page=post&s=view&id={id}",
        site == "danbooru" ? "https://cdn.donmai.us/original/image.png" : "https://img4.gelbooru.com/images/image.png", tags, md5);

    internal static async Task TaggedImports()
    {
        using var fixture = new Fixture();
        var source = fixture.Source("browser-original.png", "original-bytes");
        var md5 = Convert.ToHexString(MD5.HashData(File.ReadAllBytes(source)));
        Guid imageId, localTagId;
        using (var library = new LibraryStore(fixture.LibraryFolder))
        {
            var local = library.SaveTag(null, "Blonde hair", null, false, "#ABCDEF");
            localTagId = local.Id;
            var post = Post("danbooru", "123", new[]
            {
                new BooruTag("blonde_hair", "general"), new BooruTag("example_artist", "artist"),
                new BooruTag("fate/stay_night", "copyright"), new BooruTag("初音ミク", "character"),
                new BooruTag("highres", "meta")
            }, md5);
            var imported = await library.ImportTaggedAsync(source, "danbooru-123.png", post, _ => new(1920, 1080));
            imageId = imported.ImageId;
            Check.True(imported.IsNewImage, "A new image was not reported.");
            Check.Equal(5, imported.TagCount);
            var image = library.Snapshot.Images.Single();
            Check.True(image.TagIds.Contains(local.Id), "An existing local tag was not reused.");
            Check.Equal("#ABCDEF", library.Snapshot.EffectiveColor(local.Id));
            var slash = library.Snapshot.Tags.Single(tag => tag.Name == "fate/stay_night");
            Check.Equal("Booru/Danbooru/Copyright/fate/stay_night", library.Snapshot.TagPath(slash.Id));
            Check.Equal(post.Source, image.Sources.Single());
            Check.Equal("original-bytes", File.ReadAllText(library.ImagePath(image)));
            Check.Equal("original-bytes", File.ReadAllText(source));
            var reimport = await library.ImportTaggedAsync(source, "danbooru-123.png", post, _ => new(1920, 1080));
            Check.True(!reimport.IsNewImage, "Duplicate bytes created another image.");
            Check.Equal(imageId, reimport.ImageId);
            Check.Equal(1, library.Snapshot.Images.Count);
            Check.Equal(1, library.Snapshot.Images.Single().Sources.Count);
            Check.Equal(1, Directory.GetFiles(library.ImagesFolder).Length);
            var second = Post("gelbooru", "456", new[] { new BooruTag("blonde_hair", "general"), new BooruTag("blue_eyes", "general") });
            await library.ImportTaggedAsync(source, "gelbooru-456.png", second, _ => new(1920, 1080));
            Check.Equal(1, library.Snapshot.Images.Count);
            Check.Equal(2, library.Snapshot.Images.Single().Sources.Count);
            Check.Equal(6, library.Snapshot.Images.Single().TagIds.Count);
        }
        using var reopened = new LibraryStore(fixture.LibraryFolder);
        Check.Equal(imageId, reopened.Snapshot.Images.Single().Id);
        Check.Equal(2, reopened.Snapshot.Images.Single().Sources.Count);
        Check.True(reopened.Snapshot.Images.Single().TagIds.Contains(localTagId), "Local assignments were lost after reopening.");
        var moved = reopened.Snapshot.Tags.Single(tag => tag.Name == "fate/stay_night");
        reopened.SaveTag(moved.Id, moved.Name, null, false, null);
        Check.Equal("fate/stay_night", reopened.Snapshot.TagPath(moved.Id));
    }

    internal static async Task Failures()
    {
        using var fixture = new Fixture();
        using var library = new LibraryStore(fixture.LibraryFolder);
        var source = fixture.Source("original.png", "original-bytes");
        var tags = new[] { new BooruTag("blonde_hair", "general") };
        var post = Post("danbooru", "123", tags, new string('0', 32));
        await ThrowsAsync<InvalidDataException>(() => library.ImportTaggedAsync(source, "original.png", post, _ => new(100, 200)));
        Check.Equal(0, library.Snapshot.Images.Count);
        Check.Equal(0, library.Snapshot.Tags.Count);
        Check.Equal(0, Directory.GetFiles(library.ImagesFolder).Length);
        foreach (var originalUrl in new[] { "file:///etc/passwd", "https://example.com/original.png",
            "https://cdn.donmai.us/sample/a/sample-image.jpg", "https://cdn.donmai.us/%73ample/a.jpg" })
            Check.Throws<InvalidDataException>(() => (post with { OriginalUrl = originalUrl }).Validate());
        Check.Throws<InvalidDataException>(() => (post with { PageUrl = "https://danbooru.donmai.us/posts/999" }).Validate());
        Check.Throws<InvalidDataException>(() => (post with { Tags = Array.Empty<BooruTag>() }).Validate());
        var backup = library.CatalogPath + ".backup";
        var saved = File.ReadAllText(library.CatalogPath);
        File.Move(library.CatalogPath, backup);
        Directory.CreateDirectory(library.CatalogPath);
        try
        {
            await ThrowsAsync<IOException>(() => library.ImportTaggedAsync(source, "original.png", post with { Md5 = null }, _ => new(100, 200)));
            Check.Equal(0, library.Snapshot.Tags.Count);
            Check.Equal(0, library.Snapshot.Images.Count);
            Check.Equal(0, Directory.GetFiles(library.ImagesFolder).Length);
            Check.Equal(saved, File.ReadAllText(backup));
        }
        finally { Directory.Delete(library.CatalogPath); File.Move(backup, library.CatalogPath); }
    }

    internal static async Task Messages()
    {
        var request = new BrowserRequest(1, "import", "/tmp/image.png", "image.png", Post("danbooru", "123",
            new[] { new BooruTag("初音ミク", "character") }));
        using var encoded = new MemoryStream();
        await BrowserMessageCodec.WriteAsync(encoded, request);
        var data = encoded.ToArray();
        Check.Equal((uint)(data.Length - 4), BitConverter.ToUInt32(data));
        using var fragmented = new FragmentedStream(data);
        var decoded = await BrowserMessageCodec.ReadAsync<BrowserRequest>(fragmented);
        Check.Equal("初音ミク", decoded!.Post!.Tags.Single().Name);
        using var empty = new MemoryStream();
        Check.True(await BrowserMessageCodec.ReadAsync<BrowserRequest>(empty) is null, "EOF did not close the native host.");
        using var oversized = new MemoryStream(BitConverter.GetBytes((uint)BrowserMessageCodec.MaximumMessageBytes + 1));
        await ThrowsAsync<InvalidDataException>(() => BrowserMessageCodec.ReadAsync<BrowserRequest>(oversized));
        using var truncated = new MemoryStream(data[..^1]);
        await ThrowsAsync<EndOfStreamException>(() => BrowserMessageCodec.ReadAsync<BrowserRequest>(truncated));
        Check.Throws<InvalidDataException>(() => BrowserHost.Validate(request with { Action = "execute" }));
        Check.Throws<InvalidDataException>(() => BrowserHost.Validate(request with { Version = 2 }));
    }

    internal static async Task Pipe()
    {
        using var fixture = new Fixture();
        using var library = new LibraryStore(fixture.LibraryFolder);
        var settingsPath = fixture.PathFor("config/settings.json");
        var pipeName = BrowserImportServer.PipeName(settingsPath);
        var seen = 0;
        using var server = new BrowserImportServer(pipeName, async request =>
        {
            Interlocked.Increment(ref seen);
            if (request.Action == "ping") return new(true, "ready", LibraryFolder: library.Folder);
            var imported = await library.ImportTaggedAsync(request.FilePath!, request.FileName!, request.Post!, _ => new(500, 300));
            return new(true, imported.IsNewImage ? "imported" : "updated", ImageId: imported.ImageId,
                TagCount: imported.TagCount, LibraryFolder: library.Folder);
        });
        var source = fixture.Source("original.png", "same-bytes");
        var request = new BrowserRequest(1, "import", source, "danbooru-123.png", Post("danbooru", "123",
            new[] { new BooruTag("blonde_hair", "general") }));
        var first = await BrowserHost.HandleAsync(request, settingsPath);
        var second = await BrowserHost.HandleAsync(request, settingsPath);
        Check.Equal(library.Folder, first.LibraryFolder);
        Check.Equal(first.ImageId, second.ImageId);
        Check.Equal("updated", second.Status);
        Check.Equal(2, seen);
        Check.Equal(1, library.Snapshot.Images.Count);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var replies = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => BrowserImportServer.TrySendAsync(pipeName, new(1, "ping"), timeout.Token)));
        Check.True(replies.All(reply => reply?.Ok == true), "Concurrent pipe requests lost their replies.");
    }

    internal static async Task ClosedDesktop()
    {
        using var fixture = new Fixture();
        var settingsPath = fixture.PathFor("config/settings.json");
        new AppSettings(fixture.LibraryFolder).Save(settingsPath);
        Directory.CreateDirectory(fixture.PathFor("downloads"));
        var file = fixture.PathFor("downloads/original.png");
        using (var png = new Gdk.Pixbuf(Gdk.Colorspace.Rgb, true, 8, 200, 100))
        { png.Fill(0x336699FF); png.Save(file, "png"); }
        var request = new BrowserRequest(1, "import", file, "danbooru-123.png", Post("danbooru", "123",
            new[] { new BooruTag("blue_background", "general") }, Convert.ToHexString(MD5.HashData(File.ReadAllBytes(file)))));
        using var input = new MemoryStream();
        using var output = new MemoryStream();
        await BrowserMessageCodec.WriteAsync(input, request);
        input.Position = 0;
        Check.Equal(0, await BrowserHost.RunAsync(input, output, settingsPath));
        output.Position = 0;
        var reply = await BrowserMessageCodec.ReadAsync<BrowserReply>(output);
        Check.True(reply!.Ok, "Native import failed: " + reply.Error);
        using (var library = new LibraryStore(fixture.LibraryFolder))
        {
            var image = library.Snapshot.Images.Single();
            Check.Equal(200, image.Width);
            Check.Equal(100, image.Height);
            Check.True(File.ReadAllBytes(file).SequenceEqual(File.ReadAllBytes(library.ImagePath(image))), "The original download was modified.");
        }
        // Bad input still receives a properly framed failure reply, never plain console output.
        using var malformed = new MemoryStream(new byte[] { 3, 0, 0, 0, (byte)'b', (byte)'a', (byte)'d' });
        using var failure = new MemoryStream();
        Check.Equal(1, await BrowserHost.RunAsync(malformed, failure, settingsPath));
        failure.Position = 0;
        Check.True((await BrowserMessageCodec.ReadAsync<BrowserReply>(failure))!.Ok == false, "Malformed JSON was accepted.");
    }

    private static async Task ThrowsAsync<T>(Func<Task> operation) where T : Exception
    {
        try { await operation(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }
}
