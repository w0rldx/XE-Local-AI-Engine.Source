import type { GenericAbortSignal } from "axios";

import { axiosInstance } from "@/core/api/axios/AxiosInstance";
import { buildLocalApiUrl } from "@/core/api/utils/LocalApiUrl";
import {
	listPreviewWorkflowsResponseSchema,
	type PreviewWorkflowDetail,
	previewWorkflowDetailSchema,
	type PreviewWorkflowGraph,
	type PreviewWorkflowSummary,
	type PreviewRunStartedResponse,
	previewRunStartedResponseSchema,
} from "@/features/preview/models/PreviewWorkflowModels";

// Hand-wrapped REST surface for the Open Canvas (Preview) endpoints. The generated hey-api SDK does NOT yet
// include the preview endpoints (the OpenAPI regen runs later against a live host), so this feature talks to the
// backend directly through the SHARED axios instance + buildLocalApiUrl — exactly the seam scheduler/mcp use for
// their generated calls, just hand-wired. The shared instance carries the auth request interceptor, the 401
// interceptor, and the ProblemDetails interceptor, so a non-2xx response (incl. 409 on a stale PUT) already
// surfaces as an ApiError with the parsed ProblemDetails — callers read `error.statusCode === 409`. Every read
// validates the response against the zod schema so a wire-shape drift fails loudly instead of silently
// corrupting the canvas; the runId from an execute is validated likewise. Reads wire the TanStack Query
// AbortSignal so a query that unmounts/refetches cancels the in-flight request.

// Base prefix `api/local/v1/preview` is produced by buildLocalApiUrl from the relative paths below (it prepends
// `/api/local/<version>`). The paths mirror LocalApiRoutes.Preview exactly.

export async function listPreviewWorkflows(signal?: GenericAbortSignal): Promise<PreviewWorkflowSummary[]> {
	const response = await axiosInstance.get(buildLocalApiUrl("preview/workflows"), { signal });
	return listPreviewWorkflowsResponseSchema.parse(response.data).items;
}

export async function getPreviewWorkflow(workflowId: string, signal?: GenericAbortSignal): Promise<PreviewWorkflowDetail> {
	const response = await axiosInstance.get(buildLocalApiUrl(`preview/workflows/${workflowId}`), { signal });
	return previewWorkflowDetailSchema.parse(response.data);
}

export interface CreatePreviewWorkflowInput {
	readonly name: string;
	readonly graph: PreviewWorkflowGraph;
}

export async function createPreviewWorkflow(input: CreatePreviewWorkflowInput): Promise<PreviewWorkflowDetail> {
	const response = await axiosInstance.post(buildLocalApiUrl("preview/workflows"), {
		name: input.name,
		graph: input.graph,
	});
	return previewWorkflowDetailSchema.parse(response.data);
}

export interface UpdatePreviewWorkflowInput {
	readonly workflowId: string;
	readonly name: string;
	readonly graph: PreviewWorkflowGraph;
	// Drives optimistic concurrency: a stale Version makes the backend return 409 (surfaced as ApiError 409).
	readonly version: number;
}

export async function updatePreviewWorkflow(input: UpdatePreviewWorkflowInput): Promise<PreviewWorkflowDetail> {
	const response = await axiosInstance.put(buildLocalApiUrl(`preview/workflows/${input.workflowId}`), {
		workflowId: input.workflowId,
		name: input.name,
		graph: input.graph,
		version: input.version,
	});
	return previewWorkflowDetailSchema.parse(response.data);
}

export async function deletePreviewWorkflow(workflowId: string): Promise<void> {
	await axiosInstance.delete(buildLocalApiUrl(`preview/workflows/${workflowId}`));
}

// Execute a saved workflow by id. Returns the new run id the client subscribes to over the hub. The optional
// AbortSignal lets a caller cancel the in-flight request (e.g. the mutation is torn down on unmount), mirroring the
// reads above.
export async function executeSavedPreviewWorkflow(workflowId: string, signal?: GenericAbortSignal): Promise<PreviewRunStartedResponse> {
	const response = await axiosInstance.post(buildLocalApiUrl(`preview/workflows/${workflowId}/execute`), undefined, { signal });
	return previewRunStartedResponseSchema.parse(response.data);
}

// Execute an unsaved (inline) graph — persists nothing. Returns the new run id.
export async function executeUnsavedPreviewWorkflow(graph: PreviewWorkflowGraph, signal?: GenericAbortSignal): Promise<PreviewRunStartedResponse> {
	const response = await axiosInstance.post(buildLocalApiUrl("preview/runs/execute"), { graph }, { signal });
	return previewRunStartedResponseSchema.parse(response.data);
}

// Resume a Paused run. 404 unknown/expired, 409 wrong state (both surfaced as ApiError); 202 accepted.
export async function continuePreviewRun(runId: string, signal?: GenericAbortSignal): Promise<void> {
	await axiosInstance.post(buildLocalApiUrl(`preview/runs/${runId}/continue`), undefined, { signal });
}

// Request cancellation of an active run. 404 unknown, 409 wrong state (both surfaced as ApiError); 202 accepted.
export async function cancelPreviewRun(runId: string, signal?: GenericAbortSignal): Promise<void> {
	await axiosInstance.post(buildLocalApiUrl(`preview/runs/${runId}/cancel`), undefined, { signal });
}
