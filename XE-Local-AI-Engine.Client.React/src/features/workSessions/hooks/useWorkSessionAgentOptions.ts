import { useMemo } from "react";

import { type AgentOption, toAgentOptions } from "@/core/agents/models/AgentOption";
import { useAgentDefinitions } from "@/core/agents/queries/useAgentDefinitions";

/**
 * The agent list for the create dialog's `AgentSelectorCard`, derived by the same `toAgentOptions` as the chat
 * composer: the Default Assistant is excluded (it is the picker's own "off" row, not a pinnable agent) and the rest
 * sorted by name. The two seeded work-session personas appear here like any other agent.
 */
export function useWorkSessionAgentOptions(): { options: readonly AgentOption[]; isLoading: boolean } {
	const query = useAgentDefinitions();
	const options = useMemo<AgentOption[]>(() => toAgentOptions(query.data ?? []), [query.data]);
	return { options, isLoading: query.isPending };
}
