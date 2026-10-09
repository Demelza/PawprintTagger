"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const { parseHTML } = require("linkedom");
const fs = require("node:fs");
const vm = require("node:vm");
const path = require("node:path");
const booru = require("../firefox/booru.js");
const danPage = "https://danbooru.donmai.us/posts/123";
const gelPage = "https://gelbooru.com/index.php?page=post&s=view&id=456";
const hash = "0123456789abcdef0123456789abcdef";
const original = `https://cdn.donmai.us/original/aa/bb/${hash}.png`;
const danHtml = `<html><body><section class="image-container" data-id="123" data-file-url="${original}"
 data-tags="blonde_hair example_artist fate/stay_night 初音ミク highres">
 <img id="image" src="https://cdn.donmai.us/sample/aa/bb/sample-${hash}.jpg"></section>
 <section id="tag-list"><ul>
 <li class="category-0" data-tag-name="blonde_hair"><a>blonde hair</a></li>
 <li class="category-1" data-tag-name="example_artist"><a>example artist</a></li>
 <li class="category-3" data-tag-name="fate/stay_night"><a>fate/stay night</a></li>
 <li class="category-4" data-tag-name="初音ミク"><a>初音ミク</a></li>
 <li class="category-5" data-tag-name="highres"><a>highres</a></li></ul></section>
 <section id="post-options"><ul><li><a class="image-view-original-link" href="${original}">View original</a></li></ul></section>
 </body></html>`;
const gelHtml = `<html><body><aside id="sidebar"><ul>
 <li class="tag-type-general"><span><a href="index.php?page=wiki&amp;s=list&amp;search=blonde_hair">?</a></span>
 <a href="index.php?page=post&amp;s=list&amp;tags=blonde_hair">blonde hair</a><span>12345</span></li>
 <li class="tag-type-artist"><a href="index.php?page=post&amp;s=list&amp;tags=artist_name">artist name</a></li>
 <li class="tag-type-metadata"><a href="index.php?page=post&amp;s=list&amp;tags=highres">highres</a></li>
 <li><a href="https://img4.gelbooru.com/images/aa/bb/${hash}.png">Original image</a></li></ul></aside>
 <section class="image-container" data-id="456" data-tags="blonde_hair artist_name highres">
 <img id="image" src="https://img4.gelbooru.com/samples/aa/sample_${hash}.jpg"></section></body></html>`;

test("Only individual supported post URLs are recognized", () => {
  assert.equal(booru.pageInfo(danPage).postId, "123");
  assert.equal(booru.pageInfo(gelPage).site, "gelbooru");
  for (const value of ["https://danbooru.donmai.us/posts?tags=test", "https://gelbooru.com/index.php?page=post&s=list&id=456",
    "https://danbooru.donmai.us.attacker.example/posts/123", "http://danbooru.donmai.us/posts/123", "https://danbooru.donmai.us/posts/123/edit"])
    assert.equal(booru.pageInfo(value), null);
});

test("Danbooru original metadata wins over the displayed sample and preserves all five categories", () => {
  const { document } = parseHTML(danHtml);
  const post = booru.fromDocument(document, danPage);
  assert.equal(post.originalUrl, original);
  assert.equal(post.md5, hash);
  assert.equal(post.tags.length, 5);
  assert.deepEqual(post.tags.map(tag => tag.category), ["general", "artist", "copyright", "character", "meta"]);
  assert.equal(post.tags[2].name, "fate/stay_night");
  assert.equal(post.tags[3].name, "初音ミク");
});

test("Gelbooru reads the Original image link, raw tag queries, categories and no counts/wiki labels", () => {
  const post = booru.fromDocument(parseHTML(gelHtml).document, gelPage);
  assert.equal(post.originalUrl, `https://img4.gelbooru.com/images/aa/bb/${hash}.png`);
  assert.deepEqual(post.tags, [
    { name: "blonde_hair", category: "general" }, { name: "artist_name", category: "artist" }, { name: "highres", category: "meta" }
  ]);
});

test("Collapsed sidebar tags remain included through image-container metadata", () => {
  const { document } = parseHTML(danHtml);
  document.querySelector("#tag-list").remove();
  assert.equal(booru.fromDocument(document, danPage).tags.length, 5);
});

test("Missing originals, sample-only URLs, hostile URL schemes and video originals fail explicitly", () => {
  const { document } = parseHTML(danHtml);
  const post = booru.fromDocument(document, danPage);
  for (const originalUrl of ["javascript:alert(1)", "file:///etc/passwd", "https://attacker.example/image.png",
    `https://cdn.donmai.us/sample/a/sample-${hash}.jpg`, "https://cdn.donmai.us/original/movie.webm"])
    assert.throws(() => booru.validate({ ...post, originalUrl }));
  document.querySelector(".image-container").removeAttribute("data-file-url");
  document.querySelector(".image-view-original-link").remove();
  assert.throws(() => booru.fromDocument(document, danPage), /original image link/);
});

test("Danbooru JSON fallback uses file_url rather than large_file_url or preview_file_url", async () => {
  const { document } = parseHTML(danHtml);
  document.querySelector(".image-container").removeAttribute("data-file-url");
  document.querySelector(".image-view-original-link").remove();
  const json = { id: 123, file_url: original, large_file_url: "https://cdn.donmai.us/sample/a.jpg", md5: hash,
    tag_string: "blonde_hair artist", tag_string_general: "blonde_hair", tag_string_artist: "artist" };
  const result = await booru.extract(document, danPage, async (url, options) => {
    assert.equal(url, `${danPage}.json`);
    assert.equal(options.credentials, "same-origin");
    return { ok: true, json: async () => json };
  });
  assert.equal(result.originalUrl, original);
  assert.equal(result.tags[1].category, "artist");
  assert.throws(() => booru.fromDanbooruApi({ ...json, id: 124 }, danPage), /different post/);
});

test("Stale page metadata is rejected during post navigation", () => {
  assert.throws(() => booru.fromDocument(parseHTML(danHtml).document, "https://danbooru.donmai.us/posts/124"), /still loading/);
});

test("Download filenames stay relative, recognizable and collision safe", () => {
  const post = booru.fromDocument(parseHTML(danHtml).document, danPage);
  const name = booru.downloadName(post);
  assert.equal(name.path, `PawprintTagger/danbooru-123-${hash}.png`);
  assert.equal(name.display, "danbooru-123.png");
});

test("The page action is inserted under the original link and imports extracted original metadata", async () => {
  const { window, document } = parseHTML(danHtml);
  const createdButtons = [];
  const create = document.createElement.bind(document);
  document.createElement = tag => { const node = create(tag); if (tag === "button") createdButtons.push(node); return node; };
  let request;
  const browser = { runtime: { id: "test", onMessage: { addListener() {} }, sendMessage: async message => {
    request = message;
    return { ok: true, status: "imported", tagCount: 5 };
  } } };
  const context = { document, location: { href: danPage }, browser, PawprintBooru: booru,
    MutationObserver: class { observe() {} }, setTimeout, clearTimeout, URL, console };
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, "../firefox/content.js"), "utf8"), context);
  assert.equal(createdButtons.length, 1);
  assert.equal(document.querySelector(".image-view-original-link").closest("li").nextElementSibling.tagName, "LI");
  createdButtons[0].dispatchEvent(new window.Event("click"));
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(request.post.originalUrl, original);
  assert.match(createdButtons[0].textContent, /Added to Pawprint Tagger/);
});

module.exports = { danHtml, gelHtml, danPage, gelPage, original };
