import { setupServer } from "msw/node";

/**
 * The one MSW request-interception server for the whole suite.
 *
 * It starts with NO handlers: `src/test/MswSetup.ts` (a vitest `setupFiles` entry) calls `listen` /
 * `resetHandlers` / `close` around every test file, and each test declares the routes it needs with
 * `server.use(...)`. Anything a test did not declare is an unhandled request and fails loudly
 * (`onUnhandledRequest: "error"`), so a test can never silently pass against a real network call.
 *
 * Use this instead of `vi.mock`ing an api module when the thing under test is the *boundary* — the generated
 * hey-api SDK, the shared axios instance, and its interceptor chain (auth header, ProblemDetails → `ApiError`,
 * FormData content-type, zod response validation). Mocking the api module deletes all of that from the test.
 */
export const server = setupServer();
