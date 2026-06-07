import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	type CreatePreviewWorkflowInput,
	cancelPreviewRun,
	continuePreviewRun,
	createPreviewWorkflow,
	deletePreviewWorkflow,
	executeSavedPreviewWorkflow,
	executeUnsavedPreviewWorkflow,
	getPreviewWorkflow,
	listPreviewWorkflows,
	type UpdatePreviewWorkflowInput,
	updatePreviewWorkflow,
} from "@/features/preview/api/PreviewWorkflowApi";
import type { PreviewWorkflowGraph } from "@/features/preview/models/PreviewWorkflowModels";

// Server state for the Open Canvas (Preview) management surface. The preview endpoints are NOT in the generated
// hey-api SDK yet, so reads/mutations wrap the hand-wired axios API (PreviewWorkflowApi) instead of generated
// `*Options()`. Query keys are hand-authored arrays (not the generated `[{ _id }]` shape); the list/detail caches
// are invalidated after every workflow CRUD mutation. Execute/continue/cancel are fire-and-forget run-control
// mutations — the live run state arrives over the hub (usePreviewWorkflowHub), not these caches — so they do not
// invalidate any query.

const previewWorkflowQueryKeys = {
	all: ["preview", "workflows"] as const,
	list: () => [...previewWorkflowQueryKeys.all, "list"] as const,
	detail: (workflowId: string) => [...previewWorkflowQueryKeys.all, "detail", workflowId] as const,
};

export function usePreviewWorkflows() {
	return useQuery({
		queryKey: previewWorkflowQueryKeys.list(),
		queryFn: ({ signal }) => listPreviewWorkflows(signal),
	});
}

// One workflow's full detail (incl. graph). Disabled until an id is supplied so the detail query only fires when
// a workflow is opened on the canvas.
export function usePreviewWorkflow(workflowId: string | null) {
	return useQuery({
		queryKey: previewWorkflowQueryKeys.detail(workflowId ?? ""),
		queryFn: ({ signal }) => getPreviewWorkflow(workflowId ?? "", signal),
		enabled: workflowId !== null,
	});
}

function invalidateWorkflows(queryClient: ReturnType<typeof useQueryClient>): Promise<void> {
	return queryClient.invalidateQueries({ queryKey: previewWorkflowQueryKeys.all });
}

export function useCreatePreviewWorkflow() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: (input: CreatePreviewWorkflowInput) => createPreviewWorkflow(input),
		onSuccess: () => invalidateWorkflows(queryClient),
	});
}

// Update carries the Version for optimistic concurrency. A stale version makes the backend return 409, which the
// shared ProblemDetails interceptor surfaces as an ApiError (statusCode 409) — the caller (form) inspects it and
// shows the conflict banner. onSuccess refreshes the list + detail caches.
export function useUpdatePreviewWorkflow() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: (input: UpdatePreviewWorkflowInput) => updatePreviewWorkflow(input),
		onSuccess: () => invalidateWorkflows(queryClient),
	});
}

export function useDeletePreviewWorkflow() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: (workflowId: string) => deletePreviewWorkflow(workflowId),
		onSuccess: () => invalidateWorkflows(queryClient),
	});
}

// Execute a saved workflow. Returns the new runId so the page can register it as one of the tab's active runs
// (the run-output store ignores hub events for runs it did not start).
export function useExecuteSavedPreviewWorkflow() {
	return useMutation({
		mutationFn: (workflowId: string) => executeSavedPreviewWorkflow(workflowId),
	});
}

// Execute an unsaved inline graph (persists nothing). Returns the new runId.
export function useExecuteUnsavedPreviewWorkflow() {
	return useMutation({
		mutationFn: (graph: PreviewWorkflowGraph) => executeUnsavedPreviewWorkflow(graph),
	});
}

// Resume a paused run. Uses no cache invalidation — the resumed run's progress arrives over the hub.
export function useContinuePreviewRun() {
	return useMutation({
		mutationFn: (runId: string) => continuePreviewRun(runId),
	});
}

// Cancel an active run. The terminal `preview.run.cancelled` event arrives over the hub.
export function useCancelPreviewRun() {
	return useMutation({
		mutationFn: (runId: string) => cancelPreviewRun(runId),
	});
}
