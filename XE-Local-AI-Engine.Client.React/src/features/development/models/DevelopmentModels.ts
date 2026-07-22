import type {
	XeLocalAiEngineClientEndpointsDevelopmentV1DevelopmentArtifactResponse,
	XeLocalAiEngineClientEndpointsDevelopmentV1DevelopmentAttemptResponse,
	XeLocalAiEngineClientEndpointsDevelopmentV1DevelopmentEventResponse,
	XeLocalAiEngineClientEndpointsDevelopmentV1DevelopmentPatchPreviewResponse,
	XeLocalAiEngineClientEndpointsDevelopmentV1DevelopmentProjectDetailResponse,
	XeLocalAiEngineClientEndpointsDevelopmentV1DevelopmentProjectResponse,
	XeLocalAiEngineClientEndpointsDevelopmentV1DevelopmentTaskDetailResponse,
} from "@/core/api/generated";

export type DevelopmentProject = XeLocalAiEngineClientEndpointsDevelopmentV1DevelopmentProjectResponse;
export type DevelopmentProjectDetail = XeLocalAiEngineClientEndpointsDevelopmentV1DevelopmentProjectDetailResponse;
export type DevelopmentTaskDetail = XeLocalAiEngineClientEndpointsDevelopmentV1DevelopmentTaskDetailResponse;
export type DevelopmentAttempt = XeLocalAiEngineClientEndpointsDevelopmentV1DevelopmentAttemptResponse;
export type DevelopmentArtifact = XeLocalAiEngineClientEndpointsDevelopmentV1DevelopmentArtifactResponse;
export type DevelopmentEvent = XeLocalAiEngineClientEndpointsDevelopmentV1DevelopmentEventResponse;
export type DevelopmentPatchPreview = XeLocalAiEngineClientEndpointsDevelopmentV1DevelopmentPatchPreviewResponse;

export type DevelopmentLiveUpdateKind =
	| "Output"
	| "Activity"
	| "Tool"
	| "Command"
	| "Metrics"
	| "Progress"
	| "Warning"
	| "Terminal";

export interface DevelopmentAttemptLiveUpdate {
	readonly projectId: string;
	readonly taskId: string;
	readonly attemptId: string;
	readonly sequence: number;
	readonly occurredAtUtc: number;
	readonly kind: DevelopmentLiveUpdateKind;
	readonly role: string;
	readonly status: string;
	readonly modelId: string;
	readonly provider: string;
	readonly outputDelta?: string | null;
	readonly currentActivity?: string | null;
	readonly inputTokens?: number | null;
	readonly outputTokens?: number | null;
	readonly reasoningTokens?: number | null;
	readonly outputTokensPerSecond?: number | null;
	readonly providerRoundCount: number;
	readonly toolCallCount: number;
	readonly commandCount: number;
	readonly currentToolId?: string | null;
	readonly currentCommandId?: string | null;
	readonly currentOperationElapsedMilliseconds?: number | null;
	readonly changedFileCount: number;
	readonly patchByteCount: number;
	readonly subjectHash?: string | null;
	readonly contextUsagePercent?: number | null;
	readonly contextHeadroomPercent?: number | null;
	readonly secondsSinceMeaningfulProgress: number;
	readonly warningCategory?: string | null;
	readonly warningMessage?: string | null;
}

export interface DevelopmentAttemptSubscriptionSnapshot {
	readonly projectId: string;
	readonly taskId: string;
	readonly attemptId: string;
	readonly watermark: number;
	readonly droppedOrCoalescedUpdateCount: number;
	readonly latest?: DevelopmentAttemptLiveUpdate | null;
}

export const activeAttemptStatuses = new Set(["Pending", "Running"]);

export function isActiveAttempt(attempt: DevelopmentAttempt): boolean {
	return activeAttemptStatuses.has(attempt.status ?? "");
}
