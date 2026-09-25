// Ambient stubs for the Graph Workflows reads every `/chat` render makes (the node's capability switch, the Chat
// definitions for the workflow picker, and the selected conversation's bound runs). The Chat page tests stub the
// generated TanStack options rather than installing MSW, so without these the reads would hit the no-network guard
// and fail silently as query errors.
// Spread into a `vi.mock("@/core/api/generated/@tanstack/react-query.gen", …)` factory after `importOriginal()`.
//
// The feature answers on and the lists answer empty: no chat workflow exists and no run is bound, which is the
// normal-chat baseline those tests assert.

export const chatWorkflowQueryStubs = {
	getGraphWorkflowCapabilityOptions: () => ({
		// biome-ignore lint/style/useNamingConvention: generated hey-api query-key discriminator.
		queryKey: [{ _id: "getGraphWorkflowCapability" }],
		queryFn: async () => ({ enabled: true }),
	}),
	listGraphWorkflowDefinitionsOptions: () => ({
		// biome-ignore lint/style/useNamingConvention: generated hey-api query-key discriminator.
		queryKey: [{ _id: "listGraphWorkflowDefinitions" }],
		queryFn: async () => ({ definitions: [] }),
	}),
	listGraphWorkflowConversationRunsOptions: (options: { path: { conversationId: string } }) => ({
		// biome-ignore lint/style/useNamingConvention: generated hey-api query-key discriminator.
		queryKey: [{ _id: "listGraphWorkflowConversationRuns", path: options.path }],
		queryFn: async () => ({ runs: [] }),
	}),
};
