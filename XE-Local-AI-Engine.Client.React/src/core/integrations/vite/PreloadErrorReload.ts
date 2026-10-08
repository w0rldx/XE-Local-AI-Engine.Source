// After a node upgrade an open tab still references the old build's chunks, which the upgrade deleted. Vite raises
// `vite:preloadError` when such a chunk (JS or CSS) fails to load. TanStack's route loader reloads on some of these,
// but React.lazy caches the rejected import and every Retry rethrows, and a missing CSS chunk matches neither. One
// reload fetches the new index.html and its chunks. The stamp in sessionStorage stops a reload loop when the chunk is
// really gone: within the window the error surfaces to the error boundary instead.
const RELOAD_STAMP_KEY = "xe-preload-error-reload-at";
const RELOAD_WINDOW_MS = 10_000;

function claimReload(): boolean {
	try {
		const last = Number(sessionStorage.getItem(RELOAD_STAMP_KEY));
		const now = Date.now();
		if (Number.isFinite(last) && now - last < RELOAD_WINDOW_MS) {
			return false;
		}
		sessionStorage.setItem(RELOAD_STAMP_KEY, String(now));
		return true;
	} catch {
		// Without storage nothing can stop a loop, so leave the error to the boundary.
		return false;
	}
}

export function installPreloadErrorReload(): void {
	window.addEventListener("vite:preloadError", (event) => {
		if (claimReload()) {
			event.preventDefault();
			window.location.reload();
		}
	});
}
