"use strict";
(() => {
  const transcript = document.getElementById("transcript");
  const empty = document.getElementById("empty");
  const entries = new Map();
  let generation = -1;
  let revision = -1;
  let following = true;
  let scrollFrame = 0;
  window.addEventListener("scroll", () => {
    following = document.documentElement.scrollHeight - window.innerHeight - window.scrollY < 48;
  }, { passive: true });
  window.addEventListener("wheel", event => { if (event.deltaY < 0) following = false; }, { passive: true });
  document.addEventListener("dragover", event => event.preventDefault());
  document.addEventListener("drop", event => event.preventDefault());
  document.addEventListener("click", event => {
    const anchor = event.target.closest?.("a");
    if (!anchor) return;
    event.preventDefault();
    if (event.isTrusted && anchor.hasAttribute("href")) {
      chrome.webview.postMessage({ type: "link", href: anchor.getAttribute("href") });
    }
  });
  hljs.configure({ throwUnescapedHTML: true });
  chrome.webview.addEventListener("message", event => {
    const patch = event.data;
    if (!patch || !Number.isSafeInteger(patch.generation) || !Number.isSafeInteger(patch.revision) ||
        !Array.isArray(patch.messages) || patch.generation < generation ||
        (patch.generation === generation && patch.revision <= revision)) return;
    const reset = patch.generation !== generation;
    const shouldFollow = reset || following;
    const anchor = shouldFollow ? null : [...transcript.children].find(element => element.getBoundingClientRect().bottom > 0);
    const anchorTop = anchor?.getBoundingClientRect().top;
    if (reset) { transcript.replaceChildren(); entries.clear(); following = true; }
    generation = patch.generation;
    revision = patch.revision;
    document.documentElement.dataset.theme = patch.dark ? "dark" : "light";
    for (const message of patch.messages) {
      let entry = entries.get(message.id);
      if (!entry) {
        const element = document.createElement("article");
        element.className = message.isUser ? "message user" : "message assistant";
        const speaker = document.createElement("div"); speaker.className = "speaker";
        const body = document.createElement("div"); body.className = "body";
        const detail = document.createElement("div"); detail.className = "detail";
        const image = document.createElement("img"); image.className = "attachment"; image.alt = "送信した画像"; image.hidden = true;
        image.addEventListener("load", () => { if (following) window.scrollTo(0, document.documentElement.scrollHeight); });
        element.append(speaker, image, body, detail);
        entry = { element, speaker, body, detail, image, html: null, imageUrl: null };
        entries.set(message.id, entry);
        transcript.append(element);
      }
      entry.speaker.textContent = message.speaker;
      entry.detail.textContent = message.detail;
      const imageUrl = typeof message.imageDataUrl === "string" && message.imageDataUrl.startsWith("data:image/png;base64,")
        ? message.imageDataUrl : null;
      if (entry.imageUrl !== imageUrl) {
        entry.imageUrl = imageUrl;
        entry.image.hidden = !imageUrl;
        if (imageUrl) entry.image.src = imageUrl;
        else entry.image.removeAttribute("src");
      }
      if (entry.html !== message.html) {
        // HTML は Desktop の Markdig + HtmlSanitizer が生成した断片だけを受け取る。
        entry.body.innerHTML = message.html;
        entry.html = message.html;
      }
      if (message.final) {
        for (const code of entry.body.querySelectorAll("pre code:not([data-highlighted])")) {
          const language = [...code.classList].find(name => name.startsWith("language-"))?.slice(9);
          if (language && hljs.getLanguage(language)) {
            try { hljs.highlightElement(code); } catch { /* 本文の可読性を維持する。 */ }
          }
        }
      }
    }
    if (Array.isArray(patch.order)) {
      const keep = new Set(patch.order);
      for (const [id, entry] of entries) if (!keep.has(id)) { entry.element.remove(); entries.delete(id); }
      patch.order.forEach((id, index) => {
        const entry = entries.get(id);
        if (entry && transcript.children[index] !== entry.element)
          transcript.insertBefore(entry.element, transcript.children[index] ?? null);
      });
    }
    empty.hidden = entries.size !== 0;
    cancelAnimationFrame(scrollFrame);
    scrollFrame = requestAnimationFrame(() => {
      if (shouldFollow) { window.scrollTo(0, document.documentElement.scrollHeight); following = true; }
      else if (anchor?.isConnected) window.scrollBy(0, anchor.getBoundingClientRect().top - anchorTop);
    });
  });
  chrome.webview.postMessage({ type: "ready" });
})();
