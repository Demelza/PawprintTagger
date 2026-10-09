using Gtk;
using PawprintTagger.Core;

namespace PawprintTagger.Gtk;

internal static class Program
{
    private static int Main(string[] args)
    {
        // Firefox invokes this mode without opening a GTK window. Stdout belongs to the binary protocol.
        if (args.FirstOrDefault() == "--native-messaging")
            return BrowserHost.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), AppSettings.DefaultSettingsPath)
                .GetAwaiter().GetResult();
        if (args.FirstOrDefault() == "--install-firefox-bridge")
        {
            try
            {
                if (args.Length > 2) throw new ArgumentException("Usage: PawprintTagger --install-firefox-bridge [manifest-directory]");
                Console.WriteLine("Firefox bridge registered: " + FirefoxBridgeInstaller.Install(
                    args.Length == 2 ? args[1] : FirefoxBridgeInstaller.DefaultDirectory));
                return 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
            { Console.Error.WriteLine(ex.Message); return 1; }
        }
        if (args.Contains("--help"))
        {
            Console.WriteLine("Pawprint Tagger\nUsage: PawprintTagger [--library /absolute/library/folder]\n" +
                "       PawprintTagger --install-firefox-bridge [manifest-directory]\nRequires GTK3 and .NET 10.");
            return 0;
        }
        string? folder = null;
        if (args.Length != 0)
        {
            if (args.Length != 2 || args[0] != "--library")
            { Console.Error.WriteLine("Usage: PawprintTagger [--library /path/to/library]"); return 2; }
            folder = args[1];
        }
        GLib.Global.ApplicationName = "Pawprint Tagger";
        GLib.Global.ProgramName = "pawprint-tagger";
        Application.Init();
        LibraryStore? library = null;
        try
        {
            var settingsPath = folder is null ? AppSettings.DefaultSettingsPath : null;
            folder ??= AppSettings.Load(settingsPath!).LibraryFolder;
            library = new(folder);
            var window = new MainWindow(library, settingsPath);
            window.ShowAll();
            Application.Run();
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        {
            Console.Error.WriteLine("Could not open Pawprint Tagger: " + ex.Message);
            using var dialog = new MessageDialog(null, DialogFlags.Modal, MessageType.Error, ButtonsType.Close,
                false, "Could not open the image library") { SecondaryText = ex.Message +
                    "\n\nYour catalog has been left in place. You can choose a different folder with --library /path/to/folder." };
            dialog.Run();
            dialog.Destroy();
            return 1;
        }
        finally { library?.Dispose(); }
    }
}
