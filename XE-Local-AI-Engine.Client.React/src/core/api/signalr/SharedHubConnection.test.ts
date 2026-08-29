import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// Mirrors STOP_LINGER_MS in SharedHubConnection.ts (not exported). The linger delays the stop after the LAST subscriber
// releases so a navigation that unmounts one page just before the next mounts reuses the live connection.
const STOP_LINGER_MS = 30_000;

// A controllable fake HubConnection: start() returns a promise the test resolves via resolveStart(), so both the linger
// timer AND the deferred-stop-behind-the-start-promise can be exercised deterministically. The manager registers a
// single onreconnected fan-out, captured here so a test can drive a reconnect and observe the per-handle callbacks.
interface FakeConnection {
	state: string;
	start: ReturnType<typeof vi.fn>;
	stop: ReturnType<typeof vi.fn>;
	on: ReturnType<typeof vi.fn>;
	off: ReturnType<typeof vi.fn>;
	onreconnected: ReturnType<typeof vi.fn>;
	onreconnecting: ReturnType<typeof vi.fn>;
	onclose: ReturnType<typeof vi.fn>;
	/** The manager's fan-out callbacks, captured from its lifecycle registrations. */
	reconnectedFanout?: (connectionId?: string) => void;
	reconnectingFanout?: (error?: Error) => void;
	closedFanout?: (error?: Error) => void;
	/** Resolve the in-flight start(), flipping the connection to Connected (mirrors a successful negotiate). */
	resolveStart: () => void;
}

const hoisted = vi.hoisted(() => {
	const builtConnections: FakeConnection[] = [];
	let lastWithUrl: { url: string; options: { accessTokenFactory?: () => string } } | undefined;

	const makeConnection = (): FakeConnection => {
		let resolveStart!: () => void;
		const startPromise = new Promise<void>((resolve) => {
			resolveStart = resolve;
		});
		const connection: FakeConnection = {
			state: "Disconnected",
			start: vi.fn(() => startPromise),
			stop: vi.fn(() => {
				connection.state = "Disconnected";
				return Promise.resolve();
			}),
			on: vi.fn(),
			off: vi.fn(),
			onreconnected: vi.fn((callback: (connectionId?: string) => void) => {
				connection.reconnectedFanout = callback;
			}),
			onreconnecting: vi.fn((callback: (error?: Error) => void) => {
				connection.reconnectingFanout = callback;
			}),
			onclose: vi.fn((callback: (error?: Error) => void) => {
				connection.closedFanout = callback;
			}),
			resolveStart: () => {
				connection.state = "Connected";
				resolveStart();
			},
		};
		builtConnections.push(connection);
		return connection;
	};

	return {
		builtConnections,
		makeConnection,
		getLastWithUrl: () => lastWithUrl,
		setLastWithUrl: (value: { url: string; options: { accessTokenFactory?: () => string } }) => {
			lastWithUrl = value;
		},
	};
});

vi.mock("@microsoft/signalr", () => ({
	HubConnectionBuilder: class HubConnectionBuilder {
		withUrl(url: string, options: { accessTokenFactory?: () => string }) {
			hoisted.setLastWithUrl({ url, options });
			return this;
		}
		withAutomaticReconnect() {
			return this;
		}
		configureLogging() {
			return this;
		}
		build() {
			return hoisted.makeConnection();
		}
	},
	HubConnectionState: { Connected: "Connected", Disconnected: "Disconnected", Connecting: "Connecting", Reconnecting: "Reconnecting" },
	LogLevel: { Warning: 3 },
}));

vi.mock("@/core/auth/stores/NodeAuthStore", () => ({
	useNodeAuthStore: { getState: () => ({ accessToken: "test-token" }) },
}));

import { acquireHubConnection, resetSharedHubConnectionsForTest } from "@/core/api/signalr/SharedHubConnection";

// Drain the promise microtask queue (start().catch().finally() chains) under fake timers — advancing by 0 fires no
// timer but yields so pending microtasks settle.
async function flushMicrotasks(): Promise<void> {
	await vi.advanceTimersByTimeAsync(0);
}

// Advance past the stop-linger window AND flush the trailing deferred-stop microtask, so a pending stop actually runs.
async function advancePastLinger(): Promise<void> {
	await vi.advanceTimersByTimeAsync(STOP_LINGER_MS);
	await flushMicrotasks();
}

const HUB = "test/hub";

describe("acquireHubConnection", () => {
	beforeEach(() => {
		vi.useFakeTimers();
		resetSharedHubConnectionsForTest();
		hoisted.builtConnections.length = 0;
	});

	afterEach(() => {
		resetSharedHubConnectionsForTest();
		vi.useRealTimers();
	});

	it("builds and starts exactly one connection on first acquire", () => {
		const handle = acquireHubConnection(HUB);

		expect(hoisted.builtConnections).toHaveLength(1);
		expect(hoisted.builtConnections[0]?.start).toHaveBeenCalledTimes(1);
		expect(handle.connection).toBe(hoisted.builtConnections[0]);
	});

	it("configures the connection with the node access-token factory", () => {
		acquireHubConnection(HUB);

		const withUrl = hoisted.getLastWithUrl();
		expect(withUrl?.url).toContain("test/hub");
		expect(withUrl?.options.accessTokenFactory?.()).toBe("test-token");
	});

	it("reuses the same connection for a second acquire while the first is still open", () => {
		const first = acquireHubConnection(HUB);
		const second = acquireHubConnection(HUB);

		expect(hoisted.builtConnections).toHaveLength(1);
		expect(hoisted.builtConnections[0]?.start).toHaveBeenCalledTimes(1);
		expect(second.connection).toBe(first.connection);
	});

	it("keeps separate connections per hub path", () => {
		acquireHubConnection("hub/a");
		acquireHubConnection("hub/b");
		expect(hoisted.builtConnections).toHaveLength(2);
	});

	it("resolves whenStarted only after the initial start settles", async () => {
		const handle = acquireHubConnection(HUB);
		let started = false;
		handle.whenStarted.then(() => {
			started = true;
		});

		await flushMicrotasks();
		expect(started).toBe(false);

		hoisted.builtConnections[0]?.resolveStart();
		await flushMicrotasks();
		expect(started).toBe(true);
	});

	it("does not stop on last release until the linger elapses, then stops exactly once", async () => {
		const first = acquireHubConnection(HUB);
		const second = acquireHubConnection(HUB);
		const connection = hoisted.builtConnections[0];
		connection?.resolveStart();
		await flushMicrotasks();

		// Releasing one of two subscribers must NOT arm the linger — the connection is still in use.
		first.release();
		await flushMicrotasks();
		expect(connection?.stop).not.toHaveBeenCalled();

		// The last release arms the linger, but the stop must not fire until the window elapses.
		second.release();
		await flushMicrotasks();
		expect(connection?.stop).not.toHaveBeenCalled();

		await advancePastLinger();
		expect(connection?.stop).toHaveBeenCalledTimes(1);
	});

	it("re-acquire WITHIN the linger window reuses the live connection and never stops it", async () => {
		const first = acquireHubConnection(HUB);
		const connection = hoisted.builtConnections[0];
		connection?.resolveStart();
		await flushMicrotasks();

		// Last release arms the 30s linger; a new subscriber arrives partway through the window (a navigate-away-and-back).
		first.release();
		await vi.advanceTimersByTimeAsync(Math.floor(STOP_LINGER_MS / 3));
		const second = acquireHubConnection(HUB);

		expect(hoisted.builtConnections).toHaveLength(1);
		expect(second.connection).toBe(connection);

		// Advancing well past the ORIGINAL window must not stop it — the re-acquire cancelled the pending linger.
		await advancePastLinger();
		expect(connection?.stop).not.toHaveBeenCalled();
	});

	it("after the linger elapses with no re-acquire, stops once and the next acquire builds a fresh connection", async () => {
		const first = acquireHubConnection(HUB);
		const original = hoisted.builtConnections[0];
		original?.resolveStart();
		await flushMicrotasks();

		first.release();
		await advancePastLinger();
		expect(original?.stop).toHaveBeenCalledTimes(1);

		const second = acquireHubConnection(HUB);
		expect(hoisted.builtConnections).toHaveLength(2);
		expect(second.connection).toBe(hoisted.builtConnections[1]);
		expect(second.connection).not.toBe(original);
		expect(hoisted.builtConnections[1]?.start).toHaveBeenCalledTimes(1);
	});

	it("defers the stop while start is in flight so an acquire -> release -> acquire flip keeps one connection", async () => {
		// StrictMode double-invoke shape: mount acquires, cleanup releases (refcount 0 while start still negotiating),
		// remount re-acquires before the linger + deferred stop fires.
		const first = acquireHubConnection(HUB);
		first.release();
		const second = acquireHubConnection(HUB);

		const connection = hoisted.builtConnections[0];
		expect(hoisted.builtConnections).toHaveLength(1);

		// Settle the negotiation and run out any linger: the refcount is 1 (thanks to `second`), so no stop.
		connection?.resolveStart();
		await advancePastLinger();
		expect(connection?.stop).not.toHaveBeenCalled();

		// The genuine last release then tears it down after the linger.
		second.release();
		await advancePastLinger();
		expect(connection?.stop).toHaveBeenCalledTimes(1);
	});

	it("fans a reconnect out to every subscriber's callback and drops a handle's callback on release", async () => {
		const first = acquireHubConnection(HUB);
		const second = acquireHubConnection(HUB);
		hoisted.builtConnections[0]?.resolveStart();
		await flushMicrotasks();

		const cb1 = vi.fn();
		const cb2 = vi.fn();
		first.onReconnected(cb1);
		second.onReconnected(cb2);

		hoisted.builtConnections[0]?.reconnectedFanout?.("id-1");
		expect(cb1).toHaveBeenCalledWith("id-1");
		expect(cb2).toHaveBeenCalledWith("id-1");

		// Releasing the first handle drops ITS reconnected callback (SignalR itself cannot remove one); the second stays.
		first.release();
		cb1.mockClear();
		cb2.mockClear();
		hoisted.builtConnections[0]?.reconnectedFanout?.("id-2");
		expect(cb1).not.toHaveBeenCalled();
		expect(cb2).toHaveBeenCalledWith("id-2");
	});

	it("fans reconnecting and closed out per handle too, and drops them on release", async () => {
		// A subscriber cannot register these on the connection itself: SignalR has no way to remove ONE onreconnecting or
		// onclose callback, so a shared connection would accumulate one per mount. Without them, a transport that drops
		// after a good subscribe is invisible — the page keeps painting frozen data behind a healthy-looking UI.
		const first = acquireHubConnection(HUB);
		const second = acquireHubConnection(HUB);
		hoisted.builtConnections[0]?.resolveStart();
		await flushMicrotasks();

		const reconnecting = vi.fn();
		const closed = vi.fn();
		first.onReconnecting(reconnecting);
		first.onClosed(closed);
		const secondClosed = vi.fn();
		second.onClosed(secondClosed);

		const error = new Error("transport lost");
		hoisted.builtConnections[0]?.reconnectingFanout?.(error);
		hoisted.builtConnections[0]?.closedFanout?.(error);
		expect(reconnecting).toHaveBeenCalledWith(error);
		expect(closed).toHaveBeenCalledWith(error);
		expect(secondClosed).toHaveBeenCalledWith(error);

		first.release();
		reconnecting.mockClear();
		closed.mockClear();
		secondClosed.mockClear();
		hoisted.builtConnections[0]?.reconnectingFanout?.();
		hoisted.builtConnections[0]?.closedFanout?.();
		expect(reconnecting).not.toHaveBeenCalled();
		expect(closed).not.toHaveBeenCalled();
		expect(secondClosed).toHaveBeenCalled();
	});

	it("unregisters a single reconnected callback via the returned dispose fn without dropping the other", async () => {
		const handle = acquireHubConnection(HUB);
		hoisted.builtConnections[0]?.resolveStart();
		await flushMicrotasks();

		const cb1 = vi.fn();
		const cb2 = vi.fn();
		const dispose1 = handle.onReconnected(cb1);
		handle.onReconnected(cb2);

		dispose1();
		hoisted.builtConnections[0]?.reconnectedFanout?.();
		expect(cb1).not.toHaveBeenCalled();
		expect(cb2).toHaveBeenCalledTimes(1);
	});

	it("ignores a double release (idempotent) so refcount cannot go negative", async () => {
		const first = acquireHubConnection(HUB);
		const second = acquireHubConnection(HUB);
		const connection = hoisted.builtConnections[0];
		connection?.resolveStart();
		await flushMicrotasks();

		first.release();
		first.release(); // second call is a no-op — must not decrement the refcount the OTHER subscriber holds
		await advancePastLinger();
		expect(connection?.stop).not.toHaveBeenCalled();

		second.release();
		await advancePastLinger();
		expect(connection?.stop).toHaveBeenCalledTimes(1);
	});
});
