import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	getLogLevelOptions,
	getLogLevelQueryKey,
	getNodeInfoQueryKey,
	setLogLevelMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";

// The switch is session-only on the node, so a restart flips it back to off behind the SPA's back; a short stale time
// keeps the header from showing a stale "on" for long.
const LOG_LEVEL_STALE_MS = 30_000;

export function useLogLevel() {
	return useQuery({ ...withResponseValidation(getLogLevelOptions()), staleTime: LOG_LEVEL_STALE_MS, retry: false });
}

// Node-info carries `verboseLogging` into the support bundle, so it is refreshed with the switch.
export function useSetLogLevel() {
	const queryClient = useQueryClient();
	return useMutation({
		...withResponseValidation(setLogLevelMutation()),
		onSuccess: () =>
			Promise.all([
				queryClient.invalidateQueries({ queryKey: getLogLevelQueryKey() }),
				queryClient.invalidateQueries({ queryKey: getNodeInfoQueryKey() }),
			]),
	});
}
