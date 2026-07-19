import { type HubConnection, HubConnectionBuilder, LogLevel } from "@microsoft/signalr";

import { buildLocalApiUrl } from "@/core/api/utils/LocalApiUrl";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";

// Refcounted, module-level SignalR connections — ONE shared HubConnection per hub path, reused across component mounts.
//
// Every feature hub hook used to build a brand-new HubConnection inside its mount effect, so each page visit paid a full
// HTTP negotiate + WebSocket upgrade (live-confirmed connection churn on navigation). This manager keeps a single
// connection alive per hub for as long as at least one subscriber is mounted: the FIRST acquire builds + starts it,
// later acquires reuse it, and the LAST release stops (and discards) it. It mirrors the proven singleton pattern in
// NodeChatConnection but generalizes it to any hub and to multiple concurrent subscribers.
//
// Handlers stay PER-SUBSCRIBER: each hook still calls connection.on(...) in its effect and connection.off(...) with the
// SAME handler reference on cleanup. SignalR fans a client method out to every registered handler, so multiple
// subscribers to one hub coexist without stealing each other's events. This utility owns only the connection LIFETIME,
// never the handlers.
//
// StrictMode / fast-remount safety mirrors NodeChatConnection and the per-mount hooks it replaces: the stop is DEFERRED
// until the start promise settles AND re-checks the refcount, so an acquire -> release -> acquire flip (React's
// double-invoke, or a quick navigation back) never aborts an in-flight negotiation and never tears the connection down
// once a new subscriber has already taken it over.
//
// Auth / logout: accessTokenFactory reads the CURRENT store token on the initial negotiate and on every automatic
// reconnect, exactly as the per-mount hooks did — so a long-lived shared connection re-authenticates with the live token
// across reconnects. On logout the store token clears and the pages holding a hub unmount (they are redirected to
// sign-in), which drops the refcount to zero and stops the connection, so a shared connection does not outlive logout in
// a broken state. Parity gap versus the chat hub: this factory does NOT proactively refresh a near-expiry token
// (NodeChatConnection does); the seven hooks this replaces never did either, so current behavior is preserved.

/** Registered reconnected callback: receives the new connectionId (undefined when the transport reports none). */
type ReconnectedCallback = (connectionId?: string) => void;

/** A single subscriber's lease on a shared hub connection. Valid from acquire until {@link SharedHubHandle.release}. */
export interface SharedHubHandle {
	/** The shared connection, started on first acquire. Stable for this handle's lifetime (a fixed connection until release). */
	readonly connection: HubConnection;
	/**
	 * Resolves when the INITIAL start attempt settles — success OR failure (the start error is swallowed to a warning,
	 * matching the best-effort hooks). Check `connection.state === Connected` before acting. A subscriber that acquires
	 * after the connection is already up sees this resolve on the next microtask, so on-connect work still runs for it.
	 */
	readonly whenStarted: Promise<void>;
	/**
	 * Register a reconnected callback scoped to THIS handle and return an unregister fn. SignalR's own
	 * `connection.onreconnected` cannot unregister a single callback, which would leak one per mount on a shared
	 * connection; this manager fans one onreconnected out to a per-handle set instead. {@link release} also drops every
	 * callback this handle registered, so a hook may rely on release alone.
	 */
	onReconnected(callback: ReconnectedCallback): () => void;
	/** Release this acquisition. The last release stops (and discards) the shared connection. Idempotent. */
	release(): void;
}

interface HubEntry {
	readonly connection: HubConnection;
	readonly reconnectedCallbacks: Set<ReconnectedCallback>;
	refCount: number;
	/** The (already-caught, always-resolving) initial start promise. Stop is deferred behind it. */
	readonly startPromise: Promise<void>;
	/** Pending deferred-stop timer while the entry lingers at refcount zero (see STOP_LINGER_MS). */
	lingerTimer?: ReturnType<typeof setTimeout>;
}

// How long a connection lingers after its LAST subscriber releases before it is stopped. Navigation unmounts one
// page's subscriber before the next page mounts its own, so an immediate stop would still pay a fresh negotiate +
// WebSocket upgrade on every visit — the exact churn this manager exists to remove. A re-acquire within the window
// cancels the stop and reuses the live connection. Kept short so idle hubs (and a post-logout lingering socket —
// the same exposure class as the chat hub's permanent singleton, for at most this window) do not accumulate.
const STOP_LINGER_MS = 30_000;

// One entry per hub path, created lazily on first acquire and deleted when the last subscriber releases.
const entries = new Map<string, HubEntry>();

function buildEntry(hubPath: string): HubEntry {
	const connection = new HubConnectionBuilder()
		.withUrl(buildLocalApiUrl(hubPath), {
			// Read the live store token on every negotiate/reconnect, matching the per-mount hooks (never proactively
			// refreshed — see the logout note above). Never log the returned token; it can end up in WS query strings.
			accessTokenFactory: () => useNodeAuthStore.getState().accessToken ?? "",
		})
		.withAutomaticReconnect()
		.configureLogging(LogLevel.Warning)
		.build();

	const reconnectedCallbacks = new Set<ReconnectedCallback>();
	// A single fan-out registration: SignalR gives no way to remove one onreconnected callback, so per-handle callbacks
	// live in this set (add on onReconnected, remove on release) and are dispatched from here.
	connection.onreconnected((connectionId) => {
		for (const callback of reconnectedCallbacks) {
			callback(connectionId ?? undefined);
		}
	});

	// A hub that cannot connect must not break the page — subscribers tolerate a failed start (their queries/stores still
	// serve last-good state). withAutomaticReconnect does NOT retry the initial start, so a first-start failure leaves the
	// connection disconnected until the last release rebuilds it, exactly as the per-mount hooks behaved on remount.
	const startPromise = connection.start().catch((error: unknown) => {
		console.warn(`shared signalr hub "${hubPath}" failed to start`, error);
	});

	return { connection, reconnectedCallbacks, refCount: 0, startPromise };
}

function releaseEntry(hubPath: string, entry: HubEntry): void {
	entry.refCount -= 1;
	if (entry.refCount > 0) {
		return;
	}
	// Last subscriber gone. Linger before stopping (STOP_LINGER_MS) so a navigation that unmounts this page's
	// subscriber just before the next page acquires reuses the live connection, then defer the actual stop until the
	// start promise settles so we never abort an in-flight negotiation (the StrictMode acquire -> release -> acquire
	// flip). Both legs RE-CHECK the refcount: a subscriber that re-acquired in the meantime keeps the connection
	// alive. The `entries.get === entry` guard makes a second deferred stop (refcount bounced 0 -> 1 -> 0) a no-op
	// once the entry has already been removed/replaced.
	entry.lingerTimer = setTimeout(() => {
		entry.lingerTimer = undefined;
		entry.startPromise.finally(() => {
			if (entry.refCount > 0 || entries.get(hubPath) !== entry) {
				return;
			}
			entries.delete(hubPath);
			entry.connection.stop().catch((error: unknown) => {
				console.warn(`shared signalr hub "${hubPath}" failed to stop`, error);
			});
		});
	}, STOP_LINGER_MS);
}

/**
 * Acquire a lease on the shared connection for `hubPath` (e.g. `"scheduler/hub"`). Builds + starts the connection on the
 * first live lease and reuses it for every subsequent one; the returned handle MUST be released (in the effect cleanup)
 * so the connection can be torn down when the last subscriber unmounts.
 */
export function acquireHubConnection(hubPath: string): SharedHubHandle {
	let entry = entries.get(hubPath);
	if (!entry) {
		entry = buildEntry(hubPath);
		entries.set(hubPath, entry);
	}
	entry.refCount += 1;
	// A re-acquire during the linger window keeps the live connection: cancel the pending stop. (Even un-cancelled,
	// the deferred stop's refcount re-check would no-op — clearing just avoids the dangling timer.)
	if (entry.lingerTimer !== undefined) {
		clearTimeout(entry.lingerTimer);
		entry.lingerTimer = undefined;
	}
	const activeEntry = entry;

	let released = false;
	// The onReconnected unregister fns this handle owns, so release() can drop them all (SignalR itself cannot).
	const ownReconnectedUnsubscribers = new Set<() => void>();

	return {
		connection: activeEntry.connection,
		whenStarted: activeEntry.startPromise,
		onReconnected(callback: ReconnectedCallback): () => void {
			activeEntry.reconnectedCallbacks.add(callback);
			const unsubscribe = (): void => {
				activeEntry.reconnectedCallbacks.delete(callback);
			};
			ownReconnectedUnsubscribers.add(unsubscribe);
			return () => {
				ownReconnectedUnsubscribers.delete(unsubscribe);
				unsubscribe();
			};
		},
		release(): void {
			if (released) {
				return;
			}
			released = true;
			for (const unsubscribe of ownReconnectedUnsubscribers) {
				unsubscribe();
			}
			ownReconnectedUnsubscribers.clear();
			releaseEntry(hubPath, activeEntry);
		},
	};
}

/**
 * Test-only: drop all cached shared connections so each test starts from an empty registry. The suite runs without
 * testing-library auto-cleanup (vitest `globals` is off), so mounted hooks are not unmounted between tests and their
 * refcounts never fall to zero on their own; call this in a test's `beforeEach` to isolate the module-level state.
 * Only clears the registry (does NOT call connection.stop) so it never pollutes a test's mock call counts.
 */
export function resetSharedHubConnectionsForTest(): void {
	for (const entry of entries.values()) {
		entry.reconnectedCallbacks.clear();
		if (entry.lingerTimer !== undefined) {
			clearTimeout(entry.lingerTimer);
			entry.lingerTimer = undefined;
		}
	}
	entries.clear();
}
