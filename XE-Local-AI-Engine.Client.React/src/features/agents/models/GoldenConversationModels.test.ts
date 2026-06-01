import { describe, expect, it } from "vitest";

import {
	parsePromoteConflictBody,
	toCreatedGoldenConversation,
	toGoldenConversations,
	toGoldenHarvestResult,
} from "@/features/agents/models/GoldenConversationModels";

// Minimal valid DTO for toGoldenConversation tests.
function makeDto(overrides: Record<string, unknown> = {}): unknown {
	return {
		id: "g-10",
		agentDefinitionId: "agent-1",
		title: "T",
		inputTurns: [{ role: "user", text: "hi" }],
		assertion: null,
		rubric: "Is it right?",
		enabled: true,
		source: "manual",
		sourceMessageId: null,
		sourceConversationId: null,
		createdAtUtc: 1,
		updatedAtUtc: 2,
		...overrides,
	};
}

describe("GoldenConversationModels", () => {
	it("parses a golden case list envelope, normalizing a null assertion/rubric", () => {
		const result = toGoldenConversations({
			items: [
				{
					id: "g-1",
					agentDefinitionId: "agent-1",
					title: "Summarizes",
					inputTurns: [{ role: "user", text: "Summarize" }],
					assertion: { requiredPhrases: ["summary"], forbiddenPhrases: ["error"] },
					rubric: null,
					enabled: true,
					createdAtUtc: 1,
					updatedAtUtc: 2,
				},
				{
					id: "g-2",
					agentDefinitionId: "agent-1",
					title: "Judged",
					inputTurns: [{ role: "user", text: "Explain" }],
					// assertion omitted entirely; rubric present (the judge path).
					rubric: "Is the explanation correct?",
					enabled: false,
					createdAtUtc: 3,
					updatedAtUtc: 4,
				},
			],
		});

		expect(result).toHaveLength(2);
		expect(result[0]?.assertion?.requiredPhrases).toEqual(["summary"]);
		expect(result[0]?.rubric).toBeNull();
		expect(result[1]?.assertion).toBeNull();
		expect(result[1]?.rubric).toBe("Is the explanation correct?");
		expect(result[1]?.enabled).toBe(false);
	});

	it("throws when the golden list payload does not match the contract", () => {
		expect(() => toGoldenConversations({ items: [{ id: "g-1" }] })).toThrow(/Invalid golden conversations payload/);
	});

	it("parses a created golden case from the bare (non-envelope) POST response", () => {
		const created = toCreatedGoldenConversation({
			id: "g-3",
			agentDefinitionId: "agent-1",
			title: "Created",
			inputTurns: [{ role: "user", text: "Hi" }],
			assertion: { requiredPhrases: [], forbiddenPhrases: ["oops"] },
			rubric: null,
			enabled: true,
			createdAtUtc: 5,
			updatedAtUtc: 6,
		});

		expect(created.id).toBe("g-3");
		expect(created.assertion?.forbiddenPhrases).toEqual(["oops"]);
	});

	it("parses a well-formed 409 promote-conflict body", () => {
		const body = parsePromoteConflictBody({ status: "EvalRequired", reason: "Run the eval first." });

		expect(body).toEqual({ status: "EvalRequired", reason: "Run the eval first." });
	});

	it("returns null for an unknown/garbage promote-conflict body", () => {
		expect(parsePromoteConflictBody({ status: "Nope", reason: "x" })).toBeNull();
		expect(parsePromoteConflictBody({ detail: "RFC7807 problem details" })).toBeNull();
		expect(parsePromoteConflictBody(null)).toBeNull();
	});
});

describe("toGoldenConversation — source / provenance mapping", () => {
	it("maps source='manual' and null provenance ids", () => {
		// Exercise through the schema boundary so Zod validates and normalises the payload.
		const result = toCreatedGoldenConversation(makeDto({ source: "manual" }));

		expect(result.source).toBe("manual");
		expect(result.sourceMessageId).toBeNull();
		expect(result.sourceConversationId).toBeNull();
	});

	it("maps source='harvested' with provenance ids", () => {
		const result = toCreatedGoldenConversation(
			makeDto({
				source: "harvested",
				sourceMessageId: "msg-abc",
				sourceConversationId: "conv-xyz",
			}),
		);

		expect(result.source).toBe("harvested");
		expect(result.sourceMessageId).toBe("msg-abc");
		expect(result.sourceConversationId).toBe("conv-xyz");
	});

	it("degrades an unknown/garbage source value to 'manual' via the .catch fallback (schema boundary)", () => {
		// The Zod schema uses .catch("manual") so unrecognised literals silently normalise.
		// The degradation happens at the schema boundary (safeParse), so exercise it through
		// toCreatedGoldenConversation which validates an unknown payload against the schema.
		const result = toCreatedGoldenConversation(makeDto({ source: "garbage_unknown_value" }));

		expect(result.source).toBe("manual");
	});
});

describe("toGoldenHarvestResult", () => {
	it("parses a valid harvest counts payload", () => {
		const result = toGoldenHarvestResult({
			thumbsUpScanned: 10,
			createdCount: 3,
			duplicateCount: 2,
			skippedCount: 5,
		});

		expect(result.thumbsUpScanned).toBe(10);
		expect(result.createdCount).toBe(3);
		expect(result.duplicateCount).toBe(2);
		expect(result.skippedCount).toBe(5);
	});

	it("throws on a malformed harvest payload (safeParse path)", () => {
		expect(() => toGoldenHarvestResult({ thumbsUpScanned: "not-a-number" })).toThrow(/Invalid golden harvest payload/);
		expect(() => toGoldenHarvestResult(null)).toThrow(/Invalid golden harvest payload/);
		expect(() => toGoldenHarvestResult({ createdCount: 1 })).toThrow(/Invalid golden harvest payload/);
	});
});
