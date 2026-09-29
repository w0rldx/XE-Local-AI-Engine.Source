import { useQuery } from "@tanstack/react-query";

import { getRuntimeResourcesOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { toRuntimeResources } from "@/features/runtime-resources/models/RuntimeResourcesModels";

// Poll cadence (ms) for the top-bar gauge. The node caches its sample for about two seconds, so several open tabs
// share one probe. TanStack Query's default `refetchIntervalInBackground: false` pauses the poll while the document
// is hidden.
const runtimeResourcesPollIntervalMs = 5000;

// Live whole-machine RAM and VRAM. `enabled` keeps a signed-out shell from polling an Operator-only endpoint.
export function useRuntimeResources(enabled = true) {
	return useQuery({
		...withResponseValidation(getRuntimeResourcesOptions()),
		select: toRuntimeResources,
		enabled,
		refetchInterval: runtimeResourcesPollIntervalMs,
	});
}
