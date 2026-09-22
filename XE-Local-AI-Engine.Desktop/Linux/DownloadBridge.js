(() => {
  let nonce = __XE_NONCE__;
  if (window !== window.top || !window.webkit?.messageHandlers?.postAvWebViewMessage) return 'unavailable';
  if (globalThis.__xeSaveBridge) { globalThis.__xeSaveBridge.reset(nonce); return 'installed'; }
  const post = value => window.webkit.messageHandlers.postAvWebViewMessage.postMessage(JSON.stringify(value));
  const frames = () => document.querySelector('iframe,frame,object,embed') !== null;
  if (frames()) return 'unavailable';
  new MutationObserver(records => {
    const inserted = records.some(record => Array.from(record.addedNodes).some(node =>
      node instanceof Element && (node.matches('iframe,frame,object,embed') || node.querySelector('iframe,frame,object,embed'))));
    if (frames() || inserted) post({ kind: 'xe-frame-blocked', nonce });
  }).observe(document, { childList: true, subtree: true });
  const originalCreate = URL.createObjectURL.bind(URL);
  const originalRevoke = URL.revokeObjectURL.bind(URL);
  const originalClick = HTMLAnchorElement.prototype.click;
  const blobs = new Map();
  let pending = null;
  URL.createObjectURL = blob => {
    const url = originalCreate(blob);
    if (blobs.size >= 256) blobs.delete(blobs.keys().next().value);
    if (blob.size <= 50 * 1024 * 1024) blobs.set(url, blob);
    return url;
  };
  URL.revokeObjectURL = url => {
    if (pending?.url === url) return;
    blobs.delete(url);
    originalRevoke(url);
  };
  const release = id => {
    if (!pending || pending.id !== id) return false;
    clearTimeout(pending.timer);
    blobs.delete(pending.url);
    originalRevoke(pending.url);
    pending = null;
    return true;
  };
  const intercept = anchor => {
    if (!anchor.hasAttribute('download') || anchor.protocol !== 'blob:') return false;
    const url = anchor.href;
    const blob = blobs.get(url);
    if (pending) return true;
    if (!blob || new URL(url).origin !== location.origin || blob.size > 50 * 1024 * 1024) {
      post({ kind: 'xe-save-unavailable', nonce });
      return true;
    }
    const id = crypto.randomUUID();
    const filename = anchor.download;
    pending = { id, url, timer: setTimeout(() => {
      try { post({ kind: 'xe-save-cancel', nonce, id }); } catch {} finally { release(id); }
    }, 90000) };
    // Keep the Blob URL alive while hashing and until native terminal acknowledgment.
    blob.arrayBuffer().then(bytes => crypto.subtle.digest('SHA-256', bytes)).then(hash => {
      if (pending?.id !== id) return;
      post({ kind: 'xe-save', nonce, id, url, filename,
        size: blob.size, sha256: Array.from(new Uint8Array(hash), value => value.toString(16).padStart(2, '0')).join('') });
    }).catch(() => release(id));
    return true;
  };
  HTMLAnchorElement.prototype.click = function() {
    if (!intercept(this)) return Reflect.apply(originalClick, this, []);
  };
  document.addEventListener('click', event => {
    const anchor = event.target instanceof Element ? event.target.closest('a') : null;
    if (anchor && intercept(anchor)) { event.preventDefault(); event.stopImmediatePropagation(); }
  }, true);
  globalThis.__xeSaveBridge = { finish: (replyNonce, id) => replyNonce === nonce && release(id),
    reset: value => { if (pending) release(pending.id); nonce = value; } };
  window.addEventListener('pagehide', () => { if (pending) release(pending.id); }, { once: true });
  return 'installed';
})()
