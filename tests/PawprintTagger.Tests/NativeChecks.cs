using Gtk;
using PawprintTagger.Core;
using PawprintTagger.Gtk;
using Task = System.Threading.Tasks.Task;
using Action = System.Action;
using System.Runtime.InteropServices;

internal static class NativeChecks
{
    [DllImport("libgtk-3.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void gtk_icon_view_item_activated(IntPtr view, IntPtr path);
    [DllImport("libgtk-3.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern int gtk_icon_view_get_item_row(IntPtr view, IntPtr path);
    [DllImport("libgobject-2.0.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void g_signal_emit_by_name(IntPtr instance, string signal, string path);
    internal static Task<int> Run()
    {
        Application.Init();
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.PathFor("originals"));
        var first = fixture.PathFor("originals/landscape.png");
        var second = fixture.PathFor("originals/portrait.png");
        using (var image = new Gdk.Pixbuf(Gdk.Colorspace.Rgb, true, 8, 640, 360))
        { image.Fill(0x6495EDFF); image.Save(first, "png"); }
        using (var image = new Gdk.Pixbuf(Gdk.Colorspace.Rgb, true, 8, 240, 480))
        { image.Fill(0xCD5C5CFF); image.Save(second, "png"); }
        using var library = new LibraryStore(fixture.LibraryFolder);
        var category = library.SaveTag(null, "Appearance", null, true, "#729FCF");
        var hair = library.SaveTag(null, "Hair", category.Id, true, null);
        var tag = library.SaveTag(null, "Blonde hair", hair.Id, false, null);
        var window = new MainWindow(library, null);
        window.ShowAll();
        var result = 0;
        GtkDispatcher.Post(() => _ = CheckWindow());
        Application.Run();
        return Task.FromResult(result);

        async Task CheckWindow()
        {
            try
            {
                var dimensions = ImageLoader.Inspect(first);
                Check.Equal(640, dimensions.Width);
                Check.Equal(360, dimensions.Height);
                await OnGtk(() => window.StartImport(new[] { first, second }));
                await WaitFor(() => !window.IsImportRunning && window.VisibleImageCount == 2);
                var tagged = library.Snapshot.Images.Single(image => image.OriginalName == "landscape.png");
                await OnGtk(() =>
                {
                    using var path = new TreePath("1");
                    window.ImageGrid.SelectPath(path);
                    g_signal_emit_by_name(window.AssignmentToggle.Handle, "toggled", "0");
                });
                Check.True(library.Snapshot.Images.Single(image => image.Id == tagged.Id).TagIds.Contains(tag.Id),
                    "The GTK checkbox did not save its tag assignment.");
                await WaitFor(() => window.LoadedThumbnailCount == 2);
                using (var thumbnail = await new ImageLoader().LoadAsync(library.ImagePath(tagged), 160, 120,
                    CancellationToken.None, library.ThumbnailPath(tagged)))
                {
                    Check.Equal(160, thumbnail.Width);
                    Check.Equal(120, thumbnail.Height);
                }
                Console.WriteLine("PASS Native GTK import, tag assignment, thumbnails and image dimensions");

                await OnGtk(() => window.FilterCategory(category.Id));
                Check.Equal(1, await OnGtk(() => window.VisibleImageCount));
                await OnGtk(() => window.FilterCategory(null));
                await OnGtk(() => window.SearchText = "tag:Blonde_hair ext:png width:>=640");
                Check.Equal(1, await OnGtk(() => window.VisibleImageCount));
                Console.WriteLine("PASS Native GTK category filtering and search");

                await OnGtk(() =>
                {
                    using var path = new TreePath("0");
                    gtk_icon_view_item_activated(window.ImageGrid.Handle, path.Handle);
                });
                await WaitFor(() => window.ViewerVisible && window.ViewerHasImage);
                await OnGtk(() => window.Resize(1000, 680));
                await Task.Delay(250);
                await OnGtk(() => window.BackToLibrary());
                Check.True(!await OnGtk(() => window.ViewerVisible), "Back failed to restore the library.");
                Check.Equal(1, await OnGtk(() => window.VisibleImageCount));
                Console.WriteLine("PASS Native GTK image activation, resized viewing and Back");

                await OnGtk(() => window.SearchText = "is:untagged");
                Check.Equal(1, await OnGtk(() => window.VisibleImageCount));
                await OnGtk(() => window.SearchText = "no-match");
                Check.Equal(0, await OnGtk(() => window.VisibleImageCount));
                await OnGtk(() => window.SearchText = "");
                await WaitFor(() => window.LoadedThumbnailCount == 2);
                await OnGtk(() =>
                {
                    using var path = new TreePath("1");
                    window.ImageGrid.SelectPath(path);
                });
                await Task.Delay(150);
                await OnGtk(() =>
                {
                    Check.True(window.Allocation.Width <= 1400, "Gallery layout expanded the window beyond its requested size.");
                    Check.True(window.GalleryFitsViewport, "Thumbnail columns overflow the gallery viewport.");
                    using var one = new TreePath("0");
                    using var two = new TreePath("1");
                    Check.Equal(gtk_icon_view_get_item_row(window.ImageGrid.Handle, one.Handle),
                        gtk_icon_view_get_item_row(window.ImageGrid.Handle, two.Handle));
                });
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Delay(150);
                Console.WriteLine("PASS Native GTK empty results, refresh and garbage collection");
                if (Environment.GetEnvironmentVariable("PAWPRINT_SCREENSHOT") is { Length: > 0 } screenshot)
                {
                    await OnGtk(() =>
                    {
                        using var surface = new Cairo.ImageSurface(Cairo.Format.Argb32,
                            window.Allocation.Width, window.Allocation.Height);
                        using var context = new Cairo.Context(surface);
                        window.Draw(context);
                        surface.WriteToPng(screenshot);
                    });
                }
                var sources = new List<string>();
                for (var index = 0; index < 180; index++)
                {
                    var path = fixture.PathFor($"originals/tile-{index}.png");
                    using var tile = new Gdk.Pixbuf(Gdk.Colorspace.Rgb, true, 8, 64, 64);
                    tile.Fill((uint)((0x336600 + index) * 256 + 255));
                    tile.Save(path, "png");
                    sources.Add(path);
                }
                var stress = await Task.Run(() => library.ImportAsync(sources, ImageLoader.Inspect));
                Check.Equal(180, stress.Imported);
                await OnGtk(() => window.RefreshAll());
                await Task.Delay(200);
                await OnGtk(() =>
                {
                    using var last = new TreePath("181");
                    window.ImageGrid.ScrollToPath(last, 0.5f, 0.5f);
                });
                await WaitFor(() =>
                {
                    if (!window.ImageGrid.GetVisibleRange(out var start, out var end)) return false;
                    using (start) using (end) return end.Indices[0] == 181;
                });
                await Task.Delay(500);
                Check.True(await OnGtk(() => window.Allocation.Width <= 1400), "Scrolling expanded the window.");
                Check.True(await OnGtk(() => window.LoadedThumbnailCount is > 0 and < 80),
                    "The gallery retained thumbnails outside the viewport.");
                Console.WriteLine("PASS Native GTK responsive grid and bounded thumbnails after scrolling 182 images");
            }
            catch (Exception ex) { result = 1; Console.Error.WriteLine(ex); }
            finally { await OnGtk(() => window.CloseApp()); }
        }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await OnGtk(condition))
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("GTK operation did not finish.");
            await Task.Delay(40);
        }
    }

    private static Task OnGtk(Action action) => OnGtk(() => { action(); return true; });
    private static Task<T> OnGtk<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        GtkDispatcher.Post(() =>
        {
            try { completion.SetResult(action()); }
            catch (Exception ex) { completion.SetException(ex); }
        });
        return completion.Task;
    }
}
