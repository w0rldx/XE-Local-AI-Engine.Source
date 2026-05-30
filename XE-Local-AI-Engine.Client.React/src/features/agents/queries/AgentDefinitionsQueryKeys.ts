export const agentDefinitionsQueryKeys = {
	all: () => ["agent-definitions"] as const,
	list: () => [...agentDefinitionsQueryKeys.all(), "list"] as const,
	toolCapableModels: () => [...agentDefinitionsQueryKeys.all(), "tool-capable-models"] as const,
};
