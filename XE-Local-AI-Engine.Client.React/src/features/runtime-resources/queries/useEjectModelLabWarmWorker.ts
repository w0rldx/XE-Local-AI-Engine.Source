import { useMutation } from "@tanstack/react-query";

import { ejectModelLabWarmWorkerMutation } from "@/core/api/generated/@tanstack/react-query.gen";

// Stops the idle Model Lab worker that keeps a model warm (never a disk delete). The node answers 409 while a job
// holds the worker; the widget refreshes its own residents and gauge on success.
export function useEjectModelLabWarmWorker() {
	return useMutation({
		mutationFn: async () => {
			const options = ejectModelLabWarmWorkerMutation();
			return await options.mutationFn?.({}, undefined as never);
		},
	});
}
