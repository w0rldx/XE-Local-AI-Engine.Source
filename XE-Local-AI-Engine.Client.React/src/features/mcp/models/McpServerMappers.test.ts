import { describe, expect, it } from "vitest";

import type {
	XeLocalAiEngineClientEndpointsMcpV1McpServerResponse,
	XeLocalAiEngineClientEndpointsMcpV1McpServerToolsResponse,
} from "@/core/api/generated";
import { toMcpServerRegistration, toMcpServerToolsView, toSaveMcpServerRequest } from "@/features/mcp/models/McpServerMappers";
import type { McpServerFormValues } from "@/features/mcp/models/McpServerModels";

// Env var names are conventionally UPPER_SNAKE, which biome's useNamingConvention rejects as object-literal keys —
// build the maps dynamically so the realistic names survive without disabling the rule.
const sampleEnvMap = Object.fromEntries([
	["TOKEN", "secret"],
	["PATH_EXTRA", "/x"],
]);
const tokenOnlyEnvMap = Object.fromEntries([["TOKEN", "secret"]]);

function makeStdioResponse(
	overrides: Partial<XeLocalAiEngineClientEndpointsMcpV1McpServerResponse> = {},
): XeLocalAiEngineClientEndpointsMcpV1McpServerResponse {
	return {
		id: "mcp-1",
		name: "Filesystem tools",
		description: "Local FS",
		transportKind: "Stdio",
		command: "/usr/bin/fs-mcp",
		arguments: ["--stdio"],
		workingDirectory: "/work",
		env: sampleEnvMap,
		url: null,
		trustTier: "Sandboxed",
		headers: {},
		sessionScope: "Shared",
		enabled: false,
		version: 1,
		createdAtUtc: 1000,
		updatedAtUtc: 2000,
		...overrides,
	};
}

const stdioForm: McpServerFormValues = {
	name: "  Filesystem tools  ",
	description: "  Local FS  ",
	transportKind: "Stdio",
	command: "  /usr/bin/fs-mcp  ",
	arguments: ["--stdio", ""],
	workingDirectory: "  /work  ",
	env: [
		{ key: "TOKEN", value: "secret" },
		{ key: "  ", value: "dropped-no-key" },
	],
	url: "http://127.0.0.1:3001/sse",
	headers: [{ key: "Authorization", value: "dropped-for-stdio" }],
	trustTier: "PrivilegedHost",
	sessionScope: "PerConversation",
};

const httpForm: McpServerFormValues = {
	name: "Remote bridge",
	description: "",
	transportKind: "Http",
	command: "/leftover/command",
	arguments: ["--leftover"],
	workingDirectory: "/leftover",
	env: [{ key: "LEFT", value: "over" }],
	url: "  http://localhost:4000/sse  ",
	headers: [
		{ key: "Authorization", value: "Bearer abc" },
		{ key: " ", value: "dropped-no-name" },
	],
	// Deliberately privileged so the mapper's "inert for HTTP" normalization is asserted, not assumed.
	trustTier: "PrivilegedHost",
	sessionScope: "Shared",
};

describe("toMcpServerRegistration", () => {
	it("maps a generated server response to the domain registration", () => {
		const registration = toMcpServerRegistration(makeStdioResponse());

		expect(registration.id).toBe("mcp-1");
		expect(registration.transportKind).toBe("Stdio");
		expect(registration.env).toEqual([
			{ key: "TOKEN", value: "secret" },
			{ key: "PATH_EXTRA", value: "/x" },
		]);
		expect(registration.arguments).toEqual(["--stdio"]);
	});

	it("coalesces omitted optional fields to safe defaults (null description/env/args)", () => {
		// The generated response makes every field optional; an omitted field must coalesce to a total domain value.
		const registration = toMcpServerRegistration(makeStdioResponse({ description: null, env: undefined, arguments: undefined }));

		expect(registration.description).toBe("");
		expect(registration.env).toEqual([]);
		expect(registration.arguments).toEqual([]);
	});
});

describe("toSaveMcpServerRequest", () => {
	it("builds a stdio save request: trims fields, drops blank args, drops keyless env, nulls http url", () => {
		const request = toSaveMcpServerRequest(stdioForm);

		expect(request.name).toBe("Filesystem tools");
		expect(request.description).toBe("Local FS");
		expect(request.transportKind).toBe("Stdio");
		expect(request.command).toBe("/usr/bin/fs-mcp");
		expect(request.arguments).toEqual(["--stdio"]);
		expect(request.workingDirectory).toBe("/work");
		expect(request.env).toEqual(tokenOnlyEnvMap);
		expect(request.url).toBeNull();
		// Headers only ever reach an HTTP server.
		expect(request.headers).toEqual({});
		expect(request.sessionScope).toBe("PerConversation");
	});

	it("builds an http save request: keeps loopback url, strips all stdio leftovers", () => {
		const request = toSaveMcpServerRequest(httpForm);

		expect(request.transportKind).toBe("Http");
		expect(request.url).toBe("http://localhost:4000/sse");
		expect(request.command).toBeNull();
		expect(request.arguments).toEqual([]);
		expect(request.workingDirectory).toBeNull();
		expect(request.env).toEqual({});
		expect(request.headers).toEqual({ Authorization: "Bearer abc" });
		expect(request.sessionScope).toBe("Shared");
	});
});

describe("toMcpServerToolsView", () => {
	it("maps the connected status and discovered tools, coalescing a null description", () => {
		const view = toMcpServerToolsView({
			status: "connected",
			error: null,
			tools: [{ name: "mcp__fs__read", description: null, requiresApproval: true }],
		});

		expect(view.status).toBe("connected");
		expect(view.error).toBeNull();
		expect(view.tools[0]).toEqual({ name: "mcp__fs__read", description: "", requiresApproval: true });
	});

	it("falls back to the disabled status and an empty tool list when the response is empty", () => {
		const view = toMcpServerToolsView({} as XeLocalAiEngineClientEndpointsMcpV1McpServerToolsResponse);

		expect(view.status).toBe("disabled");
		expect(view.error).toBeNull();
		expect(view.tools).toEqual([]);
	});

	it("surfaces the redacted connection error verbatim", () => {
		const view = toMcpServerToolsView({ status: "error", error: "redacted reason", tools: [] });

		expect(view.status).toBe("error");
		expect(view.error).toBe("redacted reason");
		expect(view.failureReason).toBeNull();
	});

	it("carries the failure reason", () => {
		const view = toMcpServerToolsView({ status: "error", error: "x", failureReason: "ServerNotFound", tools: [] });

		expect(view.failureReason).toBe("ServerNotFound");
	});
});
