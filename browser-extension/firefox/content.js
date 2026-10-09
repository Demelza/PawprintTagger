/* global browser, PawprintBooru */
"use strict";
(() => {
  let ui = null;
  let timer;
  async function post() { return PawprintBooru.extract(document, location.href); }

  function mount() {
    const info = PawprintBooru.pageInfo(location.href);
    if (!info) { ui?.host.remove(); ui = null; return; }
    if (ui?.host.isConnected && ui.pageUrl === info.pageUrl) return;
    ui?.host.remove();
    const link = PawprintBooru.originalLink(document, info);
    const options = document.querySelector("#post-options ul, #tag-list, #sidebar, aside");
    if (!link && !options) return;
    const line = link?.closest("li");
    const host = document.createElement(line || options?.tagName === "UL" ? "li" : "div");
    const surface = document.createElement("div");
    host.append(surface);
    const shadow = surface.attachShadow({ mode: "closed" });
    const style = document.createElement("style");
    style.textContent = ":host{display:block;margin:.5em 0}button{font:inherit;cursor:pointer;padding:.4em .6em;max-width:100%}" +
      "button:disabled{cursor:wait}p{font:inherit;font-size:.9em;margin:.4em 0;overflow-wrap:anywhere}";
    const button = document.createElement("button");
    button.type = "button";
    button.textContent = "Add to Pawprint Tagger";
    const status = document.createElement("p");
    status.setAttribute("role", "status");
    status.setAttribute("aria-live", "polite");
    shadow.append(style, button, status);
    ui = { host, pageUrl: info.pageUrl, button, status };
    button.addEventListener("click", async () => {
      const current = ui;
      current.button.disabled = true;
      current.status.textContent = "Reading original image and tags…";
      try {
        const data = await post();
        const result = await browser.runtime.sendMessage({ type: "pawprintImport", post: data });
        if (!result?.ok) throw new Error(result?.error || "The import failed.");
        current.button.textContent = "Added to Pawprint Tagger ✓";
        current.status.textContent = `${result.tagCount} tags added${result.status === "updated" ? " to the existing image" : ""}.`;
      } catch (error) { current.status.textContent = error.message; }
      finally { current.button.disabled = false; }
    });
    if (line) line.after(host);
    else if (link) link.parentElement.after(host);
    else options.append(host);
  }

  browser.runtime.onMessage.addListener((message, sender) => {
    if (sender.id !== browser.runtime.id) return undefined;
    if (message.type === "pawprintProgress") { if (ui) ui.status.textContent = message.text; return undefined; }
    if (message.type === "pawprintExtract") return post().then(data => ({ ok: true, post: data }), error => ({ ok: false, error: error.message }));
    return undefined;
  });
  mount();
  new MutationObserver(() => { clearTimeout(timer); timer = setTimeout(mount, 150); })
    .observe(document.documentElement, { childList: true, subtree: true });
})();
