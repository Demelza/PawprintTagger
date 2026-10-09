/* global browser, PawprintBooru, PawprintTransfers */
"use strict";
const transfers = new PawprintTransfers.TransferManager(browser, PawprintBooru, (tabId, text) => {
  browser.tabs.sendMessage(tabId, { type: "pawprintProgress", text }).catch(() => {});
});

browser.runtime.onMessage.addListener((message, sender) => {
  if (sender.id !== browser.runtime.id) return undefined;
  const popup = sender.url?.startsWith(browser.runtime.getURL("")) && !sender.tab;
  const run = async () => {
    switch (message?.type) {
      case "pawprintImport": {
        if (!sender.tab || sender.frameId !== 0) throw new Error("Imports must originate from a booru post page.");
        const info = PawprintBooru.pageInfo(sender.tab.url || sender.url);
        if (!info || info.pageUrl !== message.post?.pageUrl) throw new Error("The post changed before its import started.");
        return transfers.add(message.post, sender.tab);
      }
      case "pawprintPopupImport": {
        if (!popup) throw new Error("Open the extension popup to add the current post.");
        const [tab] = await browser.tabs.query({ active: true, currentWindow: true });
        if (!tab || !PawprintBooru.pageInfo(tab.url)) throw new Error("Open an individual Danbooru or Gelbooru image post first.");
        const response = await browser.tabs.sendMessage(tab.id, { type: "pawprintExtract" });
        if (!response?.ok) throw new Error(response?.error || "Reload the post page so the extension can read its tags.");
        return transfers.add(response.post, tab);
      }
      case "pawprintTransfers":
        if (!popup) throw new Error("Only the extension popup can read recent imports.");
        return { ok: true, records: await transfers.list() };
      case "pawprintRetry":
        if (!popup) throw new Error("Only the extension popup can retry an import.");
        return transfers.retry(message.id);
      case "pawprintPing":
        if (!popup) throw new Error("Only the extension popup can check the desktop bridge.");
        return transfers.ping();
      default: return undefined;
    }
  };
  return run().catch(error => ({ ok: false, error: error.message }));
});
