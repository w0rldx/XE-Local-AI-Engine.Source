import { useQuery } from "@tanstack/react-query";

import { getAgentPlaybookMonitorOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { toPlaybookMonitor } from "@/features/agents/models/PlaybookMonitorMappers";

// Server state for an agent's playbook monitor (read-only — no mutations). The generated `*Options()` wires the
// shared axios instance + TanStack Query AbortSignal automatically; a TanStack
// `select` maps the optional-field generated
// response into the stricter domain view-model. The query is disabled when no persisted agent is selected so the
// panel never fetches with an empty id.
export function usePlaybookMonitor(agentDefinitionId: string | null) {
	return useQuery({
		...getAgentPlaybookMonitorOptions({ path: { agentDefinitionId: agentDefinitionId ?? "" } }),
		enabled: agentDefinitionId !== null && agentDefinitionId.length > 0,
		select: toPlaybookMonitor,
	});
}
