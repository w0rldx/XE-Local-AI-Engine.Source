import type {
	XeLocalAiEngineClientEndpointsDevelopmentV1DevelopmentArtifactResponse,
	XeLocalAiEngineClientEndpointsDevelopmentV1DevelopmentAttemptResponse,
	XeLocalAiEngineClientEndpointsDevelopmentV1DevelopmentEventResponse,
	XeLocalAiEngineClientEndpointsDevelopmentV1DevelopmentPatchPreviewResponse,
	XeLocalAiEngineClientEndpointsDevelopmentV1DevelopmentProfileDetectionResponse,
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
export type DevelopmentProfileDetection = XeLocalAiEngineClientEndpointsDevelopmentV1DevelopmentProfileDetectionResponse;

/**
 * The code-owned command profiles. These ids are a backend contract — the server rejects anything else — so they are
 * literals here rather than something derived from a response.
 */
export const developmentCommandProfileIds = {
	dotnetSlnx: "dotnet-slnx",
	dotnetCsproj: "dotnet-csproj",
	genericGit: "generic-git",
} as const;

/**
 * Derives the profile an operator-chosen build target implies. The backend pairs profile and target strictly — a
 * `.csproj` under `dotnet-slnx` is rejected — so picking a different candidate has to move the profile with it, not
 * just swap the path.
 */
export function developmentProfileIdForBuildTarget(buildTarget?: string | null): string {
	const target = buildTarget?.toLowerCase() ?? "";
	if (target.endsWith(".slnx") || target.endsWith(".sln")) {
		return developmentCommandProfileIds.dotnetSlnx;
	}
	if (target.endsWith(".csproj")) {
		return developmentCommandProfileIds.dotnetCsproj;
	}

	return developmentCommandProfileIds.genericGit;
}

/**
 * True when the profile runs no build system at all. Its validation gate is the whitespace check alone, so this must be
 * surfaced as an explicit, visible reduction in what a green validation proves — never as an unremarkable default.
 */
export function isDevelopmentWhitespaceOnlyProfile(profileId?: string | null): boolean {
	return (profileId ?? developmentCommandProfileIds.genericGit) === developmentCommandProfileIds.genericGit;
}

export interface DevelopmentRepository {
	readonly id: string;
	readonly alias: string;
	readonly availability: string;
}

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
