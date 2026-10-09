"use strict";
const fs = require("node:fs");
const path = require("node:path");
const { execFileSync } = require("node:child_process");
const root = path.resolve(__dirname, "..");
const output = path.resolve(root, "../artifacts/firefox");
const manifest = JSON.parse(fs.readFileSync(path.join(root, "firefox/manifest.json"), "utf8"));
fs.mkdirSync(output, { recursive: true });
execFileSync(process.execPath, [path.join(root, "node_modules/web-ext/bin/web-ext.js"), "build",
  "--source-dir", path.join(root, "firefox"), "--artifacts-dir", output, "--overwrite-dest"], { stdio: "inherit" });
const built = path.join(output, `pawprint_tagger-${manifest.version}.zip`);
const xpi = path.join(output, `PawprintTaggerFirefox-${manifest.version}.xpi`);
fs.copyFileSync(built, xpi);
console.log(`Firefox development package: ${xpi}`);
