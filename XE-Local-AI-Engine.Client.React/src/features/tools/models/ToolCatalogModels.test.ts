import { describe, expect, it } from "vitest";

import { parseToolCatalogSource, toToolDisplayName } from "@/features/tools/models/ToolCatalogModels";

describe("parseToolCatalogSource", () => {
	it("parses a built-in source", () => {
		expect(parseToolCatalogSource("builtin")).toEqual({ kind: "builtin", serverSlug: null });
	});

	it("parses a qualified mcp source into kind + server slug", () => {
		expect(parseToolCatalogSource("mcp:filesystem-tools")).toEqual({
			kind: "mcp",
			serverSlug: "filesystem-tools",
		});
	});

	it("treats a bare 'mcp' source as mcp with no slug", () => {
		expect(parseToolCatalogSource("mcp")).toEqual({ kind: "mcp", serverSlug: null });
	});

	it("treats an 'mcp:' source with an empty slug as mcp with no slug", () => {
		expect(parseToolCatalogSource("mcp:")).toEqual({ kind: "mcp", serverSlug: null });
	});

	it("falls back to built-in for an unrecognized source string", () => {
		expect(parseToolCatalogSource("something-else")).toEqual({ kind: "builtin", serverSlug: null });
	});
});

describe("toToolDisplayName", () => {
	it("returns a built-in tool name unchanged", () => {
		expect(toToolDisplayName("GetCurrentTime")).toBe("GetCurrentTime");
	});

	it("strips the mcp__{serverSlug}__ prefix from a qualified MCP tool name", () => {
		expect(toToolDisplayName("mcp__filesystem-tools__read")).toBe("read");
	});

	it("preserves a tool segment that itself contains a double underscore", () => {
		expect(toToolDisplayName("mcp__server__read__file")).toBe("read__file");
	});

	it("returns the original name when the mcp prefix has no server delimiter", () => {
		expect(toToolDisplayName("mcp__only-one-segment")).toBe("mcp__only-one-segment");
	});

	it("returns the original name when the tool segment is empty", () => {
		expect(toToolDisplayName("mcp__server__")).toBe("mcp__server__");
	});

	it("leaves a non-mcp name that merely contains underscores unchanged", () => {
		expect(toToolDisplayName("some_tool_name")).toBe("some_tool_name");
	});
});
