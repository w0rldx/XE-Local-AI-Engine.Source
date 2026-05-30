import { z } from "zod";

import {
	type OrchestrationTopology,
	orchestrationTopologySchema,
	orchestrationTopologyShapeSchema,
} from "@/features/agents/models/OrchestrationTopologyModels";
import type { ReasoningEffort } from "@/features/chat/models/ChatModels";

// Mirrors the backend AgentDefinitionKind enum (Single=0, Orchestrator=1). The wire contract carries the
// string form. Orchestrator is persisted-but-ignored in P3 (runs as a single agent) — see loop P3 plan §0.4.
export type AgentDefinitionKind = "Single" | "Orchestrator";

export const agentDefinitionKinds: readonly AgentDefinitionKind[] = ["Single", "Orchestrator"];

// Reasoning effort reuses the chat surface's union so the agent form and the composer stay in lockstep.
export const agentReasoningEfforts: readonly ReasoningEffort[] = ["none", "low", "medium", "high"];

// Domain view-model for an agent definition. Tool config is exposed as typed structures (the wire DTO keeps
// allowedToolNames as a list and toolApprovals as a map); timestamps are epoch milliseconds (long on the wire).
export interface AgentDefinition {
	readonly id: string;
	readonly name: string;
	readonly description: string;
	readonly instructions: string;
	readonly modelProfile: string | null;
	readonly reasoningEffort: ReasoningEffort | null;
	readonly kind: AgentDefinitionKind;
	readonly allowedToolNames: readonly string[];
	readonly toolApprovals: Readonly<Record<string, boolean>>;
	readonly orchestrationTopologyJson: string | null;
	readonly version: number;
	readonly createdAtUtc: number;
	readonly updatedAtUtc: number;
}

// Form values are intentionally narrower than the persisted entity: identity/version/timestamps are managed by
// the backend, not edited here. The structured `orchestration` topology (loop P5) is authored when
// kind=Orchestrator and serialized to orchestrationTopologyJson at the API boundary; it is always present so the
// editor can round-trip it, but it is only persisted (and validated) for orchestrator definitions.
export interface AgentDefinitionFormValues {
	name: string;
	description: string;
	instructions: string;
	modelProfile: string | null;
	reasoningEffort: ReasoningEffort | null;
	kind: AgentDefinitionKind;
	allowedToolNames: string[];
	toolApprovals: Record<string, boolean>;
	orchestration: OrchestrationTopology;
}

const reasoningEffortSchema = z.enum(["none", "low", "medium", "high"]);
const kindSchema = z.enum(["Single", "Orchestrator"]);

// Zod schema validating the form before submit. Name + Instructions are required (non-empty after trim);
// toolApprovals keys must be a subset of the selected tools (a per-tool toggle can never reference a tool
// that is not selected). reasoningEffort/modelProfile are nullable (null = node default). The orchestration
// topology is only enforced for kind=Orchestrator — Single definitions ignore it.
export const agentDefinitionFormSchema = z
	.object({
		name: z.string().trim().min(1).max(120),
		description: z.string().max(2000),
		instructions: z.string().trim().min(1).max(20000),
		modelProfile: z.string().trim().min(1).nullable(),
		reasoningEffort: reasoningEffortSchema.nullable(),
		kind: kindSchema,
		allowedToolNames: z.array(z.string()),
		toolApprovals: z.record(z.string(), z.boolean()),
		orchestration: orchestrationTopologyShapeSchema,
	})
	.refine(
		(value) => Object.keys(value.toolApprovals).every((toolName) => value.allowedToolNames.includes(toolName)),
		{ message: "toolApprovals keys must be a subset of allowedToolNames", path: ["toolApprovals"] },
	)
	.superRefine((value, ctx) => {
		// Orchestration validity is only required for Orchestrator definitions. A Single definition may carry a
		// stale/empty topology that must not block submit, so re-run the topology rules only when kind=Orchestrator.
		if (value.kind !== "Orchestrator") {
			return;
		}

		const result = orchestrationTopologySchema.safeParse(value.orchestration);
		if (!result.success) {
			for (const issue of result.error.issues) {
				ctx.addIssue({ code: "custom", message: issue.message, path: ["orchestration", ...issue.path] });
			}
		}
	});

export type AgentDefinitionFormSchema = z.infer<typeof agentDefinitionFormSchema>;
