import type { GenericAbortSignal } from "axios";

import { axiosInstance } from "@/core/api/axios/AxiosInstance";
import { ApiError } from "@/core/api/errors/ApiError";
import { buildLocalApiUrl } from "@/core/api/utils/LocalApiUrl";
import {
	type CancelAllPreviewRunsResponse,
	cancelAllPreviewRunsResponseSchema,
	listPreviewRunsResponseSchema,
	listPreviewWorkflowsResponseSchema,
	type PreviewWorkflowDetail,
	previewWorkflowDetailSchema,
	type PreviewWorkflowGraph,
	type PreviewWorkflowSummary,
	type PreviewRunStartedResponse,
	previewRunStartedResponseSchema,
	type PreviewRunSummary,
	previewRunSummarySchema,
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
	// Send an empty object body so axios sets `Content-Type: application/json`. These endpoints bind only the route
	// param, but a body-less POST omits the Content-Type, which FastEndpoints answers with 415. The backend binder
	// also tolerates a missing body; the `{}` here keeps every client (incl. raw fetch) on the happy path.
	const response = await axiosInstance.post(buildLocalApiUrl(`preview/workflows/${workflowId}/execute`), {}, { signal });
	return previewRunStartedResponseSchema.parse(response.data);
}

// Execute an unsaved (inline) graph — persists nothing. Returns the new run id.
export async function executeUnsavedPreviewWorkflow(graph: PreviewWorkflowGraph, signal?: GenericAbortSignal): Promise<PreviewRunStartedResponse> {
	const response = await axiosInstance.post(buildLocalApiUrl("preview/runs/execute"), { graph }, { signal });
	return previewRunStartedResponseSchema.parse(response.data);
}

// Resume a Paused run. 404 unknown/expired, 409 wrong state (both surfaced as ApiError); 202 accepted.
export async function continuePreviewRun(runId: string, signal?: GenericAbortSignal): Promise<void> {
	// Empty object body → axios sets `Content-Type: application/json`, avoiding the FastEndpoints 415 on a body-less
	// route-only POST (the backend binder tolerates the missing body too; this keeps raw-fetch clients working).
	await axiosInstance.post(buildLocalApiUrl(`preview/runs/${runId}/continue`), {}, { signal });
}

// Every run the node currently knows about — live ones plus terminal ones still inside the replay window. This is
// the only way a run whose id left this tab's memory (a plain page reload) is reachable at all: before it existed an
// orphaned run held its concurrency slot with no way to find or cancel it.
export async function listPreviewRuns(signal?: GenericAbortSignal): Promise<PreviewRunSummary[]> {
	const response = await axiosInstance.get(buildLocalApiUrl("preview/runs"), { signal });
	return listPreviewRunsResponseSchema.parse(response.data).items;
}

// One run by id. Returns null on 404 (unknown or evicted) so a caller reattaching from a runId in the route can drop
// a stale id instead of showing an error for a run the operator can no longer act on.
export async function getPreviewRun(runId: string, signal?: GenericAbortSignal): Promise<PreviewRunSummary | null> {
	try {
		const response = await axiosInstance.get(buildLocalApiUrl(`preview/runs/${runId}`), { signal });
		return previewRunSummarySchema.parse(response.data);
	} catch (error) {
		if (error instanceof ApiError && error.statusCode === 404) {
			return null;
		}
		throw error;
	}
}

// Cancel every live run. The operator's recovery path once runs have leaked their concurrency slots — without it the
// only way back from a CapReached 409 on an unreachable run was restarting the node.
export async function cancelAllPreviewRuns(signal?: GenericAbortSignal): Promise<CancelAllPreviewRunsResponse> {
	// Empty object body → axios sets `Content-Type: application/json` (same reason as the run-control POSTs above).
	const response = await axiosInstance.post(buildLocalApiUrl("preview/runs/cancel-all"), {}, { signal });
	return cancelAllPreviewRunsResponseSchema.parse(response.data);
}

// Request cancellation of an active run. 409 wrong state is a real error (surfaced as ApiError); 202 accepted. A
// 404 is swallowed: it means the run is already gone (completed/failed/cancelled and removed), which is the
// IDEMPOTENT outcome Cancel is trying to reach anyway — surfacing it as an error would show a confusing toast for
// a run the operator can no longer act on.
export async function cancelPreviewRun(runId: string, signal?: GenericAbortSignal): Promise<void> {
	try {
		// Empty object body → axios sets `Content-Type: application/json`, avoiding the FastEndpoints 415 on a
		// body-less route-only POST (the backend binder tolerates the missing body too; this keeps raw-fetch
		// clients working).
		await axiosInstance.post(buildLocalApiUrl(`preview/runs/${runId}/cancel`), {}, { signal });
	} catch (error) {
		if (error instanceof ApiError && error.statusCode === 404) {
			return;
		}
		throw error;
	}
}
