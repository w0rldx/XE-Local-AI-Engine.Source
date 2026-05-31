import { useQuery } from "@tanstack/react-query";

import { getPlaybookMonitor } from "@/features/agents/api/PlaybookMonitorApi";
import { playbookMonitorQueryKeys } from "@/features/agents/queries/PlaybookMonitorQueryKeys";

// Server state for an agent's playbook monitor (read-only — no mutations). The read wires the TanStack Query
// AbortSignal into the axios request (per repo React standards). The query is disabled when no persisted agent is
// selected so the panel never fetches with an empty id.
export function usePlaybookMonitor(agentDefinitionId: string | null) {
	return useQuery({
		queryKey: playbookMonitorQueryKeys.byAgent(agentDefinitionId ?? ""),
		queryFn: ({ signal }) => getPlaybookMonitor(agentDefinitionId ?? "", { signal }),
		enabled: agentDefinitionId !== null && agentDefinitionId.length > 0,
	});
}
