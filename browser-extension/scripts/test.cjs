"use strict";
const fs = require("node:fs");
const path = require("node:path");
const { spawnSync } = require("node:child_process");
const tests = path.resolve(__dirname, "../tests");
let failed = false;
for (const file of fs.readdirSync(tests).filter(name => name.endsWith(".test.cjs")).sort()) {
  const result = spawnSync(process.execPath, [path.join(tests, file)], { stdio: "inherit" });
  if (result.error) console.error(result.error.message);
  if (result.status !== 0) failed = true;
}
process.exitCode = failed ? 1 : 0;
