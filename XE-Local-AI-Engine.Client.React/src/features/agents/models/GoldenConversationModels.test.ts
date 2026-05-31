import { describe, expect, it } from "vitest";

import {
	parsePromoteConflictBody,
	toCreatedGoldenConversation,
	toGoldenConversations,
} from "@/features/agents/models/GoldenConversationModels";

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
