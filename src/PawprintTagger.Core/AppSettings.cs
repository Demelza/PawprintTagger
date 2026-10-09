using System.Text.Json;

namespace PawprintTagger.Core;

public sealed record AppSettings(string LibraryFolder)
{
    public static string DefaultLibraryFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures) is { Length: > 0 } pictures
            ? pictures : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures"),
        "PawprintTagger");

    public static string DefaultSettingsPath => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config && Path.IsPathRooted(config)
            ? config : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "pawprint-tagger", "settings.json");

    public static AppSettings Load(string path)
    {
        if (!File.Exists(path)) return new(DefaultLibraryFolder);
        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path))
            ?? throw new InvalidDataException("The settings file is empty.");
        if (string.IsNullOrWhiteSpace(settings.LibraryFolder) || !Path.IsPathFullyQualified(settings.LibraryFolder))
            throw new InvalidDataException("The saved library folder must be an absolute path.");
        return settings;
    }

    public void Save(string path) => AtomicJson.Write(path, this);
}
