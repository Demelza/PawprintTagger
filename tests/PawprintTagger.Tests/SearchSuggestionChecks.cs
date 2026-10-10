using PawprintTagger.Core;

internal static class SearchSuggestionChecks
{
    internal static Task Matching()
    {
        using var fixture = new Fixture();
        using var library = new LibraryStore(fixture.LibraryFolder);
        var appearance = library.SaveTag(null, "Appearance", null, true, null);
        var hair = library.SaveTag(null, "Hair", appearance.Id, true, null);
        var blonde = library.SaveTag(null, "Blonde hair", hair.Id, false, null);
        var blue = library.SaveTag(null, "blue_eyes", appearance.Id, false, null);
        var named = library.SaveTag(null, "tablecloth", null, false, null);
        var suggestions = new TagSearchSuggestions(library.Snapshot);
        foreach (var query in new[] { "BLO", "tag:blonde_h", "-tag:\"Blonde h", "ext:png -blo" })
            Check.True(suggestions.Find(query, query.Length).Any(tag => tag.Id == blonde.Id), "Missing suggestion for " + query);
        Check.Equal(blue.Id, suggestions.Find("blue e", 4).Single().Id);
        var ranked = suggestions.Find("bl", 2);
        Check.Equal(blonde.Id, ranked[0].Id);
        Check.True(ranked.Any(tag => tag.Id == named.Id), "Substring matches were missing.");
        Check.True(suggestions.Find("Appearance/Hair/Bl", 18).All(tag => tag.Id == blonde.Id), "Path matching returned unrelated tags.");
        Check.Equal(3, suggestions.Find("tag:", 4).Count);
        foreach (var query in new[] { "", "-", "blue_eyes ", "file:blo", "width:100", "ext:pn", "is:un" })
            Check.Equal(0, suggestions.Find(query, query.Length).Count);
        Check.Equal(1, suggestions.Find("tag:", 4, limit: 1).Count);
        Check.True(suggestions.Tags.All(tag => tag.Id != appearance.Id && tag.Id != hair.Id), "Groups were suggested as assignable tags.");
        library.SaveTag(blonde.Id, "Golden hair", appearance.Id, false, null);
        var refreshed = new TagSearchSuggestions(library.Snapshot);
        Check.Equal(0, refreshed.Find("blonde", 6).Count);
        Check.Equal("Appearance/Golden hair", refreshed.Find("gold", 4).Single().Path);
        return Task.CompletedTask;
    }

    internal static async Task Completion()
    {
        using var fixture = new Fixture();
        using var library = new LibraryStore(fixture.LibraryFolder);
        var group = library.SaveTag(null, "Appearance", null, true, null);
        var tag = library.SaveTag(null, "Blonde hair", group.Id, false, null);
        await library.ImportAsync(new[] { fixture.Source("tagged.png", "one"), fixture.Source("other.png", "two") }, _ => new(100, 200));
        var image = library.Snapshot.Images[0];
        library.SetImageTag(image.Id, tag.Id, true);
        var snapshot = library.Snapshot;
        var suggestions = new TagSearchSuggestions(snapshot);
        var chosen = suggestions.Find("bl", 2).Single();
        var completed = suggestions.Complete("ext:png bl", 10, chosen);
        Check.Equal("ext:png tag:\"Appearance/Blonde hair\" ", completed.Text);
        Check.Equal(completed.Text.Length, completed.Cursor);
        Check.True(new ImageSearch(completed.Text, snapshot).Matches(snapshot.Images[0]), "Completion did not match its assigned image.");
        Check.True(!new ImageSearch(completed.Text, snapshot).Matches(snapshot.Images[1]), "Completion matched an untagged image.");
        var middle = suggestions.Complete("file:tagged -bl width:100", 14, chosen);
        Check.Equal("file:tagged -tag:\"Appearance/Blonde hair\" width:100", middle.Text);
        var excluded = suggestions.Complete("-tag:\"bl", 8, chosen);
        Check.True(!new ImageSearch(excluded.Text, snapshot).Matches(snapshot.Images[0]), "The completion lost its exclusion.");
        Check.True(new ImageSearch(excluded.Text, snapshot).Matches(snapshot.Images[1]), "Exclusion removed an unrelated image.");
        var inside = suggestions.Complete("tag:\"Blonde old\" ext:png", 11, chosen);
        Check.Equal("tag:\"Appearance/Blonde hair\" ext:png", inside.Text);
        Check.Throws<ArgumentException>(() => suggestions.Complete("file:bl", 7, chosen));
        Check.Throws<ArgumentException>(() => suggestions.Complete("bl", 2, new(Guid.NewGuid(), "Foreign", "Foreign")));
    }

    internal static async Task UnicodeAndEscapes()
    {
        using var fixture = new Fixture();
        using var library = new LibraryStore(fixture.LibraryFolder);
        var tag = library.SaveTag(null, "初音ミク 😀 \"quoted\" \\ slash", null, false, null);
        await library.ImportAsync(new[] { fixture.Source("unicode.png", "one") }, _ => new(10, 20));
        library.SetImageTag(library.Snapshot.Images.Single().Id, tag.Id, true);
        var snapshot = library.Snapshot;
        var suggestions = new TagSearchSuggestions(snapshot);
        var chosen = suggestions.Find("初音", 2).Single();
        var completed = suggestions.Complete("ext:png tag:\"初音", 15, chosen);
        Check.True(completed.Text.Contains("\\\"quoted\\\""), "A quoted tag name was not escaped.");
        Check.True(completed.Text.Contains("\\\\ slash"), "A literal backslash was not escaped.");
        Check.True(new ImageSearch(completed.Text, snapshot).Matches(snapshot.Images.Single()), "An escaped Unicode tag could not be searched.");
        Check.Equal(completed.Text.Length, completed.Cursor);
        var emojiPrefix = "file:\"😀\" 初音 width:10";
        var inserted = suggestions.Complete(emojiPrefix, emojiPrefix.IndexOf("初音", StringComparison.Ordinal) + 2, chosen);
        Check.True(inserted.Text.StartsWith("file:\"😀\" tag:", StringComparison.Ordinal), "A Unicode prefix was damaged.");
        Check.True(inserted.Text.EndsWith(" width:10", StringComparison.Ordinal), "Following metadata was damaged.");
        Check.Throws<ArgumentException>(() => new ImageSearch("tag:\"unfinished\\\"", snapshot));
    }
}
