import { z } from "zod";

import type { ReasoningEffort } from "@/core/models/ReasoningEffort";
import type { GenerationMetadata } from "@/features/assist/models/AssistModels";
import {
	type OrchestrationTopology,
	orchestrationTopologySchema,
	orchestrationTopologyShapeSchema,
} from "@/features/agents/models/OrchestrationTopologyModels";

// Mirrors the backend AgentDefinitionKind enum (Single=0, Orchestrator=1). The wire contract carries the
// string form. Orchestrator IS executed by the runtime: ChatTurnResolver.ResolveOrchestrationAsync feeds the
// definition's topology to OrchestrationResolver, and OrchestrationAgentFactory builds the multi-agent run.
export type AgentDefinitionKind = "Single" | "Orchestrator";

export const agentDefinitionKinds: readonly AgentDefinitionKind[] = ["Single", "Orchestrator"];

// Reasoning effort reuses the chat surface's union so the agent form and the composer stay in lockstep.
export const agentReasoningEfforts: readonly ReasoningEffort[] = ["none", "low", "medium", "high", "auto"];

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
	// Per-agent skill picklist: ids of the node skills this agent may load on demand (MAF progressive disclosure).
	// Treated exactly like allowedToolNames — optional, defaults to []. Config-affecting (bumps the agent version).
	readonly allowedSkillIds: readonly string[];
	readonly orchestrationTopologyJson: string | null;
	// When true the agent's enabled playbook actions are appended to its instructions at resolve
	// time. Gates injection only — it is NOT a config-affecting field for the agent's own version bump (the
	// injected content drives the runtime config hash directly).
	readonly playbookEnabled: boolean;
	// When true, conversations created with this agent bound default to "temporary" (memory-excluded): the run will
	// not teach the agent new adaptive memory, though existing memory is still used. Per-conversation override lives
	// on the conversation itself. Not a config-affecting field (mirrors playbookEnabled).
	readonly defaultTemporaryChat: boolean;
	// When true (default) this agent mines its completed runs into new candidate memories. When false it is
	// retrieval-only: existing enabled memory is still injected (gated on playbookEnabled), but no new memory is
	// learned and no per-run extraction round-trip happens. Not a config-affecting field (mirrors playbookEnabled).
	readonly memoryExtractionEnabled: boolean;
	// When true this agent opts OUT of the layered base instruction scaffold the runtime normally prepends — only the
	// instructions written above are sent, unmodified. Defaults to false (scaffold applied).
	readonly disableBaseScaffold: boolean;
	// When true this agent opts OUT of the node's send-time tool-relevance filter, so every tool it is allowed to use is
	// put in front of the model on every round. Defaults to false (follow the node setting). Never an authorisation
	// change: a filtered-out tool is one the model was not shown, not one it may not call.
	readonly disableToolRelevanceFilter: boolean;
	readonly version: number;
	readonly createdAtUtc: number;
	readonly updatedAtUtc: number;
}

// Form values are intentionally narrower than the persisted entity: identity/version/timestamps are managed by
// the backend, not edited here. The structured `orchestration` topology (orchestration) is authored when
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
	// Per-agent skill picklist (node skill ids). Mirrors allowedToolNames in posture: an empty list is byte-identical
	// to the pre-skills payload at resolve time.
	allowedSkillIds: string[];
	orchestration: OrchestrationTopology;
	// Toggles whether this agent's enabled playbook actions are injected into its instructions.
	playbookEnabled: boolean;
	// Default new conversations bound to this agent to temporary (memory-excluded) — no new memory learned from them.
	defaultTemporaryChat: boolean;
	// When false the agent uses existing memory only (retrieval-only): it mines no new memory from its runs.
	memoryExtractionEnabled: boolean;
	// Opts this agent out of the layered base instruction scaffold normally prepended at resolve time.
	disableBaseScaffold: boolean;
	// Opts this agent out of the node's send-time tool-relevance filter, so every allowed tool is offered every round.
	disableToolRelevanceFilter: boolean;
	// Opaque provenance from an applied AI draft, echoed back verbatim on save. Unlike a skill there is no posture to
	// demote — an agent definition carries no Imported/Enabled fence — so this is the only thing a draft leaves
	// behind. `null` (an ordinary edit) tells the server to preserve whatever provenance the row already has.
	generationMetadata: GenerationMetadata | null;
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
		allowedSkillIds: z.array(z.string()),
		orchestration: orchestrationTopologyShapeSchema,
		playbookEnabled: z.boolean(),
		defaultTemporaryChat: z.boolean(),
		memoryExtractionEnabled: z.boolean(),
		disableBaseScaffold: z.boolean(),
		disableToolRelevanceFilter: z.boolean(),
		// Deliberately unvalidated: the provenance block is opaque client-side and must survive submit byte-for-byte,
		// so a z.object() (which strips unknown keys) would drop any field the server adds later.
		generationMetadata: z.custom<GenerationMetadata>().nullable(),
	})
	.refine((value) => Object.keys(value.toolApprovals).every((toolName) => value.allowedToolNames.includes(toolName)), {
		message: "toolApprovals keys must be a subset of allowedToolNames",
		path: ["toolApprovals"],
	})
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
