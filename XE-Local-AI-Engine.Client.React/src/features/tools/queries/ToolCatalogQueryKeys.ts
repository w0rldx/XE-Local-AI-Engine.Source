// The tool catalog now reads through the generated hey-api query layer (getToolCatalogOptions), whose query keys are
// single-element arrays `[{ _id: "getToolCatalog", ... }]`. Invalidating with just the `_id` partial object matches
// every cached variant of that endpoint (TanStack partial-object matching). This helper is the ONE place that literal
// `_id` key — which trips biome's naming-convention rule — is constructed; it is reused by the MCP-server management
// hooks (useMcpServers) to refresh the catalog when the enabled server set changes.
export const toolCatalogQueryKeys = {
	// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
	all: () => [{ _id: "getToolCatalog" }] as const,
};
