import { useMemo } from "react";

import { useAgentDefinitions } from "@/features/agents/queries/useAgentDefinitions";
import type { AgentOption } from "@/features/chat/models/ChatModels";
import { DEFAULT_ASSISTANT_NAME } from "@/features/chat/models/ChatModels";

/**
 * The agent list for the create dialog's `AgentSelectorCard`, derived exactly as `Chat.tsx` derives it: the Default
 * Assistant is excluded (it is the picker's own "off" row, not a pinnable agent) and the rest sorted by name. The two
 * seeded work-session personas appear here like any other agent.
 */
export function useWorkSessionAgentOptions(): { options: readonly AgentOption[]; isLoading: boolean } {
	const query = useAgentDefinitions();
	const options = useMemo<AgentOption[]>(
		() =>
			(query.data ?? [])
				.filter((agent) => agent.name.toLowerCase() !== DEFAULT_ASSISTANT_NAME.toLowerCase())
				.map((agent) => ({
					id: agent.id,
					name: agent.name,
					description: agent.description,
					kind: agent.kind,
					modelProfile: agent.modelProfile,
					playbookEnabled: agent.playbookEnabled,
				}))
				.sort((left, right) => left.name.localeCompare(right.name)),
		[query.data],
	);
	return { options, isLoading: query.isPending };
}
