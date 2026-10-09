/* global browser */
"use strict";
const status = document.getElementById("status");
const add = document.getElementById("add");
async function request(message) {
  const result = await browser.runtime.sendMessage(message);
  if (!result?.ok) throw new Error(result?.error || "Pawprint Tagger did not respond.");
  return result;
}
async function refresh() {
  const result = await request({ type: "pawprintTransfers" });
  const list = document.getElementById("transfers");
  list.replaceChildren();
  for (const record of result.records) {
    const item = document.createElement("li");
    const title = document.createElement("strong");
    title.textContent = `${record.post.site} #${record.post.postId}`;
    const detail = document.createElement("p");
    detail.textContent = record.status === "imported" ? `Added · ${record.tagCount} tags` :
      record.status === "downloading" ? "Downloading original…" : record.error || "Import pending";
    item.append(title, detail);
    if (record.status !== "imported") {
      const retry = document.createElement("button");
      retry.type = "button";
      retry.textContent = "Retry import";
      retry.addEventListener("click", async () => {
        retry.disabled = true;
        status.textContent = "Retrying import…";
        try { const result = await request({ type: "pawprintRetry", id: record.id }); status.textContent = `Added with ${result.tagCount} tags.`; }
        catch (error) { status.textContent = error.message; }
        finally { await refresh(); }
      });
      item.append(retry);
    }
    list.append(item);
  }
  if (!result.records.length) list.textContent = "No imports yet.";
}
add.addEventListener("click", async () => {
  add.disabled = true;
  status.textContent = "Downloading the original and adding its tags…";
  try { const result = await request({ type: "pawprintPopupImport" }); status.textContent = `Added with ${result.tagCount} tags.`; }
  catch (error) { status.textContent = error.message; }
  finally { add.disabled = false; await refresh(); }
});
document.getElementById("check").addEventListener("click", async () => {
  try { const result = await request({ type: "pawprintPing" }); status.textContent = `Connected · Library: ${result.libraryFolder}`; }
  catch (error) { status.textContent = error.message; }
});
refresh().catch(error => { status.textContent = error.message; });
