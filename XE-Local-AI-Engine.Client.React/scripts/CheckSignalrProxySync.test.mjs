import assert from "node:assert/strict";
import test from "node:test";

import { compareProxyPaths, extractMappedHubPaths } from "./CheckSignalrProxySync.mjs";

test("extracts Program.cs hub paths and reports missing and stale proxies", () => {
	const routes = `
public static class LocalApiRoutes
{
    // Braces in comments must not end a class block: { ignored }.
    public static class Chat
    {
        public const string Hub = "/api/local/v1/chat/hub";
    }
    public static class Jobs
    {
        public const string EventsHub = "/api/local/v1/jobs/hub";
    }
}`;
	const program = `
app.MapHub<ChatHub>(LocalApiRoutes.Chat.Hub);
app.MapHub<JobHub>(LocalApiRoutes.Jobs.EventsHub);`;

	const mapped = extractMappedHubPaths(program, routes);
	const result = compareProxyPaths(mapped, ["/api/local/v1/chat/hub", "/api/local/v1/stale/hub"]);

	assert.deepEqual(mapped, ["/api/local/v1/chat/hub", "/api/local/v1/jobs/hub"]);
	assert.deepEqual(result, {
		missing: ["/api/local/v1/jobs/hub"],
		stale: ["/api/local/v1/stale/hub"],
	});
});

test("accepts multiline constant and literal routes while ignoring commented and string occurrences", () => {
	const routes = `
public static class LocalApiRoutes
{
    public static class Chat
    {
        public const string Hub = "/api/local/v1/chat/hub";
    }
}`;
	const program = `
// app.MapHub<IgnoredHub>(LocalApiRoutes.Missing.Hub);
var sample = "MapHub<IgnoredHub>(LocalApiRoutes.Missing.Hub)";
/* app.MapHub<IgnoredHub>("/ignored/hub"); */
app.MapHub<
    ChatHub
>(
    LocalApiRoutes.Chat.Hub
);
app.MapHub<LiteralHub>("/api/local/v1/literal/hub");`;

	assert.deepEqual(extractMappedHubPaths(program, routes), [
		"/api/local/v1/chat/hub",
		"/api/local/v1/literal/hub",
	]);
});

test("rejects every unrecognized active MapHub route form", () => {
	assert.throws(
		() => extractMappedHubPaths("app.MapHub<ChatHub>(ResolveHubRoute());", ""),
		/Unrecognized active MapHub route argument/,
	);
});

test("rejects unmatched active MapHub invocations", () => {
	assert.throws(
		() => extractMappedHubPaths("app.MapHub<ChatHub>(LocalApiRoutes.Chat.Hub;", ""),
		/Unmatched \( in active MapHub invocation/,
	);
});
