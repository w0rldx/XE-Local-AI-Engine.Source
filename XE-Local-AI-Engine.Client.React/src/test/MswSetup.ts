// Vitest setup: run the shared MSW interception server around every test file.
//
// Listed in `vite.config.ts` `test.setupFiles` next to PinLocale.ts, so it applies suite-wide rather than
// per-file. The server starts with no handlers (see src/test/msw/Server.ts); a file that never calls
// `server.use(...)` is unaffected apart from the interception hooks being installed.
//
// `onUnhandledRequest: "error"` is the load-bearing setting. Without it a request nobody stubbed falls through to
// the real network — in jsdom that resolves to whatever the machine can reach, which is a test that passes for the
// wrong reason on a developer box and hangs in CI. With it, an unstubbed call fails the test naming the URL.
//
// `resetHandlers` after each test keeps a per-test `server.use(...)` override from leaking into the next one.

import { afterAll, afterEach, beforeAll } from "vitest";

import { server } from "@/test/msw/Server";

beforeAll(() => server.listen({ onUnhandledRequest: "error" }));

afterEach(() => server.resetHandlers());

afterAll(() => server.close());
