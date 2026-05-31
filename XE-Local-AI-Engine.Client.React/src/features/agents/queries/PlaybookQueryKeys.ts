// TanStack Query keys for the per-agent playbook. Scoped by agentDefinitionId so each agent's action list caches
// independently and mutations invalidate only the affected agent.
export const playbookQueryKeys = {
	all: () => ["playbook-actions"] as const,
	byAgent: (agentDefinitionId: string) => [...playbookQueryKeys.all(), agentDefinitionId] as const,
};
