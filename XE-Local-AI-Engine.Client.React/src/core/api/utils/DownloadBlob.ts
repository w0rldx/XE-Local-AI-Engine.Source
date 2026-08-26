// Saves bytes already in memory to the user's disk. Operator-gated endpoints cannot be reached by a bare `<a href>`
// (it carries no bearer token), so every authenticated download fetches the body through the shared axios instance
// first and then clicks a synthetic anchor over an object URL — no second request, no token in a URL.

/** Triggers a browser download of `blob` under `fileName`. */
export function saveBlob(blob: Blob, fileName: string): void {
	const url = URL.createObjectURL(blob);
	const anchor = document.createElement("a");
	anchor.href = url;
	anchor.download = fileName;
	document.body.append(anchor);
	anchor.click();
	anchor.remove();
	// Revoked on the NEXT tick rather than synchronously after `click()`: the click only QUEUES the download, and
	// revoking in the same task can pull the object URL out from under a browser that has not taken the bytes yet —
	// the download then silently does nothing, with no error anywhere. Chrome tolerates the synchronous revoke;
	// Firefox and Safari are the ones this protects.
	setTimeout(() => {
		URL.revokeObjectURL(url);
	}, 0);
}

/**
 * The server-chosen file name out of a `Content-Disposition` header, or null when the header is absent or names none.
 *
 * The value lands on `<a download>`, so it is reduced to a single path-free segment: a server (or a proxy) offering
 * `filename="../etc/passwd"` must never steer where the browser writes. Only the RFC 6266 `filename` parameter is read
 * — `filename*` (RFC 5987 percent-encoded) is not emitted by this backend, so parsing it would be dead code.
 */
export function fileNameFromContentDisposition(header: unknown): string | null {
	if (typeof header !== "string") {
		return null;
	}
	const match = /filename\s*=\s*"([^"]+)"|filename\s*=\s*([^;]+)/i.exec(header);
	const raw = (match?.[1] ?? match?.[2] ?? "").trim();
	const segment = raw.split(/[/\\]/).pop() ?? "";
	return segment.length > 0 && segment !== "." && segment !== ".." ? segment : null;
}
