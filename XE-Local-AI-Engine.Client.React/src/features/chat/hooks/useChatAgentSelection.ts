import { useCallback, useMemo } from "react";

import { useAgentDefinitions } from "@/features/agents/queries/useAgentDefinitions";
import type { AgentOption, ChatScope } from "@/features/chat/models/ChatModels";
import { DEFAULT_ASSISTANT_NAME } from "@/features/chat/models/ChatModels";
import { useNodeChatPreferencesStore } from "@/features/chat/stores/NodeChatPreferencesStore";

interface ChatAgentSelection {
	agentModeEnabled: boolean;
	selectedAgentId: string;
	agentOptions: AgentOption[];
	agentControlsAvailable: boolean;
	boundAgentMemoryEnabled: boolean;
	// The model the scope's pinned agent runs, once its definition has loaded.
	pinnedAgentModelProfile?: string | null;
	// The pinned agent is not loaded yet, so the model it runs is still unknown.
	scopedModelPending: boolean;
	handleSelectAgent: (agentId: string) => void;
}

/** Agent binding for the chat composer: the live option list, the scope's pin, and the merged agent control. */
export function useChatAgentSelection(scope: ChatScope | undefined, showAgentControls: boolean): ChatAgentSelection {
	const preferredAgentModeEnabled = useNodeChatPreferencesStore((state) => state.agentModeEnabled);
	const preferredSelectedAgentId = useNodeChatPreferencesStore((state) => state.selectedAgentId);
	const { setAgentModeEnabled, setSelectedAgentId } = useNodeChatPreferencesStore((state) => state.actions);
	// A scope that pins an agent wins over the stored composer preference: the session owns the binding.
	const agentModeEnabled = scope?.pinnedAgentId ? true : preferredAgentModeEnabled;
	const selectedAgentId = scope?.pinnedAgentId ?? preferredSelectedAgentId;
	// …and so does the model that agent pins. The scoped selectors are read-only, so falling back to the operator's
	// stored `/chat` choice would show a model label they cannot correct AND poll details for a model this session
	// is not running. An agent that pins nothing (both seeded personas do) resolves to the local default — which is
	// what the backend will actually pick — never to the stored preference.
	const agentDefinitionsQuery = useAgentDefinitions();
	const pinnedAgentModelProfile = scope?.pinnedAgentId
		? agentDefinitionsQuery.data?.find((agent) => agent.id === scope.pinnedAgentId)?.modelProfile
		: undefined;
	const scopedModelPending = scope?.pinnedAgentId !== undefined && agentDefinitionsQuery.data === undefined;

	// Build the live agent option list (sorted, excluding the Default Assistant by shared constant).
	// Single derivation site — AgentSelectorCard receives this as a prop (no internal query call).
	// Chat.tsx uses this list to gate send: a stale/deleted selectedAgentId that no longer appears here is
	// silently dropped → the send falls back to Default Assistant (mode-off behavior).
	const agentOptions = useMemo<AgentOption[]>(() => {
		const definitions = agentDefinitionsQuery.data ?? [];
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
			.sort((a, b) => a.name.localeCompare(b.name));
	}, [agentDefinitionsQuery.data]);
	// agentControlsAvailable: capability gate AND at least one agent in the live list.
	const agentControlsAvailable = showAgentControls && agentOptions.length > 0;
	// Whether the currently bound agent (agent mode on + a selected agent that still exists) has adaptive memory
	// enabled. Gates the temporary-chat toggle in the chat header — there is nothing to suppress unless the agent
	// learns memory at all. Default Assistant / mode-off => no bound agent => false.
	const boundAgentMemoryEnabled = useMemo(() => {
		if (!agentModeEnabled || !selectedAgentId) {
			return false;
		}
		return agentOptions.find((agent) => agent.id === selectedAgentId)?.playbookEnabled ?? false;
	}, [agentModeEnabled, agentOptions, selectedAgentId]);
	// Single merged agent control wiring: picking an agent enables agent mode and stamps it; picking the Default
	// Assistant row (empty id) disables agent mode and clears the selection. Replaces the old separate toggle.
	const handleSelectAgent = useCallback(
		(agentId: string) => {
			if (agentId) {
				setSelectedAgentId(agentId);
				setAgentModeEnabled(true);
			} else {
				setAgentModeEnabled(false);
				setSelectedAgentId("");
			}
		},
		[setAgentModeEnabled, setSelectedAgentId],
	);

	return {
		agentModeEnabled,
		selectedAgentId,
		agentOptions,
		agentControlsAvailable,
		boundAgentMemoryEnabled,
		pinnedAgentModelProfile,
		scopedModelPending,
		handleSelectAgent,
	};
}
