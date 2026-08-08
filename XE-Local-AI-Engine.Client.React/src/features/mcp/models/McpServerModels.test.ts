import { describe, expect, it } from "vitest";

import { isLoopbackUrl, mcpServerFormSchema, type McpServerFormValues } from "@/features/mcp/models/McpServerModels";

function baseForm(overrides: Partial<McpServerFormValues> = {}): McpServerFormValues {
	return {
		name: "Server",
		description: "",
		transportKind: "Stdio",
		command: "/usr/bin/srv",
		arguments: [],
		workingDirectory: "",
		env: [],
		url: "",
		...overrides,
	};
}

function issuePaths(form: McpServerFormValues): string[] {
	const result = mcpServerFormSchema.safeParse(form);
	if (result.success) {
		return [];
	}
	return result.error.issues.map((issue) => issue.path.join("."));
}

describe("isLoopbackUrl", () => {
	it.each([
		["http://127.0.0.1:3001/sse", true],
		["http://localhost:4000", true],
		["https://[::1]:5000/sse", true],
		["http://example.com/sse", false],
		["http://192.168.1.5:3001", false],
		["ftp://127.0.0.1", false],
		["not a url", false],
	])("classifies %s as loopback=%s", (url, expected) => {
		expect(isLoopbackUrl(url)).toBe(expected);
	});
});

describe("mcpServerFormSchema", () => {
	it("accepts a valid stdio server", () => {
		expect(issuePaths(baseForm())).toEqual([]);
	});

	it("accepts a valid http loopback server", () => {
		const form = baseForm({ transportKind: "Http", command: "", url: "http://127.0.0.1:3001/sse" });
		expect(issuePaths(form)).toEqual([]);
	});

	it("rejects an empty name", () => {
		expect(issuePaths(baseForm({ name: "  " }))).toContain("name");
	});

	it("requires a command for stdio transport", () => {
		expect(issuePaths(baseForm({ command: "  " }))).toContain("command");
	});

	it("requires a url for http transport", () => {
		const form = baseForm({ transportKind: "Http", command: "", url: "  " });
		expect(issuePaths(form)).toContain("url");
	});

	it("rejects a non-loopback http url", () => {
		const form = baseForm({ transportKind: "Http", command: "", url: "http://example.com/sse" });
		expect(issuePaths(form)).toContain("url");
	});

	it("rejects an env value with no key", () => {
		const form = baseForm({ env: [{ key: "  ", value: "orphan" }] });
		expect(issuePaths(form)).toContain("env.0.key");
	});

	it("allows a fully blank env row (no key and no value)", () => {
		const form = baseForm({ env: [{ key: "", value: "" }] });
		expect(issuePaths(form)).toEqual([]);
	});
});
