// The config drawers' field-level schemas: each one accepts what the server's save gate accepts and names the field
// that has to change otherwise.

import { describe, expect, it } from "vitest";

import {
	agentConfigSchema,
	chatInputConfigSchema,
	conditionConfigSchema,
	decisionModelConfigSchema,
	edgeConditionSchema,
	endConfigSchema,
	nodeCommonSchema,
	pauseConfigSchema,
	startConfigSchema,
	toolConfigSchema,
} from "@/features/graphWorkflows/models/GraphWorkflowConfigSchemas";

describe("config form schemas", () => {
	/** A valid llm choice DecisionModel as the drawer holds it; each case overrides one member. */
	const decisionForm = {
		question: "Q",
		questionType: null,
		labels: ["a", "b"],
		criteria: {},
		provider: null,
		model: null,
		inputBindings: [],
	};

	it("accepts the eight-node graph's own config shapes", () => {
		expect(startConfigSchema.safeParse({ inputSchema: null, defaultInput: null, aliases: [] }).success).toBe(true);
		expect(
			agentConfigSchema.safeParse({
				agentDefinitionId: null,
				instructions: "Summarise it.",
				model: null,
				reasoningEffort: null,
				responseJsonSchema: '{ "type": "object" }',
				includeUpstreamOutputs: true,
			}).success,
		).toBe(true);
		expect(
			toolConfigSchema.safeParse({ toolName: "read_file", argumentsJson: "", argumentBindings: [], allowedUrls: [] }).success,
		).toBe(true);
		expect(conditionConfigSchema.safeParse({ path: "output.json.requiresReview" }).success).toBe(true);
		// The server's `IsDotPath` accepts any segment without whitespace or wildcard punctuation, and a JSON property
		// really can be hyphenated — refusing one it accepts would block a save the node would have taken.
		expect(conditionConfigSchema.safeParse({ path: "output.json.requires-review" }).success).toBe(true);
		// An empty segment is what `GraphWorkflowTokens.IsDotPath` refuses, and the client mirror has to refuse it
		// too — otherwise the field reads green and the save comes back 400 naming a path the drawer said was fine.
		for (const path of ["a..b", ".a", "a.", "."]) {
			expect(conditionConfigSchema.safeParse({ path }).success).toBe(false);
		}
		expect(
			pauseConfigSchema.safeParse({ prompt: "Approve?", allowedDecisions: ["Approve"], requireComment: false }).success,
		).toBe(true);
		expect(endConfigSchema.safeParse({ outcome: "completed", resultPath: null }).success).toBe(true);
		expect(nodeCommonSchema.safeParse({ key: "analyze", label: "Analyze", maxAttempts: 3, timeoutSeconds: null }).success).toBe(
			true,
		);
	});

	// `char.IsWhiteSpace` and JavaScript's `\s` are not the same set, and they disagree in BOTH directions. Each of
	// these two characters is on the opposite side of the two rules, so `\s` would fail one of these assertions.
	it("answers the server on the two whitespace characters JavaScript disagrees with it about", () => {
		// U+0085 is whitespace to .NET and not to `\s`: the field would read green on a path the save then 400s.
		expect(conditionConfigSchema.safeParse({ path: "output.\u0085json" }).success).toBe(false);
		// U+FEFF is whitespace to `\s` and not to .NET: the field would refuse a path the server stores happily.
		expect(conditionConfigSchema.safeParse({ path: "out\uFEFFput.json" }).success).toBe(true);
	});

	it("accepts a native score form and a native yes/no form with no labels", () => {
		const native = { ...decisionForm, provider: "systemone", model: "laya" };
		expect(decisionModelConfigSchema.safeParse({ ...native, questionType: "score", labels: ["low", "high"] }).success).toBe(true);
		expect(decisionModelConfigSchema.safeParse({ ...native, questionType: "noul", labels: [] }).success).toBe(true);
	});

	it("answers an i18n KEY, not a sentence, for every field it refuses", () => {
		const cases = [
			[chatInputConfigSchema, { prompt: " " }, "pages.graphWorkflows.form.prompt.required"],
			[decisionModelConfigSchema, { ...decisionForm, question: "" }, "pages.graphWorkflows.form.question.required"],
			[decisionModelConfigSchema, { ...decisionForm, labels: ["a"] }, "pages.graphWorkflows.form.labels.invalid"],
			[
				decisionModelConfigSchema,
				{
					...decisionForm,
					questionType: "score",
					provider: "systemone",
					model: "m",
					labels: ["1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11"],
				},
				"pages.graphWorkflows.form.labels.levelsInvalid",
			],
			[
				decisionModelConfigSchema,
				{ ...decisionForm, questionType: "noul", labels: [] },
				"pages.graphWorkflows.form.questionType.unsupportedByProvider",
			],
			[
				decisionModelConfigSchema,
				{ ...decisionForm, provider: "systemone", model: " " },
				"pages.graphWorkflows.form.model.decisionRequired",
			],
			[nodeCommonSchema, { key: "not a key", label: "" }, "pages.graphWorkflows.form.key.invalid"],
			[nodeCommonSchema, { key: "a", label: "", maxAttempts: 101 }, "pages.graphWorkflows.form.maxAttempts.range"],
			[nodeCommonSchema, { key: "a", label: "", timeoutSeconds: 0 }, "pages.graphWorkflows.form.timeoutSeconds.min"],
			[
				startConfigSchema,
				{ inputSchema: "[1]", defaultInput: null, aliases: [] },
				"pages.graphWorkflows.form.inputSchema.notObject",
			],
			[
				startConfigSchema,
				{ inputSchema: null, defaultInput: "{ half", aliases: [] },
				"pages.graphWorkflows.form.defaultInput.invalidJson",
			],
			[
				startConfigSchema,
				{ inputSchema: null, defaultInput: null, aliases: Array.from({ length: 33 }, (_, index) => `alias ${index}`) },
				"pages.graphWorkflows.form.aliases.tooMany",
			],
			[
				startConfigSchema,
				{ inputSchema: null, defaultInput: null, aliases: ["   "] },
				"pages.graphWorkflows.form.aliases.invalid",
			],
			[
				startConfigSchema,
				{ inputSchema: null, defaultInput: null, aliases: ["?!"] },
				"pages.graphWorkflows.form.aliases.invalid",
			],
			[
				startConfigSchema,
				{ inputSchema: null, defaultInput: null, aliases: ["x".repeat(121)] },
				"pages.graphWorkflows.form.aliases.invalid",
			],
			[
				startConfigSchema,
				{ inputSchema: null, defaultInput: null, aliases: ["Lights on", "lights   ON!"] },
				"pages.graphWorkflows.form.aliases.duplicate",
			],
			[
				agentConfigSchema,
				{
					agentDefinitionId: null,
					instructions: "  ",
					model: null,
					reasoningEffort: null,
					responseJsonSchema: null,
					includeUpstreamOutputs: false,
				},
				"pages.graphWorkflows.form.instructions.required",
			],
			[
				agentConfigSchema,
				{
					agentDefinitionId: null,
					instructions: "x",
					model: null,
					reasoningEffort: null,
					responseJsonSchema: "3",
					includeUpstreamOutputs: false,
				},
				"pages.graphWorkflows.form.responseJsonSchema.notObject",
			],
			[
				toolConfigSchema,
				{ toolName: null, argumentsJson: "", argumentBindings: [] },
				"pages.graphWorkflows.form.toolName.required",
			],
			[
				toolConfigSchema,
				{ toolName: "read_file", argumentsJson: "nope", argumentBindings: [] },
				"pages.graphWorkflows.form.argumentsJson.notObject",
			],
			[
				toolConfigSchema,
				{ toolName: "read_file", argumentsJson: "", argumentBindings: [{ parameter: "path", path: " " }] },
				"pages.graphWorkflows.form.argumentBindings.pathRequired",
			],
			[conditionConfigSchema, { path: "output[0].json" }, "pages.graphWorkflows.form.path.invalid"],
			[
				pauseConfigSchema,
				{ prompt: "?", allowedDecisions: [], requireComment: false },
				"pages.graphWorkflows.form.allowedDecisions.required",
			],
			[
				pauseConfigSchema,
				{ prompt: " ", allowedDecisions: ["Approve"], requireComment: false },
				"pages.graphWorkflows.form.prompt.required",
			],
			[endConfigSchema, { outcome: "", resultPath: null }, "pages.graphWorkflows.form.outcome.required"],
			[endConfigSchema, { outcome: "done", resultPath: "a b" }, "pages.graphWorkflows.form.resultPath.invalid"],
			[edgeConditionSchema, { op: "Eq", value: "" }, "pages.graphWorkflows.form.condition.valueRequired"],
			[edgeConditionSchema, { op: "Eq", value: "1", path: "a b" }, "pages.graphWorkflows.form.path.invalid"],
		] as const;

		for (const [schema, value, expected] of cases) {
			const result = schema.safeParse(value);
			expect(result.success, expected).toBe(false);
			expect(
				result.error?.issues.map((issue) => issue.message),
				expected,
			).toContain(expected);
		}
	});

	it("takes no value for Exists and NotExists, which have no operand", () => {
		expect(edgeConditionSchema.safeParse({ op: "Exists", value: "" }).success).toBe(true);
		expect(edgeConditionSchema.safeParse({ op: "NotExists", value: "" }).success).toBe(true);
	});
});

describe("toolConfigSchema — web_fetch allow-list (mirrors the server save gate)", () => {
	const node = (toolName: string, allowedUrls: readonly string[]) => ({
		toolName,
		argumentsJson: "",
		argumentBindings: [],
		allowedUrls,
	});
	const issue = (toolName: string, allowedUrls: readonly string[]) => {
		const result = toolConfigSchema.safeParse(node(toolName, allowedUrls));
		return result.success ? undefined : result.error.issues.find((entry) => entry.path[0] === "allowedUrls")?.message;
	};

	it("requires at least one link on web_fetch", () => {
		expect(issue("web_fetch", [])).toBe("pages.graphWorkflows.form.allowedUrls.required");
	});

	it("refuses a link that is not an absolute http or https URL", () => {
		for (const entry of ["ftp://files.example.com/", "/docs", "   "]) {
			expect(issue("web_fetch", ["https://docs.example.com/", entry])).toBe("pages.graphWorkflows.form.allowedUrls.invalid");
		}
	});

	it("refuses a literal url argument that is not under any allowed link, on a path-segment boundary", () => {
		const literalIssue = (url: unknown, allowedUrls: readonly string[] = ["https://docs.example.com/guide"]) => {
			const result = toolConfigSchema.safeParse({ ...node("web_fetch", allowedUrls), argumentsJson: JSON.stringify({ url }) });
			return result.success ? undefined : result.error.issues.find((entry) => entry.path[0] === "allowedUrls")?.message;
		};
		const refused = "pages.graphWorkflows.form.allowedUrls.literalUrlNotAllowed";

		expect(literalIssue("https://docs.example.com/guide")).toBeUndefined();
		expect(literalIssue("https://docs.example.com/guide/intro")).toBeUndefined();
		expect(literalIssue("https://DOCS.example.com:443/guide/x")).toBeUndefined();
		expect(literalIssue("https://docs.example.com/any", ["https://docs.example.com"])).toBeUndefined();
		expect(literalIssue("https://docs.example.com/guide-private")).toBe(refused);
		expect(literalIssue("https://docs.example.com/guide/../admin")).toBe(refused);
		expect(literalIssue("http://docs.example.com/guide")).toBe(refused);
		expect(literalIssue("https://docs.example.com:8443/guide")).toBe(refused);
		expect(literalIssue("https://evil.example.com/guide")).toBe(refused);
		expect(literalIssue(42)).toBe(refused);
		// No literal url (the model or a binding supplies it at run time) asks nothing.
		const bound = toolConfigSchema.safeParse({
			...node("web_fetch", ["https://docs.example.com/"]),
			argumentsJson: '{"maxChars":100}',
		});
		expect(bound.success).toBe(true);
	});

	it("accepts http and https links, and asks nothing of another tool", () => {
		expect(issue("web_fetch", ["https://docs.example.com/guide", "http://status.example.com"])).toBeUndefined();
		expect(issue("read_file", [])).toBeUndefined();
	});
});
