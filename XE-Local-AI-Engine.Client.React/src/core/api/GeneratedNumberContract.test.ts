import { describe, expect, it } from "vitest";
import { zXeLocalAiEngineClientEndpointsAgentsV1AgentRunEnvelopeResponse } from "@/core/api/generated/zod.gen";

// Runtime contract: the backend serializes C# `long` (int64) fields as plain JSON numbers, and
// FetchOpenapi.mjs normalizes `format: int64` away so the generated zod validators accept and
// return `number` — matching the generated TypeScript types. Without that normalization the zod
// plugin emits `z.coerce.bigint()`, silently splitting the runtime value type (bigint) from the
// declared type (number) for every timestamp/duration in the API.
describe("generated int64 number contract", () => {
	it("parses formerly-int64 envelope fields as plain numbers, never bigint", () => {
		const parsed = zXeLocalAiEngineClientEndpointsAgentsV1AgentRunEnvelopeResponse.parse({
			id: "0b5a3f92-3a86-4c56-9d3e-2f6d3a1c9a01",
			schemaVersion: 3,
			agentDefinitionId: "8f0f2f4e-51f2-4a3e-9d1f-64de6f0b7c22",
			conversationId: null,
			messageId: null,
			invocationId: null,
			requestId: null,
			modelName: "bartowski/Model-GGUF:Q4_K_M",
			terminalStatus: "completed",
			success: true,
			failureCategory: null,
			durationMs: 1234,
			promptTokens: 10,
			completionTokens: 20,
			reasoningTokens: null,
			totalTokens: 30,
			contentChunkCount: 4,
			reasoningChunkCount: 0,
			traceId: null,
			startedAtUtc: 1_784_000_000_000,
			createdAtUtc: 1_784_000_001_000,
		});

		expect(typeof parsed.durationMs).toBe("number");
		expect(typeof parsed.startedAtUtc).toBe("number");
		expect(typeof parsed.createdAtUtc).toBe("number");
		// A regression back to z.coerce.bigint() would coerce these into BigInt values.
		expect(parsed.createdAtUtc).toBe(1_784_000_001_000);
	});

	it("rejects a non-integer where an int64-derived field is expected", () => {
		const result = zXeLocalAiEngineClientEndpointsAgentsV1AgentRunEnvelopeResponse.safeParse({
			id: "0b5a3f92-3a86-4c56-9d3e-2f6d3a1c9a01",
			schemaVersion: 3,
			agentDefinitionId: "8f0f2f4e-51f2-4a3e-9d1f-64de6f0b7c22",
			modelName: "m",
			terminalStatus: "completed",
			success: true,
			durationMs: 12.5,
			createdAtUtc: 1_784_000_001_000,
		});

		expect(result.success).toBe(false);
	});
});
