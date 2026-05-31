import type { AxiosRequestConfig } from "axios";

import { axiosInstance } from "@/core/api/axios/AxiosInstance";
import { buildLocalApiUrl } from "@/core/api/utils/LocalApiUrl";
import { type FeedbackInsights, toFeedbackInsights } from "@/features/agents/models/FeedbackInsightsModels";

// Playbook P2 — read-only feedback insights for one agent (Operator-gated, mirror PlaybookActionsApi):
//   GET /agents/{agentDefinitionId}/feedback-insights → the per-agent feedback aggregate (404 when unknown)
// Thin contract layer so the panel works against the documented Lane 3 endpoint; if the backend casing/route
// base differs, only this file changes. The read wires the TanStack Query AbortSignal in and validates the
// payload at the boundary via the Zod model.

const AGENTS_ROUTE = "agents";
const FEEDBACK_INSIGHTS_SEGMENT = "feedback-insights";

function feedbackInsightsRoute(agentDefinitionId: string): string {
	return `${AGENTS_ROUTE}/${encodeURIComponent(agentDefinitionId)}/${FEEDBACK_INSIGHTS_SEGMENT}`;
}

export async function getFeedbackInsights(agentDefinitionId: string, config?: AxiosRequestConfig): Promise<FeedbackInsights> {
	const { data } = await axiosInstance.get<unknown>(buildLocalApiUrl(feedbackInsightsRoute(agentDefinitionId)), config);
	return toFeedbackInsights(data);
}
