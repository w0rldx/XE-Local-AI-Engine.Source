export const mcpServersQueryKeys = {
	all: () => ["mcp-servers"] as const,
	list: () => [...mcpServersQueryKeys.all(), "list"] as const,
	tools: (id: string) => [...mcpServersQueryKeys.all(), "tools", id] as const,
};
