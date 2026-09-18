// Opt-in MSW lifecycle for the 14 test files that actually stub HTTP routes.
//
// This used to be a `setupFiles` entry (src/test/MswSetup.ts), so all 338 test files paid for starting and
// stopping the interception server whether or not they ever called `server.use(...)`. Calling it explicitly
// keeps the behaviour identical for the files that need it and removes it from the ones that do not; the
// no-network invariant for everyone else is held by src/test/NoNetwork.ts instead.
//
// `onUnhandledRequest: "error"` stays here: inside an MSW file a request the test did not declare must still
// fail naming its URL rather than falling through to the real network. `resetHandlers` after each test keeps a
// per-test override from leaking into the next one.

import type { HttpHandler } from "msw";
import { afterAll, afterEach, beforeAll } from "vitest";

import { server } from "@/test/msw/Server";
import { installNetworkGuard, restoreRealTransports } from "@/test/NoNetwork";

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
		server.listen({ onUnhandledRequest: "error" });
		if (defaultHandlers.length > 0) {
			server.use(...defaultHandlers);
		}
	});

	// Passing the defaults back in is what re-arms them: a bare `resetHandlers()` drops everything added after
	// `setupServer`, and this singleton was created with none. With no defaults this is exactly the old call.
	afterEach(() => server.resetHandlers(...defaultHandlers));

	afterAll(() => {
		server.close();
		installNetworkGuard();
	});

	return server;
}
