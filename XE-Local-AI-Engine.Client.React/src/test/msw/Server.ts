import { setupServer } from "msw/node";

/**
 * The one MSW request-interception server for the whole suite.
 *
 * It starts with NO handlers: `setupMswServer()` (`src/test/UseMswServer.ts`) calls `listen` / `resetHandlers` /
 * `close` around the file that opted in, and each test declares the routes it needs with `server.use(...)`.
 * Anything a test did not declare is an unhandled request, and the lifecycle fails that test naming the URL, so a
 * test can never silently pass against a call it never stubbed.
 *
 * Use this instead of `vi.mock`ing an api module when the thing under test is the *boundary* — the generated
 * hey-api SDK, the shared axios instance, and its interceptor chain (auth header, ProblemDetails → `ApiError`,
 * FormData content-type, zod response validation). Mocking the api module deletes all of that from the test.
 */
export const server = setupServer();
