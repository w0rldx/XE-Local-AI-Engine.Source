import { useMutation, useQueryClient } from "@tanstack/react-query";

import { ejectImageRuntimeMutation, ejectTranscriptionRuntimeMutation } from "@/core/api/generated/@tanstack/react-query.gen";

// The image and transcription runtime ejects, shared by each runtime's own card and the shell's aggregator widget.
// Generated keys are object arrays; TanStack partial matching on `_id` invalidates every endpoint variant.
const imageRuntimeOperationId = "getImageRuntimeStatus";
const transcriptionRuntimeOperationId = "getTranscriptionRuntimeStatus";

export function useEjectImageRuntime() {
	const queryClient = useQueryClient();
	return useMutation({
		mutationFn: async () => {
			const options = ejectImageRuntimeMutation();
			return await options.mutationFn?.({ body: {} }, undefined as never);
		},
		onSuccess: () => queryClient.invalidateQueries({ queryKey: [{ _id: imageRuntimeOperationId }] }),
	});
}

// Unloads the whisper model from memory (never a disk delete). Refreshes the runtime status so the card reflects it.
export function useEjectTranscriptionRuntime() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async () => {
			const options = ejectTranscriptionRuntimeMutation();
			// `accepted` is the contract's transport placeholder for an otherwise body-less POST; the server neither
			// requires nor interprets it.
			return await options.mutationFn?.({ body: { accepted: true } }, undefined as never);
		},
		onSuccess: () => queryClient.invalidateQueries({ queryKey: [{ _id: transcriptionRuntimeOperationId }] }),
	});
}
