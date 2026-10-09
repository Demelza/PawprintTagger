using System.Text.Json;

namespace PawprintTagger.Gtk;

internal static class FirefoxBridgeInstaller
{
    internal static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mozilla", "native-messaging-hosts");

    internal static string Install(string directory)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("The native bridge installer currently supports Linux.");
        directory = Path.GetFullPath(directory);
        var executable = Path.Combine(AppContext.BaseDirectory, "PawprintTagger");
        if (!File.Exists(executable)) throw new FileNotFoundException("Publish or build Pawprint Tagger with its executable before registering the bridge.");
        Directory.CreateDirectory(directory);
        var launcher = Path.Combine(directory, "pawprint-tagger-host");
        var manifest = Path.Combine(directory, BrowserHost.HostName + ".json");
        var script = "#!/bin/sh\nexec " + ShellQuote(executable) + " --native-messaging \"$@\"\n";
        File.WriteAllText(launcher + ".tmp", script);
        File.SetUnixFileMode(launcher + ".tmp", UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.Move(launcher + ".tmp", launcher, overwrite: true);
        File.WriteAllText(manifest + ".tmp", JsonSerializer.Serialize(new
        {
            name = BrowserHost.HostName,
            description = "Import original booru images and tags into Pawprint Tagger",
            path = launcher,
            type = "stdio",
            allowed_extensions = new[] { BrowserHost.ExtensionId }
        }, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(manifest + ".tmp", manifest, overwrite: true);
        return manifest;
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
}
