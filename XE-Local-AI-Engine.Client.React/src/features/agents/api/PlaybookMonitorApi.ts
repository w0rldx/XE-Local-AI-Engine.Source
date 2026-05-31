import type { AxiosRequestConfig } from "axios";

import { axiosInstance } from "@/core/api/axios/AxiosInstance";
import { buildLocalApiUrl } from "@/core/api/utils/LocalApiUrl";
import { type PlaybookMonitor, toPlaybookMonitor } from "@/features/agents/models/PlaybookMonitorModels";

// Playbook P5 — read-only cohort-monitoring view for one agent (Operator-gated, mirror FeedbackInsightsApi):
//   GET /agents/{agentDefinitionId}/playbook/monitor → per-Enabled-action monitoring signals + retrieval config
//                                                       (404 when the agent is unknown)
// Thin contract layer so the panel works against the documented Wave 3a endpoint; if the backend casing/route
// base differs, only this file changes. The read wires the TanStack Query AbortSignal in and validates the
// payload at the boundary via the Zod model.

const AGENTS_ROUTE = "agents";
const PLAYBOOK_SEGMENT = "playbook";
const MONITOR_SEGMENT = "monitor";

function playbookMonitorRoute(agentDefinitionId: string): string {
	return `${AGENTS_ROUTE}/${encodeURIComponent(agentDefinitionId)}/${PLAYBOOK_SEGMENT}/${MONITOR_SEGMENT}`;
}

export async function getPlaybookMonitor(agentDefinitionId: string, config?: AxiosRequestConfig): Promise<PlaybookMonitor> {
	const { data } = await axiosInstance.get<unknown>(buildLocalApiUrl(playbookMonitorRoute(agentDefinitionId)), config);
	return toPlaybookMonitor(data);
}
