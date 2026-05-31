// TanStack Query keys for the per-agent feedback insights. Scoped by agentDefinitionId so each agent's
// aggregate caches independently.
export const feedbackInsightsQueryKeys = {
	all: () => ["feedback-insights"] as const,
	byAgent: (agentDefinitionId: string) => [...feedbackInsightsQueryKeys.all(), agentDefinitionId] as const,
};
