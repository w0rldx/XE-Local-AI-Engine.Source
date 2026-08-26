import { describe, expect, it } from "vitest";

import {
	benchmarkPythonTestsLimits,
	defaultVerifierConfig,
	firstVerifierIssue,
	parseMathAnswerNumber,
	parseVerifierConfig,
	serializeVerifierConfig,
	toBenchmarkCriterionKind,
	verifierConfigIssue,
} from "@/features/benchmarks/models/BenchmarkVerifier";

// These mirror `BenchmarkJudgeVerifierConfig.Parse` on the node. The point is not to replace it — a save re-validates
// everything — but to catch a mistake while it is being typed, because the node's refusal costs a round-trip AND the
// forced re-judge that a judge-policy save implies.

describe("toBenchmarkCriterionKind", () => {
	it("reads an absent or unknown kind as llm, the pre-P2 default", () => {
		expect(toBenchmarkCriterionKind(undefined)).toBe("llm");
		expect(toBenchmarkCriterionKind(null)).toBe("llm");
		expect(toBenchmarkCriterionKind("somethingANewerNodeInvented")).toBe("llm");
	});

	it("passes a known kind through", () => {
		expect(toBenchmarkCriterionKind("jsonSchema")).toBe("jsonSchema");
	});
});

describe("parseVerifierConfig / serializeVerifierConfig", () => {
	// The editor must open on a criterion whose config is broken — that is exactly the one needing to be fixed.
	it("reads a malformed config as empty rather than throwing", () => {
		expect(parseVerifierConfig("{not json")).toEqual({});
		expect(parseVerifierConfig("[1,2]")).toEqual({});
		expect(parseVerifierConfig(null)).toEqual({});
	});

	// An `llm` criterion must carry NO config; `{}` is a config, and the node refuses it.
	it("serializes an empty object back to null, not to {}", () => {
		expect(serializeVerifierConfig({})).toBeNull();
		expect(serializeVerifierConfig({ expected: "42" })).toBe('{"expected":"42"}');
	});
});

describe("defaultVerifierConfig", () => {
	it("gives an llm criterion no configuration at all", () => {
		expect(defaultVerifierConfig("llm")).toBeNull();
	});

	// Deliberately invalid: a plausible default would ship a criterion the operator never read.
	it("starts a verifiable kind in a state the editor reports as incomplete", () => {
		expect(verifierConfigIssue("exact", defaultVerifierConfig("exact"))).toBe("expectedRequired");
		expect(verifierConfigIssue("regex", defaultVerifierConfig("regex"))).toBe("patternRequired");
		expect(verifierConfigIssue("mathAnswer", defaultVerifierConfig("mathAnswer"))).toBe("mathExpected");
		expect(verifierConfigIssue("constraint", defaultVerifierConfig("constraint"))).toBe("constraintEmpty");
	});

	it("starts a jsonSchema criterion valid, because an empty object schema is a schema", () => {
		expect(verifierConfigIssue("jsonSchema", defaultVerifierConfig("jsonSchema"))).toBeNull();
	});
});

describe("verifierConfigIssue", () => {
	it("refuses a configuration on an llm criterion", () => {
		expect(verifierConfigIssue("llm", null)).toBeNull();
		expect(verifierConfigIssue("llm", '{"expected":"42"}')).toBe("llmConfig");
	});

	it("accepts a complete exact criterion", () => {
		expect(verifierConfigIssue("exact", '{"expected":"42","normalize":{"trim":true}}')).toBeNull();
	});

	it("caps a regex pattern at the length the node caps it at", () => {
		expect(verifierConfigIssue("regex", JSON.stringify({ pattern: "a".repeat(512) }))).toBeNull();
		expect(verifierConfigIssue("regex", JSON.stringify({ pattern: "a".repeat(513) }))).toBe("patternTooLong");
	});

	it("catches a pattern that will not compile", () => {
		expect(verifierConfigIssue("regex", '{"pattern":"([unclosed"}')).toBe("patternInvalid");
	});

	// The node's schema subset is enforced rather than hidden: an operator who writes `minLength` and is never told it
	// does nothing has a criterion that passes answers it should fail.
	it("refuses a schema keyword this build does not enforce", () => {
		expect(verifierConfigIssue("jsonSchema", '{"schema":{"type":"string","minLength":3}}')).toBe("schemaKeyword");
	});

	it("checks nested schema properties and items, not only the top level", () => {
		expect(verifierConfigIssue("jsonSchema", '{"schema":{"type":"object","properties":{"a":{"pattern":"x"}}}}')).toBe("schemaKeyword");
		expect(verifierConfigIssue("jsonSchema", '{"schema":{"type":"array","items":{"minimum":1}}}')).toBe("schemaKeyword");
	});

	it("accepts a schema built from the enforced keywords", () => {
		const schema = '{"schema":{"type":"object","properties":{"total":{"type":"number"}},"required":["total"],"additionalProperties":false}}';
		expect(verifierConfigIssue("jsonSchema", schema)).toBeNull();
	});

	it("accepts a mathAnswer expected value written as a number, a string or a fraction", () => {
		for (const expected of [42, "42", "3/4", "$1,234.50"]) {
			expect(verifierConfigIssue("mathAnswer", JSON.stringify({ expected }))).toBeNull();
		}
	});

	it("refuses a negative tolerance", () => {
		expect(verifierConfigIssue("mathAnswer", '{"expected":42,"absoluteTolerance":-1}')).toBe("toleranceInvalid");
	});

	it("needs at least one constraint", () => {
		expect(verifierConfigIssue("constraint", "{}")).toBe("constraintEmpty");
		expect(verifierConfigIssue("constraint", '{"minWords":10}')).toBeNull();
	});

	it("refuses inconsistent word bounds", () => {
		expect(verifierConfigIssue("constraint", '{"minWords":50,"maxWords":10}')).toBe("constraintWords");
		expect(verifierConfigIssue("constraint", '{"minWords":-1}')).toBe("constraintWords");
	});

	it("refuses an empty contains entry and an unsupported format", () => {
		expect(verifierConfigIssue("constraint", '{"mustContain":["ok",""]}')).toBe("constraintContains");
		expect(verifierConfigIssue("constraint", '{"format":"yaml"}')).toBe("constraintFormat");
		expect(verifierConfigIssue("constraint", '{"format":"markdownList"}')).toBeNull();
	});
});

describe("parseMathAnswerNumber", () => {
	it("reads the forms a model writes a number in", () => {
		expect(parseMathAnswerNumber("1,234")).toBe(1234);
		expect(parseMathAnswerNumber("$42.50")).toBe(42.5);
		expect(parseMathAnswerNumber("3/4")).toBe(0.75);
		expect(parseMathAnswerNumber("42.")).toBe(42);
	});

	it("refuses what is not a number rather than reading it as zero", () => {
		expect(parseMathAnswerNumber("forty two")).toBeNull();
		expect(parseMathAnswerNumber("1/0")).toBeNull();
		expect(parseMathAnswerNumber("")).toBeNull();
		expect(parseMathAnswerNumber(undefined)).toBeNull();
	});
});

describe("firstVerifierIssue", () => {
	it("names the first row the node would refuse, so the editor can point at one", () => {
		expect(
			firstVerifierIssue([
				{ kind: "llm", config: null },
				{ kind: "exact", config: '{"expected":"42"}' },
				{ kind: "regex", config: '{"pattern":""}' },
			]),
		).toEqual({ index: 2, issue: "patternRequired" });
	});

	it("is null when every criterion passes the pre-check", () => {
		expect(firstVerifierIssue([{ kind: "llm", config: null }, { kind: "exact", config: '{"expected":"42"}' }])).toBeNull();
	});
});

// pythonTests is decided by RUNNING the answer's code against the operator's tests, so its configuration carries a
// program. The node bounds it at activation because the composed harness has to fit inside the sandbox script
// alongside the candidate's own code — a limit an operator must meet in the form, not an hour into a batch.
describe("pythonTests configuration", () => {
	const config = (overrides: Record<string, unknown> = {}) => JSON.stringify({ testCode: "assert candidate.solve(2) == 4", ...overrides });

	it("accepts the smallest valid configuration", () => {
		expect(verifierConfigIssue("pythonTests", config())).toBeNull();
	});

	it("requires test code, because there is nothing else to decide the criterion by", () => {
		expect(verifierConfigIssue("pythonTests", JSON.stringify({ testCode: "  " }))).toBe("testCodeRequired");
		expect(verifierConfigIssue("pythonTests", null)).toBe("testCodeRequired");
	});

	it("refuses test code past the node's activation cap", () => {
		expect(verifierConfigIssue("pythonTests", config({ testCode: "x".repeat(benchmarkPythonTestsLimits.maxTestCodeLength + 1) }))).toBe(
			"testCodeTooLong",
		);
	});

	// Exports are seeded into the test namespace as bare names, so each has to be something Python can bind.
	it("requires every export to be a plain Python identifier", () => {
		expect(verifierConfigIssue("pythonTests", config({ exports: ["solve", "_helper2"] }))).toBeNull();
		expect(verifierConfigIssue("pythonTests", config({ exports: ["solve()"] }))).toBe("exportsInvalid");
		expect(verifierConfigIssue("pythonTests", config({ exports: ["2fast"] }))).toBe("exportsInvalid");
		expect(
			verifierConfigIssue("pythonTests", config({ exports: Array.from({ length: 17 }, (_, index) => `name${index}`) })),
		).toBe("exportsCap");
	});

	it("bounds the timeout and the extraction mode the same way the node does", () => {
		expect(verifierConfigIssue("pythonTests", config({ timeoutSeconds: 30 }))).toBeNull();
		expect(verifierConfigIssue("pythonTests", config({ timeoutSeconds: 0 }))).toBe("timeoutRange");
		expect(verifierConfigIssue("pythonTests", config({ timeoutSeconds: 601 }))).toBe("timeoutRange");
		expect(verifierConfigIssue("pythonTests", config({ extract: "wholeText" }))).toBeNull();
		expect(verifierConfigIssue("pythonTests", config({ extract: "everything" }))).toBe("extractInvalid");
	});

	// A criterion that starts valid is one the operator never read; the empty test code is deliberate.
	it("starts from an empty program the editor immediately reports as incomplete", () => {
		const started = defaultVerifierConfig("pythonTests");

		expect(verifierConfigIssue("pythonTests", started)).toBe("testCodeRequired");
	});
});
