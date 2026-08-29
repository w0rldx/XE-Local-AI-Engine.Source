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

import { afterAll, afterEach, beforeAll } from "vitest";

import { server } from "@/test/msw/Server";
import { installNetworkGuard, restoreRealTransports } from "@/test/NoNetwork";

/**
 * Registers the MSW server lifecycle for the calling test file. Call once at module top level; the returned
 * server is the same singleton `@/test/msw/Server` exports, so existing `server.use(...)` imports keep working.
 */
export function setupMswServer(): typeof server {
	beforeAll(() => {
		// MSW's interceptors wrap whatever transport is installed, so hand it the real ones rather than the
		// guard's throwing stubs.
		restoreRealTransports();
		server.listen({ onUnhandledRequest: "error" });
	});

	afterEach(() => server.resetHandlers());

	afterAll(() => {
		server.close();
		installNetworkGuard();
	});

	return server;
}
