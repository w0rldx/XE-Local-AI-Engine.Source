import { useQuery } from "@tanstack/react-query";

import { getAgentFeedbackInsightsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { toFeedbackInsights } from "@/features/agents/models/FeedbackInsightsMappers";

// Server state for an agent's feedback insights (read-only — no mutations). The generated `*Options()` wires the
// shared axios instance + TanStack Query AbortSignal automatically and is wrapped in withResponseValidation so a
// zod response-shape failure surfaces as an ApiError; a TanStack `select` maps the optional-field generated
// response into the stricter domain view-model. The query is disabled when no persisted agent is selected so the
// panel never fetches with an empty id.
export function useFeedbackInsights(agentDefinitionId: string | null) {
	return useQuery({
		...withResponseValidation(getAgentFeedbackInsightsOptions({ path: { agentDefinitionId: agentDefinitionId ?? "" } })),
		enabled: agentDefinitionId !== null && agentDefinitionId.length > 0,
		select: toFeedbackInsights,
	});
}
