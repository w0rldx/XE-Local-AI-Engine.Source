import { useQuery } from "@tanstack/react-query";

import { getRuntimeResidentsOptions, getRuntimeResidentsQueryKey } from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { useRuntimeResidencyHub } from "@/core/api/signalr/useRuntimeResidencyHub";
import { toRuntimeResidents } from "@/features/runtime-resources/models/RuntimeResourcesModels";

const runtimeResidentsPollIntervalMs = 5000;

// Safety net while the hub is live, for a change no server-side raise point announced.
const runtimeResidentsPushFloorMs = 60_000;

// Image and transcription processes held in memory, next to the llama.cpp list from `useRunningModels`, kept current by
// the runtime-residency hub. The node answers from in-memory state only, so the fallback poll is as cheap as the
// resources gauge it sits beside.
export function useRuntimeResidents(enabled = true) {
	const { isLive } = useRuntimeResidencyHub(enabled, getRuntimeResidentsQueryKey());
	return useQuery({
		...withResponseValidation(getRuntimeResidentsOptions()),
		select: toRuntimeResidents,
		enabled,
		// Hub live: the push floor only. Otherwise (b): the fallback cadence while the hub is degraded or not started.
		refetchInterval: isLive ? runtimeResidentsPushFloorMs : runtimeResidentsPollIntervalMs,
	});
}
