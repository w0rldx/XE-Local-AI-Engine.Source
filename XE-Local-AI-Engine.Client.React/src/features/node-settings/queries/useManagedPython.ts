import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	getManagedPythonStatusOptions,
	getManagedPythonStatusQueryKey,
	removeComputePythonEnvironmentMutation,
	repairComputePythonEnvironmentMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";

// Server state for the Managed Python card. The status read never provisions anything, so it is safe on mount; it
// polls only while an environment is mid-provision, because nothing else pushes that transition to the page.

const managedPythonPollIntervalMs = 5_000;

export function useManagedPythonStatus() {
	return useQuery({
		...withResponseValidation(getManagedPythonStatusOptions()),
		refetchInterval: (query) =>
			query.state.data?.environments.some((environment) => environment.state === "Provisioning") === true
				? managedPythonPollIntervalMs
				: false,
	});
}

// Both mutations answer with the fresh status the node computed after the change, so it is written straight into the
// cache; a refetch would only read the same state again.
export function useRepairComputePython() {
	const queryClient = useQueryClient();
	return useMutation({
		...withResponseValidation(repairComputePythonEnvironmentMutation()),
		onSuccess: (data) => queryClient.setQueryData(getManagedPythonStatusQueryKey(), data),
	});
}

export function useRemoveComputePython() {
	const queryClient = useQueryClient();
	return useMutation({
		...withResponseValidation(removeComputePythonEnvironmentMutation()),
		onSuccess: (data) => queryClient.setQueryData(getManagedPythonStatusQueryKey(), data),
	});
}
