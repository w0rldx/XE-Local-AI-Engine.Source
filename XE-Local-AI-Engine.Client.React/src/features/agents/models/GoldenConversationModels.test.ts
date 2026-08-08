import { describe, expect, it } from "vitest";

import type { XeLocalAiEngineClientEndpointsAgentsV1GoldenConversationResponse } from "@/core/api/generated";
import {
	toGoldenConversation,
	toGoldenConversations,
	toGoldenHarvestResult,
} from "@/features/agents/models/GoldenConversationMappers";
import {
	findGoldenFieldOverLimit,
	GOLDEN_TITLE_MAX,
	parsePromoteConflictBody,
} from "@/features/agents/models/GoldenConversationModels";

// The response-shape validation that the former hand-zod schemas owned now lives in the generated zod validator
// (`validator: true`) + the withResponseValidation bridge at the hook, so these tests no longer assert reject-on-
// malformed behaviour. Instead they cover (1) the coalescing mappers that project the optional-field generated wire
// shape into the strict domain view-model, (2) the surviving FORM-cap validator, and (3) the 409 promote-conflict
// body parser (consumed by PlaybookActionMappers). A generated-shaped DTO has every field optional (`x?: T`).
function makeResponseDto(
	overrides: Partial<XeLocalAiEngineClientEndpointsAgentsV1GoldenConversationResponse> = {},
): XeLocalAiEngineClientEndpointsAgentsV1GoldenConversationResponse {
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

describe("toGoldenConversations — list mapper", () => {
	it("maps a list envelope, normalizing a null assertion/rubric and provenance", () => {
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
					source: "manual",
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
					source: "manual",
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
		// Provenance defaults: an absent source coalesces to "manual" and absent ids to null.
		expect(result[0]?.source).toBe("manual");
		expect(result[0]?.sourceMessageId).toBeNull();
	});

	it("coalesces an empty items array to an empty list", () => {
		expect(toGoldenConversations({ items: [] })).toEqual([]);
	});
});

describe("toGoldenConversation — single mapper + provenance", () => {
	it("maps a created/approved bare case (non-envelope response)", () => {
		const created = toGoldenConversation({
			id: "g-3",
			agentDefinitionId: "agent-1",
			title: "Created",
			inputTurns: [{ role: "user", text: "Hi" }],
			assertion: { requiredPhrases: [], forbiddenPhrases: ["oops"] },
			rubric: null,
			enabled: true,
			source: "manual",
			createdAtUtc: 5,
			updatedAtUtc: 6,
		});

		expect(created.id).toBe("g-3");
		expect(created.assertion?.forbiddenPhrases).toEqual(["oops"]);
	});

	it("maps source='manual' and null provenance ids", () => {
		const result = toGoldenConversation(makeResponseDto({ source: "manual" }));

		expect(result.source).toBe("manual");
		expect(result.sourceMessageId).toBeNull();
		expect(result.sourceConversationId).toBeNull();
	});

	it("maps source='harvested' with provenance ids", () => {
		const result = toGoldenConversation(
			makeResponseDto({
				source: "harvested",
				sourceMessageId: "msg-abc",
				sourceConversationId: "conv-xyz",
			}),
		);

		expect(result.source).toBe("harvested");
		expect(result.sourceMessageId).toBe("msg-abc");
		expect(result.sourceConversationId).toBe("conv-xyz");
	});

	it("degrades an unknown/garbage source value to 'manual'", () => {
		const result = toGoldenConversation(makeResponseDto({ source: "garbage_unknown_value" }));

		expect(result.source).toBe("manual");
	});

	it("coalesces empty/zero fields to safe domain defaults", () => {
		const result = toGoldenConversation({
			id: "",
			agentDefinitionId: "",
			title: "",
			inputTurns: [],
			enabled: false,
			source: "manual",
			createdAtUtc: 0,
			updatedAtUtc: 0,
		});

		expect(result.id).toBe("");
		expect(result.title).toBe("");
		expect(result.inputTurns).toEqual([]);
		expect(result.assertion).toBeNull();
		expect(result.rubric).toBeNull();
		expect(result.enabled).toBe(false);
		expect(result.source).toBe("manual");
		expect(result.createdAtUtc).toBe(0);
	});
});

describe("toGoldenHarvestResult", () => {
	it("maps a harvest counts payload", () => {
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

	it("coalesces missing counts to zero", () => {
		expect(toGoldenHarvestResult({})).toEqual({
			thumbsUpScanned: 0,
			createdCount: 0,
			duplicateCount: 0,
			skippedCount: 0,
		});
	});
});

describe("findGoldenFieldOverLimit — create-request form caps", () => {
	it("returns null when the request is within all caps", () => {
		expect(
			findGoldenFieldOverLimit({
				title: "Short",
				inputTurns: [{ role: "user", text: "hi" }],
				rubric: "Is it right?",
			}),
		).toBeNull();
	});

	it("flags an over-long title", () => {
		expect(
			findGoldenFieldOverLimit({
				title: "x".repeat(GOLDEN_TITLE_MAX + 1),
				inputTurns: [{ role: "user", text: "hi" }],
				rubric: "ok",
			}),
		).toBe("title");
	});
});

describe("parsePromoteConflictBody", () => {
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
