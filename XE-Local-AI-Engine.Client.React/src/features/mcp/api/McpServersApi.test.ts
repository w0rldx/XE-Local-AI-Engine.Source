import { describe, expect, it, vi } from "vitest";

const { axiosInstanceMock, buildLocalApiUrlMock } = vi.hoisted(() => ({
	axiosInstanceMock: {
		delete: vi.fn(),
		get: vi.fn(),
		patch: vi.fn(),
		post: vi.fn(),
		put: vi.fn(),
	},
	buildLocalApiUrlMock: vi.fn((path: string) => `/local/${path}`),
}));

vi.mock("@/core/api/axios/AxiosInstance", () => ({
	axiosInstance: axiosInstanceMock,
}));

vi.mock("@/core/api/utils/LocalApiUrl", () => ({
	buildLocalApiUrl: buildLocalApiUrlMock,
}));

import {
	createMcpServer,
	deleteMcpServer,
	getMcpServerTools,
	listMcpServers,
	type McpServerDto,
	setMcpServerEnabled,
	toMcpServerRegistration,
	toSaveMcpServerRequest,
	updateMcpServer,
} from "@/features/mcp/api/McpServersApi";
import type { McpServerFormValues } from "@/features/mcp/models/McpServerModels";

// Env var names are conventionally UPPER_SNAKE, which biome's useNamingConvention rejects as object-literal
// keys — build the maps dynamically so the realistic names survive without disabling the rule.
const sampleEnvMap = Object.fromEntries([
	["TOKEN", "secret"],
	["PATH_EXTRA", "/x"],
]);
const tokenOnlyEnvMap = Object.fromEntries([["TOKEN", "secret"]]);

function makeStdioDto(overrides: Partial<McpServerDto> = {}): McpServerDto {
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
};

describe("MCP servers API", () => {
	it("lists MCP servers, mapping DTOs and forwarding the abort signal", async () => {
		const abortController = new AbortController();
		axiosInstanceMock.get.mockResolvedValue({ data: { items: [makeStdioDto()] } });

		const result = await listMcpServers({ signal: abortController.signal });

		expect(axiosInstanceMock.get).toHaveBeenCalledWith("/local/mcp/servers", {
			signal: abortController.signal,
		});
		expect(result).toHaveLength(1);
		expect(result[0]?.env).toEqual([
			{ key: "TOKEN", value: "secret" },
			{ key: "PATH_EXTRA", value: "/x" },
		]);
		expect(result[0]?.arguments).toEqual(["--stdio"]);
	});

	it("maps a null description to an empty string and a null env map to an empty list", () => {
		const registration = toMcpServerRegistration(
			makeStdioDto({ description: null, env: null, arguments: null }),
		);

		expect(registration.description).toBe("");
		expect(registration.env).toEqual([]);
		expect(registration.arguments).toEqual([]);
	});

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
	});

	it("builds an http save request: keeps loopback url, strips all stdio leftovers", () => {
		const request = toSaveMcpServerRequest(httpForm);

		expect(request.transportKind).toBe("Http");
		expect(request.url).toBe("http://localhost:4000/sse");
		expect(request.command).toBeNull();
		expect(request.arguments).toEqual([]);
		expect(request.workingDirectory).toBeNull();
		expect(request.env).toEqual({});
	});

	it("creates an MCP server through POST", async () => {
		axiosInstanceMock.post.mockResolvedValue({ data: makeStdioDto() });

		const result = await createMcpServer(toSaveMcpServerRequest(stdioForm));

		expect(axiosInstanceMock.post).toHaveBeenCalledWith(
			"/local/mcp/servers",
			expect.objectContaining({ name: "Filesystem tools" }),
			undefined,
		);
		expect(result.id).toBe("mcp-1");
	});

	it("updates an MCP server through PUT with an encoded id", async () => {
		axiosInstanceMock.put.mockResolvedValue({ data: makeStdioDto({ id: "a/b" }) });

		await updateMcpServer("a/b", toSaveMcpServerRequest(stdioForm));

		expect(axiosInstanceMock.put).toHaveBeenCalledWith(
			"/local/mcp/servers/a%2Fb",
			expect.any(Object),
			undefined,
		);
	});

	it("deletes an MCP server through DELETE with an encoded id", async () => {
		axiosInstanceMock.delete.mockResolvedValue({ data: undefined });

		await deleteMcpServer("mcp-1");

		expect(axiosInstanceMock.delete).toHaveBeenCalledWith("/local/mcp/servers/mcp-1", undefined);
	});

	it("toggles enabled through PATCH", async () => {
		axiosInstanceMock.patch.mockResolvedValue({ data: makeStdioDto({ enabled: true }) });

		const result = await setMcpServerEnabled("mcp-1", true);

		expect(axiosInstanceMock.patch).toHaveBeenCalledWith(
			"/local/mcp/servers/mcp-1/enabled",
			{ enabled: true },
			undefined,
		);
		expect(result.enabled).toBe(true);
	});

	it("reads discovered tools + connection status for a server", async () => {
		axiosInstanceMock.get.mockResolvedValue({
			data: {
				status: "connected",
				error: null,
				tools: [{ name: "mcp__fs__read", description: null, requiresApproval: true }],
			},
		});

		const result = await getMcpServerTools("mcp-1");

		expect(axiosInstanceMock.get).toHaveBeenCalledWith("/local/mcp/servers/mcp-1/tools", undefined);
		expect(result.status).toBe("connected");
		expect(result.tools[0]).toEqual({
			name: "mcp__fs__read",
			description: "",
			requiresApproval: true,
		});
	});
});
