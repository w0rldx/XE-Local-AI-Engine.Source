// Opt-in MSW lifecycle for the test files that actually stub HTTP routes.
//
// This used to be a `setupFiles` entry (src/test/MswSetup.ts), so every test file paid for starting and stopping
// the interception server whether or not it ever called `server.use(...)`. Calling it explicitly keeps the
// behaviour identical for the files that need it and removes it from the ones that do not; the no-network
// invariant for everyone else is held by src/test/NoNetwork.ts instead.
//
// Inside an MSW file a request the test did not declare must FAIL that test naming its URL, rather than falling
// through to the real network or dying quietly one layer down — see the recorder below for why MSW's own
// `"error"` strategy is not enough on its own. `resetHandlers` after each test keeps a per-test override from
// leaking into the next one.

import type { HttpHandler } from "msw";
import { afterAll, afterEach, beforeAll, beforeEach } from "vitest";

import { server } from "@/test/msw/Server";
import { installNetworkGuard, restoreRealTransports } from "@/test/NoNetwork";

const unhandledRequests: string[] = [];

/**
 * Fails naming every request the current test made that no handler declared, and clears the record either way.
 *
 * {@link setupMswServer} calls this after each test. A test that MEANS to make an undeclared request calls it
 * itself to assert the failure — which also drains the record, so the `afterEach` does not fail that test for the
 * miss it was proving.
 */
export function assertNoUnhandledRequests(): void {
	const recorded = unhandledRequests.splice(0);
	if (recorded.length > 0) {
		throw new Error(
			`MSW intercepted ${recorded.length === 1 ? "a request" : "requests"} no handler declared:\n${recorded
				.map((entry) => `  • ${entry}`)
				.join("\n")}\nDeclare the route with server.use(...) — an undeclared call means the test proved nothing about it.`,
		);
	}
}

/**
 * Registers the MSW server lifecycle for the calling test file. Call once at module top level; the returned
 * server is the same singleton `@/test/msw/Server` exports, so existing `server.use(...)` imports keep working.
 *
 * @param defaultHandlers Routes every test in the FILE should get without asking. The server singleton is shared,
 *   so these cannot be passed to `setupServer` — they are installed once in `beforeAll` and re-installed by every
 *   `resetHandlers`, which is what makes them survive as defaults rather than leaking between files. A per-test
 *   `server.use(...)` still wins, because MSW prepends runtime handlers. Use this for a route that is merely
 *   AMBIENT to the file's subject — a capability probe the page reads before anything else — never for the data a
 *   test is actually about, which belongs in that test where a reader can see it.
 */
export function setupMswServer(...defaultHandlers: readonly HttpHandler[]): typeof server {
	beforeAll(() => {
		// MSW's interceptors wrap whatever transport is installed, so hand it the real ones rather than the
		// guard's throwing stubs.
		restoreRealTransports();
		server.listen({
			// Recording on top of the rejection is what makes a miss fail the test. `print.error()` is MSW's
			// `"error"` strategy — it logs and throws, and that throw does reach the caller as a rejected
			// `fetch`. But a component reaching the API through TanStack Query catches the rejection into
			// `query.error`, so a test whose assertions look at a different part of the DOM stays green over an
			// API call it never stubbed. The `afterEach` below re-raises the miss against the test that made it.
			onUnhandledRequest: (request, print) => {
				unhandledRequests.push(`${request.method} ${request.url}`);
				print.error();
			},
		});
		if (defaultHandlers.length > 0) {
			server.use(...defaultHandlers);
		}
	});

	// Cleared BEFORE the test rather than only after it: a test that failed on something else, or an unmount in a
	// later teardown hook, must not charge its misses to whichever test runs next.
	beforeEach(() => {
		unhandledRequests.length = 0;
	});

	// Passing the defaults back in is what re-arms them: a bare `resetHandlers()` drops everything added after
	// `setupServer`, and this singleton was created with none. Handlers are reset BEFORE the assertion so the next
	// test starts from the file's defaults even when this one is about to fail.
	afterEach(() => {
		server.resetHandlers(...defaultHandlers);
		assertNoUnhandledRequests();
	});

	afterAll(() => {
		server.close();
		installNetworkGuard();
	});

	return server;
}
