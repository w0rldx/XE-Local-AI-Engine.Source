import {
	checkHubPayloadEnums,
	checkSignalrProxySync,
	compareEnumMembers,
	compareProxyPaths,
	extractCSharpEnumMembers,
	extractMappedHubPaths,
	extractQuotedNames,
	hubPayloadEnums,
} from "./CheckSignalrProxySync.mjs";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const acquisitionHubPath = "/api/local/v1/model-fit/gguf/downloads/hub";
const benchmarkHubPath = "/api/local/v1/benchmarks/hub";

test("configures the benchmark hub as a specific WebSocket proxy before the generic API fallback", () => {
	const proxyPaths = JSON.parse(readFileSync(new URL("../config/signalr-proxy-paths.json", import.meta.url), "utf8"));
	assert.equal(proxyPaths.filter((path) => path === benchmarkHubPath).length, 1);
	const viteConfig = readFileSync(new URL("../vite.config.ts", import.meta.url), "utf8");
	const signalrSpread = viteConfig.indexOf("...Object.fromEntries(signalrProxyPaths.map");
	const genericApi = viteConfig.indexOf('"/api": localProxy(proxyTarget)');
	assert.ok(signalrSpread >= 0 && genericApi > signalrSpread, "SignalR ws:true entries must precede the generic HTTP /api proxy");
});

test("keeps the generalized GGUF acquisition hub on the single compatible download path", () => {
	const proxyPaths = JSON.parse(readFileSync(new URL("../config/signalr-proxy-paths.json", import.meta.url), "utf8"));
	assert.equal(proxyPaths.filter((path) => path === acquisitionHubPath).length, 1);
	assert.ok(checkSignalrProxySync() > 0);
	const routes = readFileSync(
		new URL("../../XE-Local-AI-Engine.Client/Endpoints/Common/LocalApiRoutes.cs", import.meta.url),
		"utf8",
	);
	const program = readFileSync(new URL("../../XE-Local-AI-Engine.Client/Program.cs", import.meta.url), "utf8");
	assert.ok(routes.includes(`public const string DownloadHub = "${acquisitionHubPath}"`));
	assert.ok(extractMappedHubPaths(program, routes).includes(acquisitionHubPath));
});

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

	assert.deepEqual(extractMappedHubPaths(program, routes), ["/api/local/v1/chat/hub", "/api/local/v1/literal/hub"]);
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

test("holds every hub payload enum to its hand-written client member list", () => {
	assert.equal(checkHubPayloadEnums(), hubPayloadEnums.length);
	assert.ok(hubPayloadEnums.length > 0);
});

test("reads C# enum members through the trivia mask, ignoring doc comments and explicit values", () => {
	const source = `public enum Kind
{
    /// <summary>A "quoted" doc comment naming Decoy.</summary>
    State = 0,

    // Decoy in a line comment.
    Progress = 1,
    Export
}`;
	assert.deepEqual(extractCSharpEnumMembers(source, "Kind"), ["State", "Progress", "Export"]);
});

test("reads a client member list from a z.enum array, an as-const array and a string-literal union alike", () => {
	assert.deepEqual(extractQuotedNames('kind: z.enum(["State", "Export"]),', "kind: z.enum([", "]"), ["State", "Export"]);
	assert.deepEqual(extractQuotedNames('const kinds = ["A", "B"] as const;', "const kinds = [", "]"), ["A", "B"]);
	assert.deepEqual(extractQuotedNames('export type K =\n\t| "A"\n\t| "B";', "export type K =", ";"), ["A", "B"]);
});

test("reports a member the server can send that the client omits, and one the client kept after a rename", () => {
	assert.deepEqual(compareEnumMembers(["State", "Export", "EvaluationState"], ["State", "Export"]), {
		missing: ["EvaluationState"],
		stale: [],
	});
	assert.deepEqual(compareEnumMembers(["State"], ["State", "Removed"]), { missing: [], stale: ["Removed"] });
});
