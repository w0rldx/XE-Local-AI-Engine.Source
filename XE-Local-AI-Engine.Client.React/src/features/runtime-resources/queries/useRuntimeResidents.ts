import { useQuery } from "@tanstack/react-query";

import { getRuntimeResidentsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { toRuntimeResidents } from "@/features/runtime-resources/models/RuntimeResourcesModels";

const runtimeResidentsPollIntervalMs = 5000;

// Image and transcription processes held in memory, next to the llama.cpp list from `useRunningModels`. The node
// answers from in-memory state only, so the poll is as cheap as the resources gauge it sits beside.
export function useRuntimeResidents(enabled = true) {
	return useQuery({
		...withResponseValidation(getRuntimeResidentsOptions()),
		select: toRuntimeResidents,
		enabled,
		refetchInterval: runtimeResidentsPollIntervalMs,
	});
}
