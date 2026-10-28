import type { AgentDefinition } from "@/core/agents/models/AgentDefinitionModels";

// Agent picker row shared by chat (ChatDisplayShellProps, ChatInputArea, AgentSelectorCard) and the work-session
// create dialog. Both derive their list through `toAgentOptions`; downstream components receive it as a prop.
export interface AgentOption {
	readonly id: string;
	readonly name: string;
	readonly description: string;
	readonly kind: "Single" | "Orchestrator";
	readonly modelProfile: string | null;
	// Whether this agent has adaptive memory enabled. Gates the temporary-chat toggle in the chat header — the toggle
	// only renders when the bound agent learns memory at all.
	readonly playbookEnabled: boolean;
}

// Matches the backend AgentDefaults.DefaultAgentName seeded slug. Used to exclude the Default Assistant from
// the agent picker so the user never selects it explicitly (mode-off reproduces it transparently).
// The name comparison below is the single exclusion site.
const DEFAULT_ASSISTANT_NAME = "Default Assistant";

/** The picker list: every definition except the Default Assistant, sorted by name. */
export function toAgentOptions(definitions: readonly AgentDefinition[]): AgentOption[] {
	return definitions
		.filter((agent) => agent.name.toLowerCase() !== DEFAULT_ASSISTANT_NAME.toLowerCase())
		.map((agent) => ({
			id: agent.id,
			name: agent.name,
			description: agent.description,
			kind: agent.kind,
			modelProfile: agent.modelProfile,
			playbookEnabled: agent.playbookEnabled,
		}))
		.sort((left, right) => left.name.localeCompare(right.name));
}
