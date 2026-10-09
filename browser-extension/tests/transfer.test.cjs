"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const { TransferManager } = require("../firefox/transfer.js");
const booru = require("../firefox/booru.js");
const post = { site: "danbooru", postId: "123", pageUrl: "https://danbooru.donmai.us/posts/123",
  originalUrl: "https://cdn.donmai.us/original/image.png", tags: [{ name: "blonde_hair", category: "general" }], md5: null };
const tab = { id: 1, incognito: false, cookieStoreId: "firefox-container-1" };
class Event {
  listeners = new Set();
  addListener(value) { this.listeners.add(value); }
  removeListener(value) { this.listeners.delete(value); }
  emit(value) { for (const listener of this.listeners) listener(value); }
}
function fakeBrowser({ state = "complete", importFails = false, hostMissing = false, previous = [] } = {}) {
  const calls = { downloads: [], native: [], storage: [] };
  const changed = new Event();
  const erased = new Event();
  const items = new Map();
  const browser = {
    storage: { local: { get: async () => ({ transfers: previous }), set: async value => { calls.storage.push(structuredClone(value)); } } },
    downloads: { onChanged: changed, onErased: erased,
      download: async options => {
        calls.downloads.push(options);
        const id = calls.downloads.length;
        items.set(id, { id, state, filename: `/tmp/firefox-download-${id}.png`, exists: true, error: state === "interrupted" ? "USER_CANCELED" : undefined });
        // Exercise a download that completes before its caller has installed event listeners.
        changed.emit({ id, state: { current: state } });
        return id;
      }, search: async ({ id }) => items.has(id) ? [items.get(id)] : [] },
    runtime: { sendNativeMessage: async (name, message) => {
      calls.native.push({ name, message });
      if (hostMissing) throw new Error("No such native application");
      if (message.action === "import" && importFails) return { ok: false, error: "Library is locked" };
      return { ok: true, status: message.action === "ping" ? "ready" : "imported", tagCount: 1, libraryFolder: "/tmp/library" };
    } }
  };
  return { browser, calls, items, setImportFails: value => { importFails = value; } };
}

test("Original download uses the post referer and container, then imports only its completed absolute filename", async () => {
  const { browser, calls } = fakeBrowser();
  const manager = new TransferManager(browser, booru);
  const reply = await manager.add(post, tab);
  assert.equal(reply.ok, true);
  assert.equal(calls.downloads.length, 1);
  assert.equal(calls.downloads[0].url, post.originalUrl);
  assert.equal(calls.downloads[0].headers[0].value, post.pageUrl);
  assert.equal(calls.downloads[0].cookieStoreId, tab.cookieStoreId);
  assert.equal(calls.downloads[0].conflictAction, "uniquify");
  assert.equal(calls.native[1].message.filePath, "/tmp/firefox-download-1.png");
  assert.equal(calls.native[1].message.post.tags[0].name, "blonde_hair");
  assert.equal(browser.downloads.onChanged.listeners.size, 0);
  assert.equal(browser.downloads.onErased.listeners.size, 0);
});

test("Simultaneous clicks for the same post produce one download and one import", async () => {
  const { browser, calls } = fakeBrowser();
  const manager = new TransferManager(browser, booru);
  await Promise.all([manager.add(post, tab), manager.add(post, tab)]);
  assert.equal(calls.downloads.length, 1);
  assert.equal(calls.native.filter(call => call.message.action === "import").length, 1);
});

test("Different posts can download together while native imports serialize, including after a failure", async () => {
  const fake = fakeBrowser();
  const original = fake.browser.runtime.sendNativeMessage;
  let active = 0, highest = 0, imports = 0;
  fake.browser.runtime.sendNativeMessage = async (name, message) => {
    if (message.action === "ping") return original(name, message);
    active++; highest = Math.max(highest, active); imports++;
    await new Promise(resolve => setTimeout(resolve, 5));
    active--;
    if (imports === 1) return { ok: false, error: "First image could not be imported" };
    return original(name, message);
  };
  const manager = new TransferManager(fake.browser, booru);
  const second = { ...post, postId: "456", pageUrl: "https://danbooru.donmai.us/posts/456" };
  const results = await Promise.allSettled([manager.add(post, tab), manager.add(second, tab)]);
  assert.equal(results[0].status, "rejected");
  assert.equal(results[1].status, "fulfilled");
  assert.equal(highest, 1);
  assert.equal(fake.calls.downloads.length, 2);
});

test("Interrupted originals are reported and never passed to the native importer", async () => {
  const { browser, calls } = fakeBrowser({ state: "interrupted" });
  const manager = new TransferManager(browser, booru);
  await assert.rejects(manager.add(post, tab), /interrupted/);
  assert.equal(calls.native.filter(call => call.message.action === "import").length, 0);
  assert.equal(browser.downloads.onChanged.listeners.size, 0);
});

test("Missing native registration is diagnosed before downloading an image", async () => {
  const { browser, calls } = fakeBrowser({ hostMissing: true });
  await assert.rejects(new TransferManager(browser, booru).add(post, tab), /install-firefox-bridge/);
  assert.equal(calls.downloads.length, 0);
});

test("A failed desktop import is persisted and can be retried without downloading again", async () => {
  const fake = fakeBrowser({ importFails: true });
  const manager = new TransferManager(fake.browser, booru);
  await assert.rejects(manager.add(post, tab), /Retry import/);
  assert.equal((await manager.list())[0].status, "needs-import");
  fake.setImportFails(false);
  const reply = await manager.retry(1);
  assert.equal(reply.ok, true);
  assert.equal(fake.calls.downloads.length, 1);
  assert.equal((await manager.list())[0].status, "imported");
});

test("Private-window imports use private cookies without persisting their post history", async () => {
  const { browser, calls } = fakeBrowser();
  await new TransferManager(browser, booru).add(post, { ...tab, incognito: true });
  assert.equal(calls.downloads[0].incognito, true);
  assert.equal(calls.storage.length, 0);
});

test("A missing downloaded file cannot be imported during retry", async () => {
  const fake = fakeBrowser({ importFails: true });
  const manager = new TransferManager(fake.browser, booru);
  await assert.rejects(manager.add(post, tab));
  fake.items.get(1).exists = false;
  const before = fake.calls.native.length;
  await assert.rejects(manager.retry(1), /Keep its downloaded file/);
  assert.equal(fake.calls.native.length, before);
});

test("A transfer completed before Firefox restarts is automatically recovered from saved state", async () => {
  const previous = [{ id: 7, post, fileName: "danbooru-123.png", status: "downloading", private: false }];
  const fake = fakeBrowser({ previous });
  fake.items.set(7, { id: 7, state: "complete", filename: "/tmp/recovered.png", exists: true });
  const manager = new TransferManager(fake.browser, booru);
  await manager.ready;
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(fake.calls.downloads.length, 0);
  assert.equal(fake.calls.native[0].message.filePath, "/tmp/recovered.png");
  assert.equal((await manager.list())[0].status, "imported");
});

test("An older pending import remains retryable after more than twenty recent imports", async () => {
  const completed = Array.from({ length: 20 }, (_, index) => ({ id: index + 1, post, status: "imported", private: false }));
  const pending = { id: 100, post, fileName: "danbooru-123.png", status: "needs-import", private: false };
  const fake = fakeBrowser({ previous: [...completed, pending] });
  fake.items.set(100, { id: 100, state: "complete", filename: "/tmp/older-original.png", exists: true });
  const manager = new TransferManager(fake.browser, booru);
  const reply = await manager.retry(100);
  assert.equal(reply.ok, true);
  assert.equal(fake.calls.downloads.length, 0);
  assert.equal(fake.calls.native[0].message.filePath, "/tmp/older-original.png");
});
