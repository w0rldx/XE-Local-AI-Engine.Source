import { ApiError } from "@/core/api/errors/ApiError";
import type {
	XeLocalAiEngineClientEndpointsTranscriptionV1TranscriptionModelResponse as TranscriptionModelResponse,
	XeLocalAiEngineClientEndpointsTranscriptionV1TranscriptionRuntimeStatusResponse as TranscriptionRuntimeStatusResponse,
	XeLocalAiEngineClientEndpointsTranscriptionV1TranscriptionSessionConfigResponse as TranscriptionSessionConfigResponse,
	XeLocalAiEngineClientEndpointsTranscriptionV1TranscriptionSessionDetailResponse as TranscriptionSessionDetailResponse,
	XeLocalAiEngineClientEndpointsTranscriptionV1TranscriptionSessionSummaryResponse as TranscriptionSessionSummaryResponse,
	XeLocalAiEngineClientEndpointsTranscriptionV1TranscriptSegmentResponse as TranscriptSegmentResponse,
} from "@/core/api/generated";

// Wire enums arrive as the backend's C# NAMES (not camelCase) — see the endpoint DTOs. They are normalized into these
// unions so a value the backend adds later degrades to a sane default instead of crashing a render.
const transcriptionSessionStatuses = ["Created", "Transcribing", "Completed", "Failed", "Cancelled"] as const;
export type TranscriptionSessionStatus = (typeof transcriptionSessionStatuses)[number];

// Every kind the backend enum can persist. Dictation and ApplicationProcess are not creatable from any surface yet,
// but a row carrying one must keep its own identity: degrading it to File would tell the operator a dictated session
// came from a file.
const transcriptionSourceKinds = [
	"File",
	"Microphone",
	"SystemAudio",
	"MicrophoneAndSystem",
	"Dictation",
	"ApplicationProcess",
] as const;
export type TranscriptionSourceKind = (typeof transcriptionSourceKinds)[number];

// The subset the new-session dialog offers. All four are rendered, three of them disabled, so enabling live capture
// is a data change rather than a layout change.
export const transcriptionDialogSourceKinds = ["File", "Microphone", "SystemAudio", "MicrophoneAndSystem"] as const;

const transcriptChannels = ["Mono", "You", "Others"] as const;
export type TranscriptChannel = (typeof transcriptChannels)[number];

export type TranscriptionLanguageMode = "auto" | "override";

/** Language codes the dialog offers beside auto-detection. The backend accepts any 2-8 character code. */
export const transcriptionLanguageCodes = ["en", "de", "fr", "es", "it", "nl", "pt", "ja", "zh"] as const;

export function toTranscriptionSessionStatus(raw: string | null | undefined): TranscriptionSessionStatus {
	return (transcriptionSessionStatuses as readonly string[]).includes(raw ?? "")
		? (raw as TranscriptionSessionStatus)
		: "Created";
}

// A kind outside the backend enum entirely (a client older than the node) falls back to File — the only kind every
// version has had.
export function toTranscriptionSourceKind(raw: string | null | undefined): TranscriptionSourceKind {
	return (transcriptionSourceKinds as readonly string[]).includes(raw ?? "") ? (raw as TranscriptionSourceKind) : "File";
}

// Anything that is not one of the three known channels renders as Mono, i.e. with no badge — a badge naming a channel
// the UI cannot explain is worse than no badge.
export function toTranscriptChannel(raw: string | null | undefined): TranscriptChannel {
	return (transcriptChannels as readonly string[]).includes(raw ?? "") ? (raw as TranscriptChannel) : "Mono";
}

// Strict view-models over the (optional-field) generated DTOs — the shape every component renders against.
export interface TranscriptionSessionView {
	id: string;
	title: string | null;
	status: TranscriptionSessionStatus;
	sourceKind: TranscriptionSourceKind;
	modelId: string;
	detectedLanguage: string | null;
	durationMs: number | null;
	segmentCount: number;
	createdAtUtc: number;
	updatedAtUtc: number;
}

export interface TranscriptSegmentView {
	id: string;
	seq: number;
	startMs: number;
	endMs: number;
	text: string;
	channel: TranscriptChannel;
	confidence: number | null;
}

export interface TranscriptionSessionConfigView {
	languageMode: TranscriptionLanguageMode;
	languageOverride: string | null;
	translate: boolean;
	maxWindowSeconds: number;
	channelAttribution: boolean;
}

export interface TranscriptionSessionDetailView {
	session: TranscriptionSessionView;
	segments: readonly TranscriptSegmentView[];
	config: TranscriptionSessionConfigView;
	errorCode: string | null;
	errorMessage: string | null;
}

export function toTranscriptionSessionView(dto: TranscriptionSessionSummaryResponse): TranscriptionSessionView {
	return {
		id: dto.id,
		title: dto.title ?? null,
		status: toTranscriptionSessionStatus(dto.status),
		sourceKind: toTranscriptionSourceKind(dto.sourceKind),
		modelId: dto.modelId,
		detectedLanguage: dto.detectedLanguage ?? null,
		durationMs: dto.durationMs ?? null,
		segmentCount: dto.segmentCount,
		createdAtUtc: dto.createdAtUtc,
		updatedAtUtc: dto.updatedAtUtc,
	};
}

function toTranscriptSegmentView(dto: TranscriptSegmentResponse): TranscriptSegmentView {
	return {
		id: dto.id,
		seq: dto.seq,
		startMs: dto.startMs,
		endMs: dto.endMs,
		text: dto.text,
		channel: toTranscriptChannel(dto.channel),
		confidence: dto.confidence ?? null,
	};
}

function toTranscriptionSessionConfigView(dto: TranscriptionSessionConfigResponse): TranscriptionSessionConfigView {
	return {
		languageMode: dto.languageMode === "override" ? "override" : "auto",
		languageOverride: dto.languageOverride ?? null,
		translate: dto.translate,
		maxWindowSeconds: dto.maxWindowSeconds,
		channelAttribution: dto.channelAttribution,
	};
}

export function toTranscriptionSessionDetailView(dto: TranscriptionSessionDetailResponse): TranscriptionSessionDetailView {
	return {
		session: toTranscriptionSessionView(dto.session),
		// Segments arrive in Seq order from the store, but the render order is the contract this list depends on, so it
		// is asserted here rather than assumed.
		segments: [...dto.segments].map(toTranscriptSegmentView).sort((a, b) => a.seq - b.seq),
		config: toTranscriptionSessionConfigView(dto.config),
		errorCode: dto.errorCode ?? null,
		errorMessage: dto.errorMessage ?? null,
	};
}

/**
 * The four phases a coordinated weight transfer reports. Anything outside them is treated as no download at all: a
 * row that cannot be explained must not claim to be running, because the operator would then wait for a transfer
 * that no longer exists.
 */
const transcriptionDownloadPhases = ["running", "completed", "cancelled", "failed"] as const;
export type TranscriptionDownloadPhase = (typeof transcriptionDownloadPhases)[number];

export interface TranscriptionModelView {
	id: string;
	tier: string;
	sizeBytes: number;
	englishOnly: boolean;
	installed: boolean;
	downloadPhase: TranscriptionDownloadPhase | null;
	downloadPercent: number | null;
	/** The coordinator's operator-safe failure reason; never a path, a URL or a token. */
	downloadError: string | null;
}

export function toTranscriptionModelView(dto: TranscriptionModelResponse): TranscriptionModelView {
	const download = dto.download ?? null;
	const completed = download?.completedBytes ?? null;
	const total = download?.totalBytes ?? null;
	const phase = download?.phase ?? null;
	return {
		id: dto.id,
		tier: dto.tier,
		sizeBytes: dto.sizeBytes,
		englishOnly: dto.englishOnly,
		installed: dto.installed,
		downloadPhase: (transcriptionDownloadPhases as readonly string[]).includes(phase ?? "")
			? (phase as TranscriptionDownloadPhase)
			: null,
		downloadPercent: completed !== null && total !== null && total > 0 ? (completed / total) * 100 : null,
		downloadError: download?.sanitizedError ?? null,
	};
}

export interface TranscriptionRuntimeView {
	enabled: boolean;
	state: TranscriptionRuntimeStatusResponse["state"];
	// null until a daemon has actually spawned — never read as "not installed".
	backend: string | null;
	binarySource: string | null;
	binaryVersion: string | null;
	selectedModelId: string | null;
	recommendedModelId: string;
	vadInstalled: boolean;
	supportsTranscode: boolean;
	managedRuntimeValidity: string | null;
	/** True only where the node can capture one application's audio server-side (Windows 10 build 20348 and later). */
	processCaptureSupported: boolean;
}

export function toTranscriptionRuntimeView(dto: TranscriptionRuntimeStatusResponse): TranscriptionRuntimeView {
	return {
		enabled: dto.enabled,
		state: dto.state,
		backend: dto.backend ?? null,
		binarySource: dto.binarySource ?? null,
		binaryVersion: dto.binaryVersion ?? null,
		selectedModelId: dto.selectedModelId ?? null,
		recommendedModelId: dto.recommendedModelId,
		vadInstalled: dto.vadInstalled,
		supportsTranscode: dto.supportsTranscode,
		managedRuntimeValidity: dto.managedRuntime?.validity ?? null,
		processCaptureSupported: dto.processCaptureSupported,
	};
}

/**
 * The reason codes the capture/process endpoint answers a refusal with (`ProcessCaptureBlockedResponse.reason`).
 * Each names a different thing for the operator to do, so they are read off the typed body rather than collapsed
 * into one "the node refused" sentence.
 */
const processCaptureBlockedReasons = ["capture-not-supported", "session-not-live", "capture-already-running"] as const;
export type ProcessCaptureBlockedReason = (typeof processCaptureBlockedReasons)[number];

/**
 * Reads the capture/process endpoint's typed refusal off a thrown error, or null for anything else.
 *
 * The 400 and 409 bodies are `{ reason, message }`, not ProblemDetails, so the shared axios interceptor parks the
 * whole body on `apiProblemDetails` — the same shape `unsupportedContainerDetail` reads. Only the three reasons this
 * endpoint documents are recognised: an unknown one falls back to the generic refusal rather than inventing a key
 * that no locale has.
 */
export function processCaptureBlockedReason(error: unknown): ProcessCaptureBlockedReason | null {
	if (!(error instanceof ApiError)) {
		return null;
	}
	const reason = (error.apiProblemDetails as unknown as Record<string, unknown> | undefined)?.["reason"];
	return (processCaptureBlockedReasons as readonly string[]).includes(reason as string)
		? (reason as ProcessCaptureBlockedReason)
		: null;
}

/** The container list the node can actually read, carried on the upload endpoint's typed 415 body. */
export interface UnsupportedContainerDetail {
	supportedContainers: readonly string[];
	ffmpegRequired: boolean;
}

/**
 * Reads the upload endpoint's typed 415 body off a thrown error.
 *
 * The 415 is a returned refusal with a per-feature body (`TranscriptionUnsupportedContainerResponse`), not
 * ProblemDetails, so the shared axios interceptor still produces an `ApiError` but parks the whole body on
 * `apiProblemDetails`. Returning null for anything else lets the caller fall back to the plain message.
 */
export function unsupportedContainerDetail(error: unknown): UnsupportedContainerDetail | null {
	if (!(error instanceof ApiError) || error.statusCode !== 415) {
		return null;
	}
	const body = error.apiProblemDetails as unknown as Record<string, unknown> | undefined;
	const containers = body?.["supportedContainers"];
	if (!Array.isArray(containers)) {
		return null;
	}
	return {
		supportedContainers: containers.filter((value): value is string => typeof value === "string"),
		ffmpegRequired: body?.["ffmpegRequired"] === true,
	};
}
