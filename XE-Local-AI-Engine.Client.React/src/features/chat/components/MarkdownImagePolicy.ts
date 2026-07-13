import { defaultUrlTransform } from "react-markdown";

// Image policy for MODEL-rendered markdown. Model output can embed `![alt](https://tracker.example/pixel.png)`, which
// a naive <img> would fetch on render — leaking the viewer's presence (and referrer) to an attacker-controlled host.
// Remote images are blocked behind an explicit click-to-load consent placeholder (see SafeMarkdownImage); inline
// sources the model or the app legitimately produces (data:image/, blob:, same-origin, api-relative) render directly.

// True when a src points off the app's own origin: an absolute http(s) URL to another origin, or a protocol-relative
// //host URL. data:, blob:, relative, and same-origin absolute sources are local and render without consent.
export function isRemoteImageSrc(src: string): boolean {
	if (src.startsWith("//")) {
		return true; // protocol-relative — resolves to a remote host
	}
	if (/^https?:\/\//i.test(src)) {
		try {
			return new URL(src).origin !== globalThis.location.origin;
		} catch {
			return true; // unparseable absolute URL — treat as remote (fail closed)
		}
	}
	return false;
}

// The origin shown on the consent placeholder so the user knows who they'd be contacting before loading.
export function remoteImageOrigin(src: string): string {
	try {
		return new URL(src, globalThis.location.origin).origin;
	} catch {
		return src;
	}
}

// react-markdown's defaultUrlTransform blocks dangerous schemes (javascript:/file:/vbscript:/…) but ALSO strips
// data: URLs, which legitimate inline images and locally generated image bytes use. Keep the default protection and
// re-allow only image data URLs and blob URLs for the `src` attribute; every other URL (including link hrefs) keeps
// the default secure transform.
export function markdownImageUrlTransform(url: string, key: string): string {
	if (key === "src") {
		const normalized = url.trim().toLowerCase();
		if (normalized.startsWith("data:image/") || normalized.startsWith("blob:")) {
			return url;
		}
	}
	return defaultUrlTransform(url);
}
