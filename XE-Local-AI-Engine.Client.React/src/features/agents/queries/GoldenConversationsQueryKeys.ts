// TanStack Query keys for the per-agent golden conversation set. Scoped by agentDefinitionId so each agent's
// golden cases cache independently and mutations invalidate only the affected agent.
export const goldenConversationsQueryKeys = {
	all: () => ["golden-conversations"] as const,
	byAgent: (agentDefinitionId: string) => [...goldenConversationsQueryKeys.all(), agentDefinitionId] as const,
};
