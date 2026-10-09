# Pawprint Tagger

A native GTK3 image viewer and tag organizer, written in C# for .NET 10.

A companion [Firefox extension](browser-extension/README.md) adds original
images and their existing tags from Danbooru and Gelbooru. It supports Firefox
140 and later on Linux desktop.

## Build and run

Install the **.NET 10 SDK** and **GTK3 runtime**, then run from this directory:

```sh
dotnet build PawprintTagger.slnx
dotnet run --project src/PawprintTagger.Gtk/PawprintTagger.Gtk.csproj
```

The only external managed dependency is GtkSharp `3.24.24.95`. The first restore needs NuGet access. GTK3 must be installed separately
(for example, `libgtk-3-0` on Debian/Ubuntu or `gtk3` on Arch/Artix).

To use a particular library without changing your saved preference:

```sh
dotnet run --project src/PawprintTagger.Gtk/PawprintTagger.Gtk.csproj -- --library /path/to/library
```

To publish an application directory:

```sh
dotnet publish src/PawprintTagger.Gtk/PawprintTagger.Gtk.csproj -c Release -o artifacts/PawprintTagger
./artifacts/PawprintTagger/PawprintTagger
```

The published application requires the .NET 10 runtime and GTK3. The included
desktop launcher can be installed after placing the published executable on
your `PATH`.

## Using the library

- **Import images…** copies selected files into your library. Source files
  remain untouched. Identical content is detected with SHA-256 and imported
  once, even when filenames differ. Equal filenames with different contents
  can coexist. Imports run in the background, report failures per file, and
  can be cancelled; already imported images remain saved.
- The left tag tree takes **15%** of the window width. Create categories with
  **+ Group**, then create tags with **+ Tag**. The equally sized **+ Tag**,
  **+ Group**, **Edit**, and **Delete** controls sit below **All images**.
  Selecting a category makes
  it the parent of new entries. For example, create `Appearance`, then `Hair`
  inside it, then the tag `Blonde hair` inside `Hair`.
- **Edit** changes an entry's name, parent category, and optional color
  label. Colors appear in the tree and are inherited by child entries. Turning
  off an entry's own color restores inheritance. Categories cannot be moved
  into their descendants. **Delete** asks before removing an entry and its
  descendants, and removes their tag assignments from images.
- Selecting a tag filters the gallery to images with that tag. Selecting a
  category includes images with any of its descendant tags. **All images**
  clears the tree filter while keeping the search query.
- Select a thumbnail to see its preview, dimensions, original filename, file
  size, import date, and tag checklist on the right. Check or uncheck tags to
  save assignments immediately. Categories organize tags; they are not assigned
  directly to images.
- Double-click a thumbnail or press Enter on it to open
  an image across the application window. Its aspect ratio is preserved as
  the window resizes. **Back** or Escape returns to the gallery; Left/Right
  or **Previous**/**Next** browses the current results.
- **Library folder…** opens or creates a library at a different location and
  saves that choice. Each folder is a separate library. Switching folders does
  not migrate your existing library; to move one, close the app, copy its entire
  folder to the new location, and open that folder.

The gallery scrolls through all results. Thumbnails are decoded asynchronously,
cached on disk, and retained in the grid only near the current viewport. The
gallery starts empty; it does not add sample files or tags to your library.

## Search

Words match filenames and full tag paths, case-insensitively. Terms are combined
with AND. Quote phrases with spaces, or write tag names with underscores.
A leading minus excludes a term. The tree selection and search are combined.

| Query | Matches |
| --- | --- |
| `Blonde_hair` | Filename or tag path containing “blonde hair” |
| `tag:"Blonde hair"` | An assigned tag path containing “blonde hair” |
| `tag:Appearance/Hair` | Tags within this path |
| `file:holiday -tag:portrait` | Holiday filenames without portrait tags |
| `ext:png width:>=1920 height:>1080` | PNGs meeting both dimension constraints |
| `is:untagged` | Images without assigned tags |
| `size:<1048576` | Files smaller than 1 MiB |

`name:` is an alias for `file:`. Numeric fields accept `=`, `>`, `>=`, `<`,
and `<=`; a bare number means equality. The **?** button shows help in the app.
Use **Ctrl+F** to focus search and **Ctrl+O** to import images.

## Storage

The initial library is `Pictures/PawprintTagger` in your home folder. Your saved
library choice is in `$XDG_CONFIG_HOME/pawprint-tagger/settings.json`, or
`~/.config/pawprint-tagger/settings.json` when XDG_CONFIG_HOME is unset.

```text
PawprintTagger/
  library.json       # Versioned image metadata, categories, tags and assignments
  images/            # Owned image copies with collision-resistant filenames
  thumbnails/        # Rebuildable PNG thumbnails
  .pawprint.lock     # Prevents two instances writing to the same library
```

Metadata writes use a temporary file followed by an atomic replacement. Failed
writes leave the previous catalog and in-memory state intact. An invalid catalog
is reported without resetting it. Back up `library.json` and `images/` together.
You can clear `thumbnails/` while the application is closed to reclaim cache
space; thumbnails will be rebuilt.

Image support depends on installed GdkPixbuf loaders. Common GTK installations
support PNG, JPEG, GIF, BMP and TIFF; other formats depend on your system.
GIF and other animated images currently display a static frame. JPEG embedded
orientation is applied to previews and the viewer. Full-window viewing fits the
image to the window; zoom and animated playback are not implemented yet.

## Verification

The test executable has no additional test framework dependencies:

```sh
dotnet run --project tests/PawprintTagger.Tests/PawprintTagger.Tests.csproj
```

It checks original preservation, duplicates and filename collisions, tag editing
and persistence, category filtering, search syntax, cancellation, write failures,
damaged catalogs, library locking, settings, booru tag/source imports, original
checksums, native-message framing, active-library routing, and closed-app browser
imports. It creates isolated temporary
libraries and does not touch your saved library.

For the native GTK check, run in a graphical desktop session:

```sh
G_DEBUG=fatal-criticals dotnet run --project tests/PawprintTagger.Tests/PawprintTagger.Tests.csproj -- --gtk-smoke
```

This creates real image fixtures and checks import controls, tag checkboxes,
thumbnails, category/search filtering, image activation, viewing after resize,
Back, empty results, GTK cleanup, and bounded thumbnail retention while
scrolling a 182-image library, plus browser imports into the active window.
Optionally set
`PAWPRINT_SCREENSHOT=/tmp/pawprint-tagger.png` to capture
the test window. On a headless machine, run the command under `xvfb-run` if Xvfb
is installed.

Source is separated into a UI-independent `PawprintTagger.Core` library, the
`PawprintTagger.Gtk` application, and integration checks. The `example/` project
is reference material and is not part of the new solution.
