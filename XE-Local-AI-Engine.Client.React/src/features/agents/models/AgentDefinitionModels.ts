import { z } from "zod";

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

// Form values are intentionally narrower than the persisted entity: identity/version/timestamps and the
// persist-but-ignore topology are managed by the backend, not edited here in P3.
export interface AgentDefinitionFormValues {
	name: string;
	description: string;
	instructions: string;
	modelProfile: string | null;
	reasoningEffort: ReasoningEffort | null;
	kind: AgentDefinitionKind;
	allowedToolNames: string[];
	toolApprovals: Record<string, boolean>;
}

const reasoningEffortSchema = z.enum(["none", "low", "medium", "high"]);
const kindSchema = z.enum(["Single", "Orchestrator"]);

// Zod schema validating the form before submit. Name + Instructions are required (non-empty after trim);
// toolApprovals keys must be a subset of the selected tools (a per-tool toggle can never reference a tool
// that is not selected). reasoningEffort/modelProfile are nullable (null = node default).
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
	})
	.refine(
		(value) => Object.keys(value.toolApprovals).every((toolName) => value.allowedToolNames.includes(toolName)),
		{ message: "toolApprovals keys must be a subset of allowedToolNames", path: ["toolApprovals"] },
	);

export type AgentDefinitionFormSchema = z.infer<typeof agentDefinitionFormSchema>;
