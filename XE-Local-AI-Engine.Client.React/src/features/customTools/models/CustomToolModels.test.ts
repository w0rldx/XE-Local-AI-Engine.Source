import { describe, expect, it } from "vitest";

import {
	CUSTOM_TOOL_NAME_PREFIX,
	CUSTOM_TOOL_TIMEOUT_MAX,
	customToolFormSchema,
	type CustomToolFormValues,
	toSlug,
} from "@/features/customTools/models/CustomToolModels";

// The schema mirrors CustomToolService.ValidateAsync so an obviously-invalid definition fails the same way on both
// sides. The backend stays authoritative — these cases pin the rules the client is allowed to pre-empt, and in
// particular the safety-relevant ones: the mandatory danger acknowledgement and "Fixed declares no parameters".
//
// The per-kind block requirements live in superRefine, NOT on the base object: only the active kind's block is
// submitted (toDefinition) and only its editor is rendered (CustomToolForm), so an unconditional requirement made
// the empty create form unsavable for either kind with the error attached to an off-screen field. See
// "ignores the inactive kind's empty block" below.

function values(overrides: Partial<CustomToolFormValues> = {}): CustomToolFormValues {
	return {
		name: "fetch_status",
		description: "Fetches a status page.",
		kind: "HttpFetch",
		mode: "Fixed",
		enabled: false,
		acknowledged: true,
		parameters: [],
		http: { method: "GET", urlTemplate: "https://example.test/status", headers: [], bodyTemplate: "", allowedHosts: [] },
		command: { executable: "/usr/bin/env", argsTemplate: [], workingDirectory: "", timeoutSeconds: 0, env: [] },
		...overrides,
	};
}

/** The `message` of every issue a parse produced, so a case can name the rule it expects to trip. */
function issues(input: unknown): string[] {
	const result = customToolFormSchema.safeParse(input);
	return result.success ? [] : result.error.issues.map((issue) => issue.message);
}

/** The dotted path of every issue a parse produced, so a case can assert where the form will surface it. */
function issuePaths(input: unknown): string[] {
	const result = customToolFormSchema.safeParse(input);
	return result.success ? [] : result.error.issues.map((issue) => issue.path.join("."));
}

describe("customToolFormSchema", () => {
	it("accepts a fully populated acknowledged tool", () => {
		expect(customToolFormSchema.safeParse(values()).success).toBe(true);
	});

	// The server enforces the acknowledgement on every create/update, so `false` can never save. Gating it here only
	// makes the requirement visible before the round-trip — it must not be relaxable.
	it("rejects an unacknowledged tool", () => {
		expect(customToolFormSchema.safeParse(values({ acknowledged: false })).success).toBe(false);
	});

	it.each([
		["Uppercase", "Fetch_status"],
		["a leading underscore", "_fetch"],
		["a trailing underscore", "fetch_"],
		["a hyphen", "fetch-status"],
		["an empty slug", ""],
	])("rejects a slug with %s", (_case, name) => {
		expect(customToolFormSchema.safeParse(values({ name })).success).toBe(false);
	});

	it.each([["a1"], ["a"], ["fetch_status_2"]])("accepts the valid slug %s", (name) => {
		expect(customToolFormSchema.safeParse(values({ name })).success).toBe(true);
	});

	it("requires a description", () => {
		expect(customToolFormSchema.safeParse(values({ description: "   " })).success).toBe(false);
	});

	// Cross-field rule: a Fixed tool takes no arguments from the model, so declaring parameters is a contradiction the
	// backend rejects. The issue is attached to `mode` so the form can surface it on a stable path.
	it("rejects parameters on a Fixed tool and attaches the issue to mode", () => {
		const input = values({ parameters: [{ name: "target", type: "string", description: "", required: true }] });

		expect(customToolFormSchema.safeParse(input).success).toBe(false);
		expect(issues(input)).toContain("fixedNoParameters");
		expect(issuePaths(input)).toContain("mode");
	});

	it("accepts the same parameters on a Parameterized tool", () => {
		const input = values({
			mode: "Parameterized",
			parameters: [{ name: "target", type: "string", description: "", required: true }],
		});

		expect(customToolFormSchema.safeParse(input).success).toBe(true);
	});

	it("rejects a parameter name that is not an identifier", () => {
		const input = values({
			mode: "Parameterized",
			parameters: [{ name: "2target", type: "string", description: "", required: true }],
		});

		expect(issues(input)).toContain("paramNameInvalid");
	});

	it("rejects an env var name that is not an identifier", () => {
		const input = values({
			kind: "Command",
			command: { ...values().command, env: [{ name: "2bad", value: "x", isSecret: false }] },
		});

		expect(issues(input)).toContain("envNameInvalid");
	});

	// HostProcessExecutor.MaxTimeoutSeconds; 0 means "use the executor default", so the range is inclusive at both ends.
	it.each([
		[0, true],
		[CUSTOM_TOOL_TIMEOUT_MAX, true],
		[CUSTOM_TOOL_TIMEOUT_MAX + 1, false],
		[-1, false],
	])("bounds the command timeout: %s is valid=%s", (timeoutSeconds, valid) => {
		const input = values({ kind: "Command", command: { ...values().command, timeoutSeconds } });

		expect(customToolFormSchema.safeParse(input).success).toBe(valid);
	});

	// The empty create form (CustomToolsPage `emptyFormValues`) starts with BOTH blocks blank. Requiring the inactive
	// one made Save fail with the issue on a field whose editor is not on screen — no visible error, no save.
	it("ignores the inactive kind's empty block", () => {
		const httpToolWithBlankCommand = values({ command: { ...values().command, executable: "" } });
		const commandToolWithBlankUrl = values({
			kind: "Command",
			http: { method: "GET", urlTemplate: "", headers: [], bodyTemplate: "", allowedHosts: [] },
		});

		expect(customToolFormSchema.safeParse(httpToolWithBlankCommand).success).toBe(true);
		expect(customToolFormSchema.safeParse(commandToolWithBlankUrl).success).toBe(true);
	});

	// The flip side: each kind still has to supply its OWN field, and the issue lands on the path whose editor is
	// on screen so the form can surface it.
	it.each([
		["HttpFetch", { http: { method: "GET", urlTemplate: "  ", headers: [], bodyTemplate: "", allowedHosts: [] } }, "urlRequired", "http.urlTemplate"],
		["Command", { kind: "Command" as const, command: { executable: "  ", argsTemplate: [], workingDirectory: "", timeoutSeconds: 0, env: [] } }, "executableRequired", "command.executable"],
	])("still requires the %s block's own field", (_kind, overrides, message, path) => {
		const input = values(overrides as Partial<CustomToolFormValues>);

		expect(customToolFormSchema.safeParse(input).success).toBe(false);
		expect(issues(input)).toContain(message);
		expect(issuePaths(input)).toContain(path);
	});
});

describe("toSlug", () => {
	it("strips the reserved custom__ prefix", () => {
		expect(toSlug(`${CUSTOM_TOOL_NAME_PREFIX}fetch_status`)).toBe("fetch_status");
	});

	it("leaves an already-bare slug alone", () => {
		expect(toSlug("fetch_status")).toBe("fetch_status");
	});

	it("strips only the leading occurrence", () => {
		expect(toSlug(`${CUSTOM_TOOL_NAME_PREFIX}${CUSTOM_TOOL_NAME_PREFIX}x`)).toBe(`${CUSTOM_TOOL_NAME_PREFIX}x`);
	});
});
