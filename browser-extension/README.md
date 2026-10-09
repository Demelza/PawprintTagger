# Pawprint Tagger for Firefox

Adds **Add to Pawprint Tagger** beside the original-image/download action on
individual Danbooru, Safebooru (safebooru.donmai.us), and Gelbooru post pages.
The toolbar popup can also add the current post and retry a failed import.
Supports **Firefox 140 and later on Linux desktop** with .NET 10 and GTK3.

## Install the development build

From the Pawprint Tagger project directory:

```sh
dotnet publish src/PawprintTagger.Gtk/PawprintTagger.Gtk.csproj -c Release -o artifacts/PawprintTagger
./artifacts/PawprintTagger/PawprintTagger --install-firefox-bridge
```

The registration command writes the host manifest and launcher to
`~/.mozilla/native-messaging-hosts/`. It uses the published application at its
current absolute path. Run it again after moving that application directory.
An optional directory argument stages registration files elsewhere for review.

In Firefox, open `about:debugging#/runtime/this-firefox`, select **Load Temporary
Add-on**, and choose `browser-extension/firefox/manifest.json` or
`artifacts/firefox/PawprintTaggerFirefox-0.1.0.xpi`. Use **Check desktop connection**
in the extension popup to verify registration, then reload an open booru page.

Temporary add-ons are removed when Firefox closes. A permanent installation in
regular Firefox requires [Mozilla signing](https://extensionworkshop.com/documentation/publish/signing-and-distribution-overview/).
The package included here is unsigned; it is ready for testing or submission
for an unlisted signed build. No browser signature preferences need changing
for temporary installation.

The bridge installer targets Linux desktop Firefox. Snap/Flatpak Firefox may
require the package's native-messaging integration. Android is not supported by
the desktop bridge.

## How imports work

1. The extension reads the post's explicit original link or original-file
   metadata and tag sidebar. Danbooru's same-origin post JSON is a fallback.
   Sample and preview URLs are rejected, and videos/animation archives report
   an unsupported-original error.
2. Firefox downloads the original with the current session, including the
   selected Firefox container, to `PawprintTagger/` inside the configured
   download directory. Firefox's own download controls remain available.
3. Once the download completes, a native message supplies its local path and
   post metadata to Pawprint Tagger. The app copies the bytes into its library.
   If the post supplies an MD5, the downloaded bytes must match it.
4. An open app receives the import and refreshes its current library. With the
   app closed, the bridge uses the saved library folder without opening a window.
   Source downloads are kept; they can be removed after a successful import.

Imported tags retain their booru spelling, including underscores and slashes.
They are organized under `Booru / site / General`, `Artist`, `Character`,
`Copyright`, or `Meta`, with color labels for the specialized groups. A uniquely
matching local tag is reused, including names written with spaces instead of
underscores. Tags can be moved and edited using the desktop's existing controls.
Original post/image URLs are stored with the library image.

Image content is deduplicated with SHA-256. Adding an existing image merges
missing tags and source information while preserving local tag assignments.
Image and tag metadata are committed together. A failed import leaves the
download available for **Retry import** in the popup; retrying does not download
the file again. Imports interrupted by closing Firefox are recovered when the
extension next starts, if the download remains available.

Image decoding uses your installed GdkPixbuf loaders. A format without an
installed loader produces an import error while preserving the download.
Animated images currently use the desktop viewer's static-frame behavior.

## Permissions and data

The extension reads post metadata only on the listed booru hosts. `downloads`
allows original downloads; `nativeMessaging` connects to the local app;
`storage` keeps recent imports for retry; `activeTab` powers the toolbar action.
`cookies` permits selecting the download's container; cookie values are never
read or sent to Pawprint Tagger.

Firefox's data declaration lists website content and browsing activity because
images, tags, and original post URLs are sent to a separate local application.
There is no remote Pawprint service, telemetry, API key, or account requirement.
Private-window history stays in memory; images explicitly imported from a
private window are still saved to the library. Allow the extension in private
windows in Firefox if you want that feature.

## Build and verify

Node.js 22 or later is needed only for development and packaging:

```sh
cd browser-extension
npm ci --ignore-scripts
npm test
npm run lint
npm run build
```

Runtime extension code has no external JavaScript dependencies. The XPI contains
only files from `firefox/`. Tests exercise both site adapters, original-versus-
sample selection, tag categories, the page button, containers, download failures,
retries, duplicate clicks, private history, and restart recovery. Desktop tests
also check framed native messages, the active-library pipe, closed-app imports,
checksums, and atomic rollback.

With the desktop published and the bridge registered, a real Firefox smoke
check can be run with an isolated profile, settings, and library:

```sh
node scripts/firefox-smoke.cjs --firefox /path/to/firefox
```

It downloads a generated fixture from a local test server, imports it through
Firefox's native messaging, verifies its bytes and tags, and checks re-import
deduplication. It never reads or changes the saved user library.

Verified with Firefox **140.0**: actual download completion, native-host launch,
exact image bytes, Unicode/slash tags, all five categories, and tag merging on
re-import. The extension also passes 19 automated checks; desktop core and
bridge behavior pass 14 checks, plus the native GTK integration check.

## Protocol references

- [Mozilla native messaging](https://developer.mozilla.org/en-US/docs/Mozilla/Add-ons/WebExtensions/Native_messaging)
- [Firefox downloads API](https://developer.mozilla.org/en-US/docs/Mozilla/Add-ons/WebExtensions/API/downloads/download)
- [Firefox data declarations and local applications](https://extensionworkshop.com/documentation/develop/firefox-builtin-data-consent/)
- [Danbooru original metadata](https://github.com/danbooru/danbooru/blob/master/app/helpers/posts_helper.rb)
- [Gelbooru API and tag categories](https://gelbooru.com/index.php?page=wiki&s=view&id=18780)
