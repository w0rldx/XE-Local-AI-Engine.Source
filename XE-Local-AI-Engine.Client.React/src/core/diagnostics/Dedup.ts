// Central dedup gate: "one error → one breadcrumb".
//
// The same logical error is seen by several collectors (ErrorBoundary `onError`, React-root
// `onCaughtError`, `window.onerror`, the `console.error` patch). `shouldRecord` keyed on
// (message + top stack frame) within a ~1s window precedes every error `push`, so it lands once.

/** Suppression window in ms — a repeat of the same key inside this window is dropped. */
export const DEDUP_WINDOW_MS = 1_000;

const lastSeen = new Map<string, number>();

/** Build the dedup key from an error message and its stack (top frame only). */
export function buildErrorKey(message: string, stack?: string): string {
	const topFrame = stack
		?.split("\n")
		.map((line) => line.trim())
		.find((line) => line.startsWith("at ") || line.includes("@") || line.includes(".ts") || line.includes(".js"));
	return `${message}::${topFrame ?? ""}`;
}

/**
 * Returns true the first time a key is seen (and after the window elapses), false while a duplicate
 * arrives inside the suppression window. Side-effecting: stamps the key on an accepted call.
 */
export function shouldRecord(errorKey: string, now: number = Date.now()): boolean {
	const previous = lastSeen.get(errorKey);
	if (previous !== undefined && now - previous < DEDUP_WINDOW_MS) {
		return false;
	}
	lastSeen.set(errorKey, now);
	pruneExpired(now);
	return true;
}

/** Forget all dedup state (test/reset hook). */
export function reset(): void {
	lastSeen.clear();
}

function pruneExpired(now: number): void {
	for (const [key, seenAt] of lastSeen) {
		if (now - seenAt >= DEDUP_WINDOW_MS) {
			lastSeen.delete(key);
		}
	}
}
