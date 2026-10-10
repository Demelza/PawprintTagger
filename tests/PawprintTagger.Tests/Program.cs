using System.Text.Json;
using PawprintTagger.Core;
using PawprintTagger.Gtk;

if (args.Contains("--gtk-smoke")) return await NativeChecks.Run();

var tests = new (string Name, Func<Task> Run)[]
{
    ("Imports copy originals, preserve names, deduplicate bytes and reject unreadable images", Imports),
    ("Tag folders, moves, inherited colors and image assignments survive reopening", TagsAndPersistence),
    ("Tag edits reject cycles, duplicate siblings and assignments of categories", InvalidTags),
    ("Search combines phrases, booru aliases, exclusions, paths and metadata", Search),
    ("Tag suggestions match names, booru aliases and paths while excluding other fields", SearchSuggestionChecks.Matching),
    ("Choosing a tag completes one query term and preserves exclusions and metadata", SearchSuggestionChecks.Completion),
    ("Tag completion preserves Unicode and escapes quotes and backslashes", SearchSuggestionChecks.UnicodeAndEscapes),
    ("Cancelled imports leave no staged files and keep already committed images", Cancellation),
    ("Failed catalog writes do not change the in-memory library", FailedWrites),
    ("Damaged catalogs are rejected without being overwritten", CorruptCatalogs),
    ("A library admits one writer and releases its lock on close", LibraryLock),
    ("Settings persist a configurable library path", Settings),
    ("Booru imports preserve original bytes, tag categories, slash tags and source provenance", BrowserChecks.TaggedImports),
    ("Booru imports verify original checksums and roll back failed catalog writes", BrowserChecks.Failures),
    ("Browser native messages handle Unicode, partial reads, malformed frames and replies", BrowserChecks.Messages),
    ("Browser pipe imports use the open library and serialize concurrent requests", BrowserChecks.Pipe),
    ("Native host imports with the desktop closed without requiring a GTK display", BrowserChecks.ClosedDesktop)
};
var failures = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL " + test.Name + "\n" + ex); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} checks passed.");
return failures == 0 ? 0 : 1;

static async Task Imports()
{
    using var fixture = new Fixture();
    var first = fixture.Source("one/portrait.png", "first-image");
    var sameName = fixture.Source("two/portrait.png", "second-image");
    var duplicate = fixture.Source("same-bytes.jpg", "first-image");
    var broken = fixture.Source("broken.png", "invalid-image");
    var original = File.ReadAllBytes(first);
    using var library = new LibraryStore(fixture.LibraryFolder);
    var result = await library.ImportAsync(new[] { first, sameName, duplicate, broken, fixture.PathFor("missing.png") }, path =>
    {
        if (File.ReadAllText(path).StartsWith("invalid")) throw new InvalidDataException("Not an image.");
        return new(1200, 800);
    });
    Check.Equal(2, result.Imported);
    Check.Equal(1, result.Duplicates);
    Check.Equal(2, result.Failures.Count);
    Check.True(File.ReadAllBytes(first).SequenceEqual(original), "Original bytes changed.");
    var images = library.Snapshot.Images;
    Check.True(images.All(image => image.OriginalName == "portrait.png"), "Original names were lost.");
    Check.True(images[0].StoredName != images[1].StoredName, "Equal filenames collided.");
    Check.Equal("first-image", File.ReadAllText(library.ImagePath(images[0])));
    Check.Equal("second-image", File.ReadAllText(library.ImagePath(images[1])));
    Check.Equal(2, Directory.GetFiles(library.ImagesFolder).Length);
    var again = await library.ImportAsync(new[] { library.ImagePath(images[0]) }, _ => new(1200, 800));
    Check.Equal(1, again.Duplicates);
}

static async Task TagsAndPersistence()
{
    using var fixture = new Fixture();
    var source = fixture.Source("photo.png", "photo");
    Guid imageId, appearanceId, hairId, blondeId;
    using (var library = new LibraryStore(fixture.LibraryFolder))
    {
        var appearance = library.SaveTag(null, "Appearance", null, true, "#FFAA00");
        var hair = library.SaveTag(null, "Hair", appearance.Id, true, null);
        var blonde = library.SaveTag(null, "Blonde hair", hair.Id, false, null);
        appearanceId = appearance.Id; hairId = hair.Id; blondeId = blonde.Id;
        Check.Equal("Appearance/Hair/Blonde hair", library.Snapshot.TagPath(blonde.Id));
        Check.Equal("#FFAA00", library.Snapshot.EffectiveColor(blonde.Id));
        await library.ImportAsync(new[] { source }, _ => new(800, 600));
        imageId = library.Snapshot.Images.Single().Id;
        library.SetImageTag(imageId, blonde.Id, true);
        library.SetImageTag(imageId, blonde.Id, true);
        Check.Equal(1, library.Snapshot.Images.Single().TagIds.Count);
        var search = new ImageSearch("", library.Snapshot, appearance.Id);
        Check.True(search.Matches(library.Snapshot.Images.Single()), "Category did not include descendants.");
    }
    using (var library = new LibraryStore(fixture.LibraryFolder))
    {
        Check.Equal(imageId, library.Snapshot.Images.Single().Id);
        Check.True(library.Snapshot.Images.Single().TagIds.Contains(blondeId), "Assignments did not persist.");
        library.SaveTag(blondeId, "Blond", appearanceId, false, "#123456");
        Check.Equal("Appearance/Blond", library.Snapshot.TagPath(blondeId));
        Check.Equal("#123456", library.Snapshot.EffectiveColor(blondeId));
        library.SetImageTag(imageId, blondeId, false);
        Check.Equal(0, library.Snapshot.Images.Single().TagIds.Count);
        library.SetImageTag(imageId, blondeId, true);
        library.SaveTag(blondeId, "Blond", hairId, false, null);
        library.DeleteTag(hairId);
        Check.Equal(1, library.Snapshot.Tags.Count);
        Check.Equal(0, library.Snapshot.Images.Single().TagIds.Count);
        Check.True(File.Exists(library.ImagePath(library.Snapshot.Images.Single())), "Deleting tags removed an image.");
    }
}

static async Task InvalidTags()
{
    using var fixture = new Fixture();
    using var library = new LibraryStore(fixture.LibraryFolder);
    var parent = library.SaveTag(null, "Parent", null, true, null);
    var child = library.SaveTag(null, "Child", parent.Id, true, null);
    var tag = library.SaveTag(null, "Tag", child.Id, false, null);
    Check.Throws<ArgumentException>(() => library.SaveTag(parent.Id, "Parent", child.Id, true, null));
    Check.Throws<ArgumentException>(() => library.SaveTag(child.Id, "Child", child.Id, true, null));
    Check.Throws<ArgumentException>(() => library.SaveTag(null, "tag", child.Id, false, null));
    Check.Throws<ArgumentException>(() => library.SaveTag(null, "Nested", tag.Id, false, null));
    Check.Throws<ArgumentException>(() => library.SaveTag(null, "Invalid/name", null, true, null));
    Check.Throws<ArgumentException>(() => library.SaveTag(null, "Invalid", null, false, "red"));
    Check.Throws<ArgumentException>(() => library.SaveTag(tag.Id, "Tag", null, true, null));
    await library.ImportAsync(new[] { fixture.Source("a.png", "a") }, _ => new(10, 20));
    Check.Throws<ArgumentException>(() => library.SetImageTag(library.Snapshot.Images.Single().Id, parent.Id, true));
    Check.Equal(3, library.Snapshot.Tags.Count);
}

static async Task Search()
{
    using var fixture = new Fixture();
    using var library = new LibraryStore(fixture.LibraryFolder);
    var category = library.SaveTag(null, "Appearance", null, true, null);
    var tag = library.SaveTag(null, "Blonde hair", category.Id, false, null);
    await library.ImportAsync(new[] { fixture.Source("summer holiday.png", "one"), fixture.Source("winter.jpg", "two") }, _ => new(1920, 1080));
    var first = library.Snapshot.Images[0];
    var second = library.Snapshot.Images[1];
    library.SetImageTag(first.Id, tag.Id, true);
    var snapshot = library.Snapshot;
    first = snapshot.Images[0];
    foreach (var query in new[] { "tag:\"Blonde hair\"", "Blonde_hair", "Appearance/Blonde_hair",
        "file:\"summer holiday\" ext:png width:>=1920 height:1080 -tag:blue", "summer blonde", "-is:untagged" })
    {
        var search = new ImageSearch(query, snapshot);
        Check.True(search.Matches(first), "Expected first image for: " + query);
        Check.True(!search.Matches(second), "Unexpected second image for: " + query);
    }
    Check.True(new ImageSearch("is:untagged -ext:png", snapshot).Matches(second), "Untagged filter failed.");
    Check.True(!new ImageSearch("width:>1920", snapshot).Matches(first), "Numeric comparison failed.");
    Check.Throws<ArgumentException>(() => new ImageSearch("tag:\"unfinished", snapshot));
    Check.Throws<ArgumentException>(() => new ImageSearch("width:big", snapshot));
    Check.Throws<ArgumentException>(() => new ImageSearch("unknown:value", snapshot));
    Check.Throws<ArgumentException>(() => new ImageSearch("tag:", snapshot));
}

static async Task Cancellation()
{
    using var fixture = new Fixture();
    using var library = new LibraryStore(fixture.LibraryFolder);
    using var cancellation = new CancellationTokenSource();
    var first = fixture.Source("first.png", "a");
    var second = fixture.Source("second.png", "b");
    var report = await library.ImportAsync(new[] { first, second }, _ => new(30, 40),
        new InlineProgress(_ => cancellation.Cancel()), cancellation.Token);
    Check.True(report.Cancelled, "Cancellation not reported.");
    Check.Equal(1, report.Imported);
    Check.Equal(1, Directory.GetFiles(library.ImagesFolder).Length);
    Check.True(File.Exists(second), "Cancellation removed a source file.");
}

static Task FailedWrites()
{
    using var fixture = new Fixture();
    using var library = new LibraryStore(fixture.LibraryFolder);
    library.SaveTag(null, "Saved", null, true, null);
    var before = File.ReadAllText(library.CatalogPath);
    var backup = library.CatalogPath + ".backup";
    File.Move(library.CatalogPath, backup);
    Directory.CreateDirectory(library.CatalogPath);
    try
    {
        Check.Throws<IOException>(() => library.SaveTag(null, "Unsaved", null, false, null));
        Check.Equal(1, library.Snapshot.Tags.Count);
        Check.Equal(before, File.ReadAllText(backup));
        Check.Equal(0, Directory.GetFiles(library.Folder, "*.tmp").Length);
    }
    finally { Directory.Delete(library.CatalogPath); File.Move(backup, library.CatalogPath); }
    return Task.CompletedTask;
}

static Task CorruptCatalogs()
{
    using var fixture = new Fixture();
    Directory.CreateDirectory(fixture.LibraryFolder);
    var path = Path.Combine(fixture.LibraryFolder, "library.json");
    void Reject(string contents)
    {
        File.WriteAllText(path, contents);
        try { using var unexpected = new LibraryStore(fixture.LibraryFolder); throw new Exception("Damaged catalog was accepted."); }
        catch (Exception ex) when (ex is InvalidDataException or JsonException) { }
        Check.Equal(contents, File.ReadAllText(path));
    }
    Reject("{broken");
    Reject("{\"Version\":99,\"Tags\":[],\"Images\":[]}");
    var id = Guid.NewGuid();
    Reject(JsonSerializer.Serialize(new LibrarySnapshot(1, new[] { new TagNode(id, "Cycle", id, true) }, Array.Empty<LibraryImage>())));
    var image = new LibraryImage(Guid.NewGuid(), "image.png", "../../outside.png", new string('A', 64), 10, 20, 100,
        DateTimeOffset.UtcNow, Array.Empty<Guid>());
    Reject(JsonSerializer.Serialize(new LibrarySnapshot(1, Array.Empty<TagNode>(), new[] { image })));
    File.WriteAllText(path, JsonSerializer.Serialize(LibrarySnapshot.Empty));
    using var library = new LibraryStore(fixture.LibraryFolder);
    Check.Equal(0, library.Snapshot.Images.Count);
    return Task.CompletedTask;
}

static Task LibraryLock()
{
    using var fixture = new Fixture();
    using (var first = new LibraryStore(fixture.LibraryFolder))
        Check.Throws<IOException>(() => { using var second = new LibraryStore(fixture.LibraryFolder); });
    using var reopened = new LibraryStore(fixture.LibraryFolder);
    return Task.CompletedTask;
}

static Task Settings()
{
    using var fixture = new Fixture();
    var path = fixture.PathFor("config/settings.json");
    var settings = new AppSettings(fixture.LibraryFolder);
    settings.Save(path);
    Check.Equal(settings, AppSettings.Load(path));
    File.WriteAllText(path, "{\"LibraryFolder\":\"relative-path\"}");
    Check.Throws<InvalidDataException>(() => AppSettings.Load(path));
    return Task.CompletedTask;
}

internal sealed class InlineProgress(Action<ImportProgress> callback) : IProgress<ImportProgress>
{ public void Report(ImportProgress value) => callback(value); }

internal sealed class Fixture : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "pawprint-tagger-tests-" + Guid.NewGuid().ToString("N"));
    internal string LibraryFolder => PathFor("library");
    internal string PathFor(string relative) => Path.Combine(Root, relative);
    internal string Source(string relative, string content)
    {
        var path = PathFor("originals/" + relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }
    public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
}

internal static class Check
{
    internal static void True(bool condition, string message) { if (!condition) throw new Exception(message); }
    internal static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
    internal static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("Expected exception " + typeof(T).Name);
    }
}
