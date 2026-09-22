/*
 * XE native export bridge — evaluated in the WebKitGTK page by GtkDesktopBridge.ArmAsync (Linux native shell).
 *
 * Purpose: keep an in-page export inside the native shell. The SPA builds a Blob, hands it to
 * URL.createObjectURL and clicks an <a download>. This script owns that click: it looks the Blob up, hashes
 * it, and asks the native side to run the save through a GTK file picker instead of letting WebKit download
 * it on its own. It also reports any frame in the document, because the native document policy forbids them.
 *
 * Outbound messages — JSON, sent through window.webkit.messageHandlers.postAvWebViewMessage and parsed by
 * GtkDesktopBridge.ReceiveMessage, which drops anything whose nonce does not match the armed one:
 *   { kind: 'xe-save',             nonce, id, url, filename, size, sha256 }  export ready; validated by GtkSaveIntent.Parse
 *   { kind: 'xe-save-cancel',      nonce, id }                               the page-side save timed out
 *   { kind: 'xe-save-unavailable', nonce }                                   the click cannot become an export
 *   { kind: 'xe-frame-blocked',    nonce }                                   a frame exists or was just inserted
 *
 * Inbound calls, made from C# through NativeWebView.InvokeScript:
 *   globalThis.__xeSaveBridge.finish(nonce, id)  terminal native acknowledgment: release the Blob held for `id`
 *   globalThis.__xeSaveBridge.reset(nonce)       re-arm on the same document: drop the pending save, adopt the new nonce
 *
 * Return value — ArmAsync accepts exactly 'installed' and treats everything else as a failure:
 *   'installed'   the bridge is live in this document (also when an already installed bridge was re-armed)
 *   'unavailable' this document cannot host the bridge (sub-frame, no message handler, or a frame is present)
 *
 * __XE_NONCE__ is substituted with a JSON string literal by GtkDesktopBridge.ArmAsync before evaluation.
 * MAX_BLOB_BYTES and SAVE_TIMEOUT_MS must stay equal to their C# counterparts, GtkSaveIntent.MaximumBytes and
 * GtkDesktopBridge.SaveTimeout: the native side re-checks both and a larger or slower export is rejected there,
 * not here. LinuxDesktopPolicyTests pins all three constants. Plain ES2020: no build step, no dependencies.
 */
(() => {
  // ---- Guard: only the top document, with a live Avalonia message handler, can host the bridge.
  if (window !== window.top || !window.webkit?.messageHandlers?.postAvWebViewMessage) { return 'unavailable'; }

  // ---- Config
  const MAX_BLOB_BYTES = 50 * 1024 * 1024; // Equals GtkSaveIntent.MaximumBytes, which rejects a larger export.
  const MAX_CACHED_BLOBS = 256; // Page-side memory bound for the Blob registry; no C# counterpart.
  const SAVE_TIMEOUT_MS = 90000; // Equals GtkDesktopBridge.SaveTimeout, which cancels the native save.
  const FRAME_SELECTOR = 'iframe,frame,object,embed';
  let nonce = __XE_NONCE__;

  // ---- Re-entry: arming the same document again re-uses the installed bridge and adopts the fresh nonce.
  if (globalThis.__xeSaveBridge) {
    globalThis.__xeSaveBridge.reset(nonce);
    return 'installed';
  }

  // ---- Transport: outbound messages are fire-and-forget, and a torn-down handler must never abort a caller.
  const postSafely = message => {
    try {
      window.webkit.messageHandlers.postAvWebViewMessage.postMessage(JSON.stringify(message));
    } catch { /* The page-side flow still has to run to completion without the native side. */ }
  };

  // ---- Frame guard: the native document policy forbids frames, so report one instead of exporting from it.
  const hasFrame = () => document.querySelector(FRAME_SELECTOR) !== null;
  if (hasFrame()) { return 'unavailable'; }
  const insertsFrame = mutations => mutations.some(mutation => Array.from(mutation.addedNodes).some(
    node => node instanceof Element && (node.matches(FRAME_SELECTOR) || node.querySelector(FRAME_SELECTOR) !== null)));
  new MutationObserver(mutations => {
    if (hasFrame() || insertsFrame(mutations)) { postSafely({ kind: 'xe-frame-blocked', nonce }); }
  }).observe(document, { childList: true, subtree: true });

  // ---- Blob registry: remember the Blob behind every object URL this document creates, oldest evicted first.
  const createObjectUrl = URL.createObjectURL.bind(URL);
  const revokeObjectUrl = URL.revokeObjectURL.bind(URL);
  const cachedBlobs = new Map();
  URL.createObjectURL = blob => {
    const url = createObjectUrl(blob);
    if (cachedBlobs.size >= MAX_CACHED_BLOBS) { cachedBlobs.delete(cachedBlobs.keys().next().value); }
    if (blob.size <= MAX_BLOB_BYTES) { cachedBlobs.set(url, blob); }
    return url;
  };
  URL.revokeObjectURL = url => {
    if (pendingSave?.url === url) { return; } // The page revokes right after the click; the save still needs the Blob.
    cachedBlobs.delete(url);
    revokeObjectUrl(url);
  };

  // ---- Pending-save state: one export at a time, its Blob URL kept alive until the native side acknowledges.
  let pendingSave = null; // { id, url, timer } while a save is in flight, otherwise null.
  const releasePendingSave = id => {
    if (pendingSave === null || pendingSave.id !== id) { return false; }
    clearTimeout(pendingSave.timer);
    cachedBlobs.delete(pendingSave.url);
    revokeObjectUrl(pendingSave.url);
    pendingSave = null;
    return true;
  };

  // ---- Download interception: an <a download> aimed at one of our Blob URLs becomes a native save intent.
  const isBlobDownload = anchor => anchor.hasAttribute('download') && anchor.protocol === 'blob:';
  const rejectSave = () => {
    postSafely({ kind: 'xe-save-unavailable', nonce });
    return true;
  };

  const beginSave = (blob, url, filename) => {
    const id = crypto.randomUUID();
    const timer = setTimeout(() => {
      postSafely({ kind: 'xe-save-cancel', nonce, id });
      releasePendingSave(id);
    }, SAVE_TIMEOUT_MS);
    pendingSave = { id, url, timer };
    // Keep the Blob URL alive while hashing and until native terminal acknowledgment.
    blob.arrayBuffer()
      .then(bytes => crypto.subtle.digest('SHA-256', bytes))
      .then(digest => {
        if (pendingSave?.id !== id) { return; }
        const sha256 = Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, '0')).join('');
        postSafely({ kind: 'xe-save', nonce, id, url, filename, size: blob.size, sha256 });
      })
      .catch(() => releasePendingSave(id));
  };

  // True when this click was consumed, and the caller must suppress the browser's own download.
  const intercept = anchor => {
    if (!isBlobDownload(anchor)) { return false; }
    if (pendingSave !== null) { return rejectSave(); } // A second export click while a save is still in flight.
    const url = anchor.href;
    const blob = cachedBlobs.get(url);
    if (blob === undefined) { return rejectSave(); } // Not created by this document, or already evicted.
    if (new URL(url).origin !== location.origin) { return rejectSave(); }
    if (blob.size > MAX_BLOB_BYTES) { return rejectSave(); }
    beginSave(blob, url, anchor.download);
    return true;
  };

  const originalAnchorClick = HTMLAnchorElement.prototype.click;
  HTMLAnchorElement.prototype.click = function () {
    if (!intercept(this)) { Reflect.apply(originalAnchorClick, this, []); }
  };
  document.addEventListener('click', event => {
    const anchor = event.target instanceof Element ? event.target.closest('a') : null;
    if (anchor !== null && intercept(anchor)) {
      event.preventDefault();
      event.stopImmediatePropagation();
    }
  }, true);

  // ---- Public native bridge: the only surface C# calls into.
  globalThis.__xeSaveBridge = {
    finish: (replyNonce, id) => replyNonce === nonce && releasePendingSave(id),
    reset: freshNonce => {
      if (pendingSave !== null) { releasePendingSave(pendingSave.id); }
      nonce = freshNonce;
    },
  };

  // ---- Lifecycle: leaving the document drops the pending save so its Blob URL is not leaked.
  window.addEventListener('pagehide', () => {
    if (pendingSave !== null) { releasePendingSave(pendingSave.id); }
  }, { once: true });
  return 'installed';
})()
