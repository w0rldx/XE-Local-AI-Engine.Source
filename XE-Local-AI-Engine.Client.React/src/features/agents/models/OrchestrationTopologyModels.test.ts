import { describe, expect, it } from "vitest";
import { z } from "zod";

import {
	deserializeOrchestrationTopology,
	type OrchestrationTopology,
	orchestrationTopologySchema,
	serializeOrchestrationTopology,
} from "@/features/agents/models/OrchestrationTopologyModels";

const topology: OrchestrationTopology = {
	participantAgentDefinitionIds: ["spec-1", "spec-2"],
	handoffs: [{ fromAgentDefinitionId: "self-1", toAgentDefinitionId: "spec-1", reason: "research" }],
	maxTurnsPerAgent: 6,
	returnToPrevious: true,
};

const wireSchema = z.object({
	participantAgentDefinitionIds: z.array(z.string()),
	triageAgentDefinitionId: z.string(),
	handoffs: z.array(z.object({ reason: z.string().nullable() })),
});

function parseWire(json: string): z.infer<typeof wireSchema> {
	return wireSchema.parse(JSON.parse(json));
}

describe("orchestration topology serialization", () => {
	it("round-trips a topology through serialize → deserialize (triage stripped from specialists)", () => {
		const json = serializeOrchestrationTopology(topology, "self-1");
		const { topology: restored, triageAgentDefinitionId } = deserializeOrchestrationTopology(json);

		expect(triageAgentDefinitionId).toBe("self-1");
		// The triage (self-1) is folded into participants on serialize and stripped back out on deserialize.
		expect(restored.participantAgentDefinitionIds).toEqual(["spec-1", "spec-2"]);
		expect(restored.handoffs).toEqual(topology.handoffs);
		expect(restored.maxTurnsPerAgent).toBe(6);
		expect(restored.returnToPrevious).toBe(true);
	});

	it("folds the triage into participants at the head and dedupes it", () => {
		const withSelf: OrchestrationTopology = { ...topology, participantAgentDefinitionIds: ["self-1", "spec-1"] };
		const wire = parseWire(serializeOrchestrationTopology(withSelf, "self-1"));

		expect(wire.participantAgentDefinitionIds).toEqual(["self-1", "spec-1"]);
		expect(wire.triageAgentDefinitionId).toBe("self-1");
	});

	it("normalizes a blank handoff reason to null", () => {
		const blank: OrchestrationTopology = {
			...topology,
			handoffs: [{ fromAgentDefinitionId: "self-1", toAgentDefinitionId: "spec-1", reason: "   " }],
		};
		const wire = parseWire(serializeOrchestrationTopology(blank, "self-1"));

		expect(wire.handoffs).toHaveLength(1);
		expect(wire.handoffs[0]?.reason).toBeNull();
	});

	it("returns an empty topology for a null or unparseable string", () => {
		expect(deserializeOrchestrationTopology(null).topology.participantAgentDefinitionIds).toEqual([]);
		expect(deserializeOrchestrationTopology("not json").topology.participantAgentDefinitionIds).toEqual([]);
		expect(deserializeOrchestrationTopology("").triageAgentDefinitionId).toBeNull();
	});

	it("tolerates a partial wire payload by filling defaults", () => {
		const { topology: restored } = deserializeOrchestrationTopology(
			JSON.stringify({ triageAgentDefinitionId: "self-1", participantAgentDefinitionIds: ["self-1", "spec-9"] }),
		);

		expect(restored.participantAgentDefinitionIds).toEqual(["spec-9"]);
		expect(restored.handoffs).toEqual([]);
		expect(restored.maxTurnsPerAgent).toBe(8);
		expect(restored.returnToPrevious).toBe(false);
	});
});

describe("orchestration topology schema", () => {
	it("rejects a topology with no specialists", () => {
		const result = orchestrationTopologySchema.safeParse({ ...topology, participantAgentDefinitionIds: [] });

		expect(result.success).toBe(false);
	});

	it("rejects a self-referential handoff", () => {
		const result = orchestrationTopologySchema.safeParse({
			...topology,
			handoffs: [{ fromAgentDefinitionId: "spec-1", toAgentDefinitionId: "spec-1", reason: null }],
		});

		expect(result.success).toBe(false);
	});

	it("rejects a non-positive max turns", () => {
		expect(orchestrationTopologySchema.safeParse({ ...topology, maxTurnsPerAgent: 0 }).success).toBe(false);
		expect(orchestrationTopologySchema.safeParse({ ...topology, maxTurnsPerAgent: 2 }).success).toBe(true);
	});
});
