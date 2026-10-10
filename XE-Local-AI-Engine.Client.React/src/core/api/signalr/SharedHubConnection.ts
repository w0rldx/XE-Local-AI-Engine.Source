import { type HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from "@microsoft/signalr";

import { resolveHubAccessToken } from "@/core/api/signalr/ResolveHubAccessToken";
import { buildLocalApiUrl } from "@/core/api/utils/LocalApiUrl";

// Refcounted, module-level SignalR connections — ONE shared HubConnection per hub path, reused across component mounts.
//
// A hook that built its own HubConnection inside its mount effect would pay a full HTTP negotiate + WebSocket upgrade
// on every page visit. This manager keeps a single
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
// Auth / logout: accessTokenFactory resolves the token on the initial negotiate and on every automatic reconnect, and
// refreshes a missing or near-expiry one first (the same resolver as the chat hub), so a reconnect after an idle tab or
// a sleep does not negotiate with an expired token and get a 401. On logout the store token clears and the pages
// holding a hub unmount (they are redirected to sign-in), which drops the refcount to zero and stops the connection,
// so a shared connection does not outlive logout in a broken state.

/** Registered reconnected callback: receives the new connectionId (undefined when the transport reports none). */
type ReconnectedCallback = (connectionId?: string) => void;

/** Registered reconnecting/closed callback: receives the transport error when there was one. */
type HubLifecycleCallback = (error?: Error) => void;

/** A single subscriber's lease on a shared hub connection. Valid from acquire until {@link SharedHubHandle.release}. */
export interface SharedHubHandle {
	/** The shared connection, started on first acquire. Stable for this handle's lifetime (a fixed connection until release). */
	readonly connection: HubConnection;
	/**
	 * Resolves when the start attempt current at acquire time settles — success OR failure (the start error is swallowed to a warning,
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
	/**
	 * Register a callback for the transport dropping and starting to retry, scoped to THIS handle. Same reason for the
	 * per-handle set as {@link onReconnected}: SignalR's own `onreconnecting` cannot unregister one callback.
	 */
	onReconnecting(callback: HubLifecycleCallback): () => void;
	/**
	 * Register a callback for the connection closing for good — the automatic-reconnect policy gave up, so nothing
	 * further arrives on this connection and a subscriber must fall back to polling until the manager's backoff restart
	 * succeeds, which fires {@link onReconnected}.
	 */
	onClosed(callback: HubLifecycleCallback): () => void;
	/** Release this acquisition. The last release stops (and discards) the shared connection. Idempotent. */
	release(): void;
}

interface HubEntry {
	readonly connection: HubConnection;
	readonly reconnectedCallbacks: Set<ReconnectedCallback>;
	readonly reconnectingCallbacks: Set<HubLifecycleCallback>;
	readonly closedCallbacks: Set<HubLifecycleCallback>;
	refCount: number;
	/** The (already-caught, always-resolving) latest start promise. Stop is deferred behind it. */
	startPromise: Promise<void>;
	/** True while a start() is in flight, so an acquire or a restart never starts a second one. */
	starting: boolean;
	/** Consecutive failed starts, indexing RESTART_DELAYS_MS. Reset by a successful start. */
	restartAttempt: number;
	/** Pending backoff restart after a close or a failed start (see RESTART_DELAYS_MS). */
	restartTimer?: ReturnType<typeof setTimeout>;
	/** Pending deferred-stop timer while the entry lingers at refcount zero (see STOP_LINGER_MS). */
	lingerTimer?: ReturnType<typeof setTimeout>;
}

// How long a connection lingers after its LAST subscriber releases before it is stopped. Navigation unmounts one
// page's subscriber before the next page mounts its own, so an immediate stop would still pay a fresh negotiate +
// WebSocket upgrade on every visit — the exact churn this manager exists to remove. A re-acquire within the window
// cancels the stop and reuses the live connection. Kept short so idle hubs (and a post-logout lingering socket —
// the same exposure class as the chat hub's permanent singleton, for at most this window) do not accumulate.
const STOP_LINGER_MS = 30_000;

// Backoff for restarting a connection that closed for good (withAutomaticReconnect gave up, e.g. a node restart or a
// long sleep) or whose start failed (withAutomaticReconnect never retries a start). The last delay repeats while a
// subscriber holds a lease: shell components hold some hubs for the whole tab, so giving up would freeze them for good.
const RESTART_DELAYS_MS = [1_000, 2_000, 5_000, 10_000, 30_000];

// One entry per hub path, created lazily on first acquire and deleted when the last subscriber releases.
const entries = new Map<string, HubEntry>();

function buildEntry(hubPath: string): HubEntry {
	const connection = new HubConnectionBuilder()
		.withUrl(buildLocalApiUrl(hubPath), {
			// Never log the returned token; it can end up in WS query strings.
			accessTokenFactory: () => resolveHubAccessToken(),
		})
		.withAutomaticReconnect()
		.configureLogging(LogLevel.Warning)
		.build();

	const reconnectedCallbacks = new Set<ReconnectedCallback>();
	const reconnectingCallbacks = new Set<HubLifecycleCallback>();
	const closedCallbacks = new Set<HubLifecycleCallback>();
	// One fan-out registration per lifecycle event: SignalR gives no way to remove a single onreconnected /
	// onreconnecting / onclose callback, so per-handle callbacks live in these sets (added on registration, dropped on
	// release) and are dispatched from here.
	connection.onreconnected((connectionId) => {
		for (const callback of reconnectedCallbacks) {
			callback(connectionId ?? undefined);
		}
	});
	connection.onreconnecting((error) => {
		for (const callback of reconnectingCallbacks) {
			callback(error);
		}
	});
	// The retry policy gave up. Nothing more arrives on this connection, and — unlike a reconnecting blip — nothing will
	// announce that later either, so a subscriber that ignores this shows frozen data behind a healthy-looking page.
	const entry: HubEntry = {
		connection,
		reconnectedCallbacks,
		reconnectingCallbacks,
		closedCallbacks,
		refCount: 0,
		startPromise: Promise.resolve(),
		starting: false,
		restartAttempt: 0,
	};
	connection.onclose((error) => {
		for (const callback of closedCallbacks) {
			callback(error);
		}
		scheduleRestart(hubPath, entry);
	});

	startEntry(hubPath, entry, false);
	return entry;
}

// A hub that cannot connect must not break the page — subscribers tolerate a failed start (their queries/stores still
// serve last-good state) and the manager retries on RESTART_DELAYS_MS. Restarting the SAME connection keeps every
// subscriber's handlers; a successful restart fans out as a reconnect so subscribers re-read what they missed.
function startEntry(hubPath: string, entry: HubEntry, isRestart: boolean): void {
	if (entry.restartTimer !== undefined) {
		clearTimeout(entry.restartTimer);
		entry.restartTimer = undefined;
	}
	entry.starting = true;
	entry.startPromise = entry.connection
		.start()
		.then(
			() => {
				entry.restartAttempt = 0;
				if (isRestart) {
					for (const callback of entry.reconnectedCallbacks) {
						callback(entry.connection.connectionId ?? undefined);
					}
				}
			},
			(error: unknown) => {
				console.warn(`shared signalr hub "${hubPath}" failed to start`, error);
				// A 404 from negotiate means the hub's feature is off for this process: retrying cannot succeed, so stop here
				// as if the retry policy gave up. A later acquire after the lease is released tries once more.
				if (!isFeatureOffStart(error)) {
					scheduleRestart(hubPath, entry);
				}
			},
		)
		.finally(() => {
			entry.starting = false;
		});
}

// SignalR wraps the negotiate HttpError in a FailedToNegotiateWithServerError that keeps the status only in its message.
function isFeatureOffStart(error: unknown): boolean {
	const candidate = error as { statusCode?: unknown; errorType?: unknown; message?: unknown } | null;
	return (
		candidate?.statusCode === 404 ||
		(candidate?.errorType === "FailedToNegotiateWithServerError" &&
			typeof candidate.message === "string" &&
			candidate.message.includes("Status code '404'"))
	);
}

function scheduleRestart(hubPath: string, entry: HubEntry): void {
	// No restart once the last subscriber left (the linger stop owns the entry) or the entry was discarded; the stop on
	// the last release also fires onclose and lands here.
	if (entry.refCount === 0 || entries.get(hubPath) !== entry || entry.restartTimer !== undefined) {
		return;
	}
	const delayMs = RESTART_DELAYS_MS[Math.min(entry.restartAttempt, RESTART_DELAYS_MS.length - 1)] ?? 0;
	entry.restartAttempt += 1;
	entry.restartTimer = setTimeout(() => {
		entry.restartTimer = undefined;
		if (
			entry.refCount === 0 ||
			entries.get(hubPath) !== entry ||
			entry.starting ||
			entry.connection.state !== HubConnectionState.Disconnected
		) {
			return;
		}
		startEntry(hubPath, entry, true);
	}, delayMs);
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
	// A closed connection (or one whose start failed) must not be handed out dead: start it now instead of waiting for
	// a pending backoff restart.
	if (!entry.starting && entry.connection.state === HubConnectionState.Disconnected) {
		startEntry(hubPath, entry, true);
	}
	const activeEntry = entry;

	let released = false;
	// Every lifecycle unregister fn this handle owns, so release() can drop them all (SignalR itself cannot).
	const ownUnsubscribers = new Set<() => void>();

	function register<T>(set: Set<T>, callback: T): () => void {
		set.add(callback);
		const unsubscribe = (): void => {
			set.delete(callback);
		};
		ownUnsubscribers.add(unsubscribe);
		return () => {
			ownUnsubscribers.delete(unsubscribe);
			unsubscribe();
		};
	}

	return {
		connection: activeEntry.connection,
		whenStarted: activeEntry.startPromise,
		onReconnected: (callback: ReconnectedCallback) => register(activeEntry.reconnectedCallbacks, callback),
		onReconnecting: (callback: HubLifecycleCallback) => register(activeEntry.reconnectingCallbacks, callback),
		onClosed: (callback: HubLifecycleCallback) => register(activeEntry.closedCallbacks, callback),
		release(): void {
			if (released) {
				return;
			}
			released = true;
			for (const unsubscribe of ownUnsubscribers) {
				unsubscribe();
			}
			ownUnsubscribers.clear();
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
		entry.reconnectingCallbacks.clear();
		entry.closedCallbacks.clear();
		if (entry.lingerTimer !== undefined) {
			clearTimeout(entry.lingerTimer);
			entry.lingerTimer = undefined;
		}
		if (entry.restartTimer !== undefined) {
			clearTimeout(entry.restartTimer);
			entry.restartTimer = undefined;
		}
	}
	entries.clear();
}
