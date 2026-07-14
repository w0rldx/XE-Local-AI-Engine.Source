import { describe, expect, it } from "vitest";
import {
	zXeLocalAiEngineClientEndpointsAgentsV1AgentRunEnvelopeResponse,
	zXeLocalAiEngineClientEndpointsImagesV1CreateImageJobRequest,
	zXeLocalAiEngineClientEndpointsImagesV1ImageJobResponse,
	zXeLocalAiEngineClientModelsSamplingOptions,
} from "@/core/api/generated/zod.gen";

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

// The one class of int64 that is NOT safe as a JSON number is an unconstrained 64-bit RNG seed: a value above 2^53
// would be silently rounded on the wire and then rejected by z.int(). The backend represents every seed field as a
// STRING instead (see SeedValue), so the generated seed validators are z.string() — a large seed round-trips exactly
// and a JSON number is rejected. This is the deliberate exception to the int64→number rule above.
describe("generated seed string contract", () => {
	// 9007199254740993 = 2^53 + 1: the first integer a JSON number cannot represent.
	const largeSeed = "9007199254740993";

	it("keeps SamplingOptions.seed a string and preserves a value above 2^53", () => {
		const parsed = zXeLocalAiEngineClientModelsSamplingOptions.parse({ seed: largeSeed });
		expect(typeof parsed.seed).toBe("string");
		expect(parsed.seed).toBe(largeSeed);
		// A JSON number is rejected — the wire contract is a string, never a lossy int64 number.
		expect(zXeLocalAiEngineClientModelsSamplingOptions.safeParse({ seed: 1234 }).success).toBe(false);
	});

	it("keeps the image request/response seed a string and preserves a value above 2^53", () => {
		const request = zXeLocalAiEngineClientEndpointsImagesV1CreateImageJobRequest.parse({
			modelName: "stable-diffusion-1.5",
			prompt: "a watercolor fox",
			seed: largeSeed,
		});
		expect(typeof request.seed).toBe("string");
		expect(request.seed).toBe(largeSeed);

		const response = zXeLocalAiEngineClientEndpointsImagesV1ImageJobResponse.parse({
			id: "0b5a3f92-3a86-4c56-9d3e-2f6d3a1c9a01",
			modelName: "stable-diffusion-1.5",
			prompt: "a watercolor fox",
			seed: largeSeed,
			width: 512,
			height: 512,
			steps: 20,
			sampler: "euler_a",
			cfgScale: 7,
			status: "Queued",
			createdAtUtc: 1_784_000_001_000,
		});
		expect(typeof response.seed).toBe("string");
		expect(response.seed).toBe(largeSeed);
		// A JSON number is rejected on the request seed too.
		expect(
			zXeLocalAiEngineClientEndpointsImagesV1CreateImageJobRequest.safeParse({
				modelName: "m",
				prompt: "p",
				seed: 1234,
			}).success,
		).toBe(false);
	});
});
