"use strict";
// Uses the registered native host, an isolated Firefox profile, and a generated image.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const http = require("node:http");
const crypto = require("node:crypto");
const zlib = require("node:zlib");
const { spawn } = require("node:child_process");

function chunk(type, data) {
  const bytes = Buffer.concat([Buffer.from(type), data]);
  let crc = 0xffffffff;
  for (const byte of bytes) {
    crc ^= byte;
    for (let bit = 0; bit < 8; bit++) crc = (crc >>> 1) ^ (crc & 1 ? 0xedb88320 : 0);
  }
  const header = Buffer.alloc(4), tail = Buffer.alloc(4);
  header.writeUInt32BE(data.length); tail.writeUInt32BE((crc ^ 0xffffffff) >>> 0);
  return Buffer.concat([header, bytes, tail]);
}
function fixturePng() {
  const header = Buffer.alloc(13);
  header.writeUInt32BE(2, 0); header.writeUInt32BE(1, 4); header[8] = 8; header[9] = 2;
  return Buffer.concat([Buffer.from("89504e470d0a1a0a", "hex"), chunk("IHDR", header),
    chunk("IDAT", zlib.deflateSync(Buffer.from([0, 100, 160, 220, 220, 160, 100]))), chunk("IEND", Buffer.alloc(0))]);
}

(async () => {
  const args = process.argv.slice(2);
  const binary = args.includes("--firefox") ? args[args.indexOf("--firefox") + 1] : "firefox";
  if (!binary) throw new Error("Supply a Firefox executable after --firefox.");
  const root = path.resolve(__dirname, "..");
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), "pawprint-firefox-smoke-"));
  const source = path.join(temporary, "extension"), config = path.join(temporary, "config");
  const library = path.join(temporary, "library"), downloads = path.join(temporary, "downloads");
  fs.cpSync(path.join(root, "firefox"), source, { recursive: true });
  fs.mkdirSync(path.join(config, "pawprint-tagger"), { recursive: true });
  fs.mkdirSync(downloads);
  fs.writeFileSync(path.join(config, "pawprint-tagger/settings.json"), JSON.stringify({ LibraryFolder: library }));
  const png = fixturePng(), md5 = crypto.createHash("md5").update(png).digest("hex");
  let imageRequests = 0, receivedReferer = null, child, timeout;
  let reportResolve, reportReject;
  const report = new Promise((resolve, reject) => { reportResolve = resolve; reportReject = reject; });
  const server = http.createServer((request, response) => {
    if (request.url === "/image.png") {
      imageRequests++; receivedReferer = request.headers.referer;
      response.writeHead(200, { "Content-Type": "image/png", "Content-Length": png.length }); response.end(png);
    } else if (request.url === "/result" && request.method === "POST") {
      let body = "";
      request.on("data", bytes => { body += bytes; if (body.length > 100000) request.destroy(); });
      request.on("end", () => {
        try { reportResolve(JSON.parse(body)); response.end("ok"); }
        catch (error) { reportReject(error); response.writeHead(400); response.end(); }
      });
    } else { response.writeHead(404); response.end(); }
  });
  await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
  const origin = `http://127.0.0.1:${server.address().port}`;
  const manifestPath = path.join(source, "manifest.json");
  const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
  manifest.permissions.push("http://127.0.0.1/*");
  manifest.background.scripts.push("smoke.js");
  fs.writeFileSync(manifestPath, JSON.stringify(manifest));
  const post = { site: "danbooru", postId: "321", pageUrl: "https://danbooru.donmai.us/posts/321",
    originalUrl: `https://cdn.donmai.us/original/${md5}.png`, md5,
    tags: [{ name: "blonde_hair", category: "general" }, { name: "example_artist", category: "artist" },
      { name: "初音ミク", category: "character" }, { name: "fate/stay_night", category: "copyright" },
      { name: "highres", category: "meta" }] };
  const fixture = `<section class="image-container" data-id="321" data-file-url="${post.originalUrl}" data-md5="${md5}"></section>` +
    `<ul id="tag-list">${post.tags.map(tag => `<li data-tag-name="${tag.name}" data-tag-category="${tag.category}"></li>`).join("")}</ul>` +
    `<img src="https://cdn.donmai.us/sample/sample.png">`;
  const harness = `/* global browser, PawprintBooru, PawprintTransfers */
    (async () => {
      let result;
      try {
        const info = await browser.runtime.getBrowserInfo();
        const post = PawprintBooru.fromDocument(new DOMParser().parseFromString(${JSON.stringify(fixture)}, "text/html"), ${JSON.stringify(post.pageUrl)});
        if (post.originalUrl !== ${JSON.stringify(post.originalUrl)} || post.tags.length !== 5) throw new Error("Original extraction failed.");
        // Only this test adapter maps the synthetic original to a local fixture server.
        const downloadApi = { download: options => browser.downloads.download({ ...options, url: ${JSON.stringify(origin + "/image.png")} }),
          search: query => browser.downloads.search(query), onChanged: browser.downloads.onChanged, onErased: browser.downloads.onErased };
        const api = { runtime: browser.runtime, storage: browser.storage, downloads: downloadApi };
        const transfers = new PawprintTransfers.TransferManager(api, PawprintBooru);
        const [tab] = await browser.tabs.query({ active: true, currentWindow: true });
        const connection = await transfers.ping();
        const first = await transfers.add(post, tab);
        const second = await transfers.add({ ...post, tags: [...post.tags, { name: "blue_eyes", category: "general" }] }, tab);
        result = { ok: true, browser: info.version, connection, first, second, transfers: await transfers.list() };
      } catch (error) { result = { ok: false, error: error.message, stack: error.stack }; }
      await fetch(${JSON.stringify(origin + "/result")}, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(result) });
    })();`;
  fs.writeFileSync(path.join(source, "smoke.js"), harness);
  try {
    child = spawn(process.execPath, [path.join(root, "node_modules/web-ext/bin/web-ext.js"), "run",
      "--source-dir", source, "--firefox", binary, "--no-reload", "--no-input", "--no-config-discovery",
      "--start-url", "about:blank", "--args=-headless", "--pref", "browser.download.folderList=2",
      "--pref", `browser.download.dir=${downloads}`, "--pref", "browser.download.useDownloadDir=true"],
    { env: { ...process.env, XDG_CONFIG_HOME: config }, detached: true, stdio: ["ignore", "pipe", "pipe"] });
    let output = "";
    child.stdout.on("data", bytes => { output = (output + bytes).slice(-12000); });
    child.stderr.on("data", bytes => { output = (output + bytes).slice(-12000); });
    child.on("error", reportReject);
    child.on("exit", code => { if (code) reportReject(new Error(`web-ext exited with ${code}.\n${output}`)); });
    timeout = setTimeout(() => reportReject(new Error(`Firefox smoke test timed out.\n${output}`)), 50000);
    const result = await report;
    assert.equal(result.ok, true, result.error);
    assert.equal(result.connection.libraryFolder, library);
    assert.equal(result.first.ok, true, result.first.error);
    assert.equal(result.first.status, "imported"); assert.equal(result.first.tagCount, 5);
    assert.equal(result.second.status, "updated"); assert.equal(result.second.tagCount, 6);
    assert.equal(result.first.imageId, result.second.imageId);
    const catalog = JSON.parse(fs.readFileSync(path.join(library, "library.json"), "utf8"));
    assert.equal(catalog.Images.length, 1);
    const image = catalog.Images[0];
    assert.equal(image.Width, 2); assert.equal(image.Height, 1); assert.equal(image.TagIds.length, 6);
    assert.equal(image.Sources.length, 1);
    assert.deepEqual(fs.readFileSync(path.join(library, "images", image.StoredName)), png);
    assert.ok(catalog.Tags.some(tag => tag.Name === "fate/stay_night"));
    assert.ok(catalog.Tags.some(tag => tag.Name === "初音ミク"));
    assert.equal(imageRequests, 2); assert.equal(receivedReferer, post.pageUrl);
    console.log(`PASS Firefox ${result.browser}: actual downloads, native bridge, exact original bytes, Unicode/category tags and deduplication`);
  } finally {
    clearTimeout(timeout);
    if (child?.pid) {
      const ended = new Promise(resolve => child.once("exit", resolve));
      try { process.kill(-child.pid, "SIGTERM"); } catch (error) { if (error.code !== "ESRCH") throw error; }
      await Promise.race([ended, new Promise(resolve => setTimeout(resolve, 1000))]);
      try { process.kill(-child.pid, "SIGKILL"); } catch (error) { if (error.code !== "ESRCH") throw error; }
    }
    server.closeAllConnections(); await new Promise(resolve => server.close(resolve));
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
