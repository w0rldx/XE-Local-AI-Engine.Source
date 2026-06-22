import { useQuery } from "@tanstack/react-query";

import { listAgentExecutionLogsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { type AgentExecutionLog, toAgentExecutionLog } from "@/features/agents/models/AgentExecutionLogModels";

// How many recent execution-log rows to pull in one read. The endpoint returns metadata-only rows (no total), so
// the diagnostics table paginates client-side over this bounded window (mirrors the scheduler run-history table).
export const AGENT_EXECUTION_LOG_WINDOW = 200;

// Server state for an agent's execution-log diagnostics (adaptive-memory observability, metadata only). The read
// uses the generated hey-api `*Options()` (which wires the shared axios instance + TanStack Query AbortSignal
// automatically), wrapped in withResponseValidation so a zod response-shape failure surfaces as an ApiError; a
// TanStack `select` maps the optional-field generated response into the stricter domain view-models. The query is
// disabled when no agent is selected so the panel never fetches with an empty id.
export function useAgentExecutionLogs(agentDefinitionId: string | null) {
	return useQuery({
		...withResponseValidation(
			listAgentExecutionLogsOptions({
				path: { agentDefinitionId: agentDefinitionId ?? "" },
				query: { limit: AGENT_EXECUTION_LOG_WINDOW, offset: 0 },
			}),
		),
		enabled: agentDefinitionId !== null && agentDefinitionId.length > 0,
		select: (data): AgentExecutionLog[] => (data.items ?? []).map(toAgentExecutionLog),
	});
}
