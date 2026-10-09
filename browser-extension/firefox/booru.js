/* Shared page adapters. Original URLs are explicit metadata/links, never the displayed <img> sample. */
"use strict";
(() => {
  const categories = { 0: "general", 1: "artist", 3: "copyright", 4: "character", 5: "meta", metadata: "meta", tag: "general" };
  const names = new Set(["general", "artist", "copyright", "character", "meta"]);
  const idPattern = /^[1-9][0-9]{0,15}$/;

  function pageInfo(value) {
    const url = new URL(value);
    if (url.protocol !== "https:" || url.port || url.username || url.password) return null;
    if (["danbooru.donmai.us", "safebooru.donmai.us"].includes(url.hostname)) {
      const match = url.pathname.match(/^\/posts\/([1-9][0-9]{0,15})\/?$/);
      if (match) return { site: "danbooru", postId: match[1], pageUrl: `${url.origin}/posts/${match[1]}` };
    }
    if (["gelbooru.com", "www.gelbooru.com"].includes(url.hostname) && url.pathname === "/index.php" &&
        url.searchParams.get("page") === "post" && url.searchParams.get("s") === "view" && idPattern.test(url.searchParams.get("id") || "")) {
      const id = url.searchParams.get("id");
      return { site: "gelbooru", postId: id, pageUrl: `${url.origin}/index.php?page=post&s=view&id=${id}` };
    }
    return null;
  }

  function originalLink(document, info) {
    if (info.site === "danbooru") {
      return document.querySelector(".image-view-original-link, #post-option-view-original a, #image-download-link, #post-option-download a");
    }
    return Array.from(document.querySelectorAll("a[href]")).find(anchor => /^original(?:\s+image)?$/i.test(anchor.textContent.trim()));
  }

  function categoryFor(row) {
    const data = row.getAttribute("data-tag-category");
    if (data !== null) return categories[data] || (names.has(data) ? data : "general");
    for (const className of row.classList) {
      const match = className.match(/^(?:tag-type|category)-(.+)$/);
      if (match) return categories[match[1]] || (names.has(match[1]) ? match[1] : "general");
    }
    return "general";
  }

  function tagName(row, pageUrl) {
    const data = row.getAttribute("data-tag-name");
    if (data) return data;
    for (const anchor of row.querySelectorAll("a[href]")) {
      try {
        const url = new URL(anchor.getAttribute("href"), pageUrl);
        const value = url.searchParams.get("tags");
        if (value) return value.replace(/^"|"$/g, "").trim().replace(/\s+/g, "_");
      } catch { /* Wiki/help links are not tag search links. */ }
    }
    const search = row.querySelector(".search-tag");
    return search ? search.textContent.trim().replace(/\s+/g, "_") : null;
  }

  function readTags(document, info, container) {
    const result = new Map();
    const root = document.querySelector("#tag-list") || document;
    for (const row of root.querySelectorAll("li[data-tag-name], li[class*='category-'], li[class*='tag-type-']")) {
      const name = tagName(row, info.pageUrl);
      if (name) result.set(name, { name, category: categoryFor(row) });
    }
    // Metadata preserves tags omitted by a collapsed or customized sidebar.
    const raw = container?.getAttribute("data-tags") || document.querySelector("meta[name='post-tags']")?.getAttribute("content") || "";
    for (const name of raw.trim().split(/\s+/).filter(Boolean)) {
      if (!result.has(name)) result.set(name, { name, category: "general" });
    }
    return Array.from(result.values());
  }

  function validate(post) {
    const info = post && pageInfo(post.pageUrl);
    if (!info || info.site !== post.site || info.postId !== post.postId) throw new Error("This is not a supported booru post page.");
    let original;
    try { original = new URL(post.originalUrl); } catch { throw new Error("The original image link is unavailable."); }
    const domain = info.site === "danbooru" ? "donmai.us" : "gelbooru.com";
    if (original.protocol !== "https:" || original.port || original.username || original.password ||
        !(original.hostname === domain || original.hostname.endsWith(`.${domain}`))) throw new Error("The original image link does not belong to this booru.");
    if (/(?:^|\/)(?:samples?|previews?|thumbnails?)(?:\/|[-_])/i.test(decodeURIComponent(original.pathname))) {
      throw new Error("The page only provides a sample or thumbnail. Open a post with an accessible original image.");
    }
    const extension = original.pathname.match(/\.([a-z0-9]+)$/i)?.[1].toLowerCase();
    if (!new Set(["jpg", "jpeg", "png", "gif", "webp", "avif", "bmp", "tif", "tiff", "jxl"]).has(extension)) {
      throw new Error("This post's original is not a supported image file. Videos and animation archives cannot be imported.");
    }
    if (!Array.isArray(post.tags) || post.tags.length === 0 || post.tags.length > 2048 ||
        post.tags.some(tag => !tag || typeof tag.name !== "string" || !tag.name.trim() || tag.name.length > 128 ||
          /[\u0000-\u001f\u007f]/.test(tag.name) || !names.has(tag.category))) throw new Error("The post's tags are missing or invalid.");
    if (post.md5 != null && !/^[a-f0-9]{32}$/i.test(post.md5)) throw new Error("The post's original-image checksum is invalid.");
    return { ...info, originalUrl: original.href, tags: post.tags, md5: post.md5 || null };
  }

  function fromDocument(document, pageUrl) {
    const info = pageInfo(pageUrl);
    if (!info) throw new Error("Open an individual Danbooru or Gelbooru image post first.");
    const container = document.querySelector(".image-container[data-id], #image-container[data-id], .image-container[data-file-url], #image-container");
    const containerId = container?.getAttribute("data-id");
    if (containerId && containerId !== info.postId) throw new Error("The next post is still loading. Try again in a moment.");
    const link = originalLink(document, info);
    const raw = (info.site === "danbooru" ? container?.getAttribute("data-file-url") : null) || link?.getAttribute("href");
    if (!raw) throw new Error("An accessible original image link could not be found. A sample will not be downloaded.");
    const originalUrl = new URL(raw, info.pageUrl).href;
    const hash = decodeURIComponent(new URL(originalUrl).pathname).match(/(?:^|\/|[_-])([a-f0-9]{32})\.[a-z0-9]+$/i)?.[1];
    return validate({ ...info, originalUrl, tags: readTags(document, info, container),
      md5: container?.getAttribute("data-md5") || hash || null });
  }

  function fromDanbooruApi(json, pageUrl) {
    const info = pageInfo(pageUrl);
    if (!info || info.site !== "danbooru" || String(json.id) !== info.postId) throw new Error("Danbooru returned a different post.");
    const tags = new Map();
    for (const category of names) {
      for (const name of String(json[`tag_string_${category}`] || "").split(/\s+/).filter(Boolean)) tags.set(name, { name, category });
    }
    for (const name of String(json.tag_string || "").split(/\s+/).filter(Boolean)) {
      if (!tags.has(name)) tags.set(name, { name, category: "general" });
    }
    return validate({ ...info, originalUrl: json.file_url, md5: json.md5 || null, tags: Array.from(tags.values()) });
  }

  async function extract(document, pageUrl, fetcher = fetch) {
    try { return fromDocument(document, pageUrl); }
    catch (originalError) {
      const info = pageInfo(pageUrl);
      if (!info || info.site !== "danbooru") throw originalError;
      try {
        const response = await fetcher(`${new URL(info.pageUrl).origin}/posts/${info.postId}.json`,
          { credentials: "same-origin", headers: { Accept: "application/json" } });
        if (!response.ok) throw originalError;
        return fromDanbooruApi(await response.json(), pageUrl);
      } catch { throw originalError; }
    }
  }

  function downloadName(post) {
    const url = new URL(post.originalUrl);
    const raw = decodeURIComponent(url.pathname.split("/").pop()).replace(/[^a-z0-9._-]/gi, "_").replace(/^\.+|\.+$/g, "");
    const extension = raw.match(/\.[a-z0-9]+$/i)?.[0] || ".image";
    const base = raw.length > 160 ? raw.slice(0, 150) + extension : raw;
    return { path: `PawprintTagger/${post.site}-${post.postId}-${base}`, display: `${post.site}-${post.postId}${extension}` };
  }

  const api = Object.freeze({ pageInfo, originalLink, validate, fromDocument, fromDanbooruApi, extract, downloadName });
  globalThis.PawprintBooru = api;
  if (typeof module !== "undefined") module.exports = api;
})();
