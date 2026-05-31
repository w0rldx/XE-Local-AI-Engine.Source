import { useQuery } from "@tanstack/react-query";

import { getFeedbackInsights } from "@/features/agents/api/FeedbackInsightsApi";
import { feedbackInsightsQueryKeys } from "@/features/agents/queries/FeedbackInsightsQueryKeys";

// Server state for an agent's feedback insights (read-only — no mutations). The read wires the TanStack Query
// AbortSignal into the axios request (per repo React standards). The query is disabled when no persisted agent
// is selected so the panel never fetches with an empty id.
export function useFeedbackInsights(agentDefinitionId: string | null) {
	return useQuery({
		queryKey: feedbackInsightsQueryKeys.byAgent(agentDefinitionId ?? ""),
		queryFn: ({ signal }) => getFeedbackInsights(agentDefinitionId ?? "", { signal }),
		enabled: agentDefinitionId !== null && agentDefinitionId.length > 0,
	});
}
