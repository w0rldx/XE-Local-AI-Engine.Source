// TanStack Query keys for the per-agent playbook monitor. Scoped by agentDefinitionId so each agent's monitoring
// view caches independently.
export const playbookMonitorQueryKeys = {
	all: () => ["playbook-monitor"] as const,
	byAgent: (agentDefinitionId: string) => [...playbookMonitorQueryKeys.all(), agentDefinitionId] as const,
};
