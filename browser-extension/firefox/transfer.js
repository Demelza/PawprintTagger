"use strict";
(() => {
  const host = "org.pawprint_tagger.bridge";
  class TransferManager {
    constructor(browser, booru, progress = () => {}) {
      this.browser = browser;
      this.booru = booru;
      this.progress = progress;
      this.busy = new Map();
      this.importQueue = Promise.resolve();
      this.records = [];
      this.ready = browser.storage.local.get("transfers").then(data => {
        this.records = Array.isArray(data.transfers) ? this.keepRecent(data.transfers) : [];
      });
      this.ready.then(() => {
        for (const record of this.records.filter(item => item.status === "downloading")) {
          this.waitForDownload(record.id).then(() => this.importRecord(record)).catch(async error => {
            if (record.status === "downloading") {
              record.status = "download-failed";
              record.error = error.message;
              await this.save(record);
            }
          });
        }
      }).catch(() => {});
    }

    async ping() {
      try {
        const reply = await this.browser.runtime.sendNativeMessage(host, { version: 1, action: "ping" });
        if (!reply?.ok) throw new Error(reply?.error || "The desktop bridge is unavailable.");
        return reply;
      } catch (error) {
        throw new Error(`Pawprint Tagger is not connected. Run its --install-firefox-bridge command, then retry. ${error.message}`);
      }
    }

    async save(record) {
      if (!record.private) {
        const publicRecords = this.records.filter(item => !item.private);
        await this.browser.storage.local.set({ transfers: this.keepRecent(publicRecords) });
      }
    }

    keepRecent(records) {
      return records.filter((entry, index) => index < 20 || ["downloading", "needs-import"].includes(entry.status));
    }

    async list() { await this.ready; return this.records.slice(0, 20); }

    async add(post, tab) {
      await this.ready;
      post = this.booru.validate(post);
      const key = `${post.site}:${post.postId}:${tab.cookieStoreId || "default"}:${Boolean(tab.incognito)}`;
      if (this.busy.has(key)) return this.busy.get(key);
      const work = this.addOnce(post, tab).finally(() => this.busy.delete(key));
      this.busy.set(key, work);
      return work;
    }

    async addOnce(post, tab) {
      await this.ping();
      const pending = this.records.find(record => record.status === "needs-import" && record.post.pageUrl === post.pageUrl &&
        record.post.originalUrl === post.originalUrl && record.private === Boolean(tab.incognito));
      if (pending) return this.retry(pending.id);
      this.progress(tab.id, "Downloading original…");
      const name = this.booru.downloadName(post);
      const options = { url: post.originalUrl, filename: name.path, conflictAction: "uniquify", saveAs: false,
        headers: [{ name: "Referer", value: post.pageUrl }], incognito: Boolean(tab.incognito) };
      if (tab.cookieStoreId) options.cookieStoreId = tab.cookieStoreId;
      const id = await this.browser.downloads.download(options);
      const record = { id, post, fileName: name.display, private: Boolean(tab.incognito),
        status: "downloading", addedAt: new Date().toISOString(), error: null };
      this.records.unshift(record);
      // Keep pending jobs, plus recent completed jobs. Private jobs stay in memory only.
      this.records = this.keepRecent(this.records);
      await this.save(record);
      try {
        await this.waitForDownload(id);
      } catch (error) {
        record.status = "download-failed";
        record.error = error.message;
        await this.save(record);
        throw error;
      }
      this.progress(tab.id, "Adding image and tags to Pawprint Tagger…");
      return this.importRecord(record);
    }

    waitForDownload(id) {
      return new Promise((resolve, reject) => {
        let done = false;
        const finish = (error, item) => {
          if (done) return;
          done = true;
          clearTimeout(timeout);
          this.browser.downloads.onChanged.removeListener(changed);
          this.browser.downloads.onErased.removeListener(erased);
          error ? reject(error) : resolve(item);
        };
        const check = async () => {
          try {
            const [item] = await this.browser.downloads.search({ id });
            if (!item) return finish(new Error("The download was removed before it could be imported."));
            if (item.state === "complete") return finish(null, item);
            if (item.state === "interrupted") return finish(new Error(`The original-image download was interrupted: ${item.error || "cancelled"}.`));
          } catch (error) { finish(error); }
        };
        const changed = delta => { if (delta.id === id) void check(); };
        const erased = removed => { if (removed === id) finish(new Error("The download was removed.")); };
        const timeout = setTimeout(() => finish(new Error("The download is taking too long. Finish it in Firefox and use Retry import.")), 15 * 60 * 1000);
        this.browser.downloads.onChanged.addListener(changed);
        this.browser.downloads.onErased.addListener(erased);
        // Covers downloads that completed before the listener could be registered.
        void check();
      });
    }

    importRecord(record) {
      // Closed-app native hosts each open the library; serialize them to avoid competing writers.
      const work = this.importQueue.then(() => this.importOne(record));
      this.importQueue = work.catch(() => {});
      return work;
    }

    async importOne(record) {
      try {
        const [item] = await this.browser.downloads.search({ id: record.id });
        if (!item || item.state !== "complete" || item.exists === false || !item.filename) {
          throw new Error("Finish this download in Firefox before retrying. Keep its downloaded file until import succeeds.");
        }
        const reply = await this.browser.runtime.sendNativeMessage(host, { version: 1, action: "import",
          filePath: item.filename, fileName: record.fileName, post: record.post });
        if (!reply?.ok) throw new Error(reply?.error || "The desktop import failed.");
        record.status = "imported";
        record.error = null;
        record.libraryFolder = reply.libraryFolder;
        record.tagCount = reply.tagCount;
        await this.save(record);
        return reply;
      } catch (error) {
        record.status = "needs-import";
        record.error = error.message;
        await this.save(record);
        throw new Error(`The original was downloaded, but import failed: ${error.message} Use Retry import in the extension popup.`);
      }
    }

    async retry(id) {
      await this.ready;
      const record = this.records.find(record => record.id === id);
      if (!record) throw new Error("This download is no longer in the extension's recent imports.");
      return this.importRecord(record);
    }
  }
  globalThis.PawprintTransfers = { TransferManager };
  if (typeof module !== "undefined") module.exports = { TransferManager };
})();
