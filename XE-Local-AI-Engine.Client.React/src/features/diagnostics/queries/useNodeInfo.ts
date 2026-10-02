import { useQuery } from "@tanstack/react-query";

import { getNodeInfoOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";

// Engine version, OS and hardware barely change while the SPA is open, and the report gathers GPU and runtime probes on
// the node, so one read per session is enough. It feeds the prefilled GitHub issue link.
const NODE_INFO_STALE_MS = 30 * 60_000;

export function useNodeInfo() {
	return useQuery({ ...withResponseValidation(getNodeInfoOptions()), staleTime: NODE_INFO_STALE_MS, retry: false });
}
