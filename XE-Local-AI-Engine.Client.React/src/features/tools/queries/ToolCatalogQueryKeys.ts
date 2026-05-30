export const toolCatalogQueryKeys = {
	all: () => ["tool-catalog"] as const,
	list: () => [...toolCatalogQueryKeys.all(), "list"] as const,
};
