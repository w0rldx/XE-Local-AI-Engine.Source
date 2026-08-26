// Domain vocabulary for the work-session surface. The generated client types every enum as a bare `string` (they
// cross the wire as names), so these unions are the client-side narrowing — and the place a status typo fails to
// compile instead of silently falling through a colour map.

import type {
	XeLocalAiEngineClientEndpointsWorkSessionsV1WorkSessionArtifactResponse as WorkSessionArtifactResponse,
	XeLocalAiEngineClientEndpointsWorkSessionsV1WorkSessionCheckpointResponse as WorkSessionCheckpointResponse,
	XeLocalAiEngineClientEndpointsWorkSessionsV1WorkSessionEventResponse as WorkSessionEventResponse,
	XeLocalAiEngineClientEndpointsWorkSessionsV1WorkSessionFindingResponse as WorkSessionFindingResponse,
	XeLocalAiEngineClientEndpointsWorkSessionsV1WorkSessionResponse as WorkSessionResponse,
	XeLocalAiEngineClientEndpointsWorkSessionsV1WorkSessionSummaryResponse as WorkSessionSummaryResponse,
	XeLocalAiEngineClientEndpointsWorkSessionsV1WorkSessionTaskResponse as WorkSessionTaskResponse,
} from "@/core/api/generated/types.gen";

export type {
	WorkSessionArtifactResponse,
	WorkSessionCheckpointResponse,
	WorkSessionEventResponse,
	WorkSessionFindingResponse,
	WorkSessionResponse,
	WorkSessionSummaryResponse,
	WorkSessionTaskResponse,
};

const workSessionStatuses = [
	"Draft",
	"Running",
	"Paused",
	"WaitingForInput",
	"WaitingForApproval",
	"Completed",
	"Failed",
	"Cancelled",
	"Interrupted",
] as const;
export type WorkSessionStatus = (typeof workSessionStatuses)[number];

// `Development` is reserved by the brief (Q1) and deliberately not offered until the Dev-Mode chat series lands.
export const workSessionKinds = ["General", "Research"] as const;
export type WorkSessionKind = (typeof workSessionKinds)[number];

const workSessionTaskStatuses = ["Planned", "Active", "Blocked", "Done", "Dropped"] as const;
export type WorkSessionTaskStatus = (typeof workSessionTaskStatuses)[number];

export const workSessionFindingKinds = ["Finding", "Evidence", "Decision", "OpenQuestion"] as const;
export type WorkSessionFindingKind = (typeof workSessionFindingKinds)[number];

const workSessionArtifactKinds = ["Report", "Note", "File", "Patch"] as const;
export type WorkSessionArtifactKind = (typeof workSessionArtifactKinds)[number];

function narrow<T extends string>(values: readonly T[], value: string | undefined, fallback: T): T {
	return values.includes(value as T) ? (value as T) : fallback;
}

/** A status the server did not send, or sent unknown, reads as `Draft` — the state with no destructive controls. */
export function toWorkSessionStatus(value: string | undefined): WorkSessionStatus {
	return narrow(workSessionStatuses, value, "Draft");
}

export function toWorkSessionKind(value: string | undefined): WorkSessionKind {
	return narrow(workSessionKinds, value, "General");
}

export function toWorkSessionTaskStatus(value: string | undefined): WorkSessionTaskStatus {
	return narrow(workSessionTaskStatuses, value, "Planned");
}

export function toWorkSessionFindingKind(value: string | undefined): WorkSessionFindingKind {
	return narrow(workSessionFindingKinds, value, "Finding");
}

export function toWorkSessionArtifactKind(value: string | undefined): WorkSessionArtifactKind {
	return narrow(workSessionArtifactKinds, value, "Note");
}

/**
 * The session is doing work, or is parked mid-step waiting for the operator. All four hold the node's single
 * invocation slot, so they share the same controls (Pause / Cancel) and the same "queued for the next step"
 * composer promise — there is nothing to resume while a step is live.
 */
export function isActiveWorkSessionStatus(status: WorkSessionStatus): boolean {
	return status === "Running" || status === "WaitingForApproval" || status === "WaitingForInput";
}

/** No further steps will run and the session takes no more input. */
export function isTerminalWorkSessionStatus(status: WorkSessionStatus): boolean {
	return status === "Completed" || status === "Failed" || status === "Cancelled";
}

/**
 * Posting a follow-up also resumes paused or interrupted sessions. `Draft` is deliberately excluded: it has no next
 * step until Start, so its composer must promise "used when you start", never "resuming".
 */
export function resumesOnFollowUp(status: WorkSessionStatus): boolean {
	return status === "Paused" || status === "Interrupted";
}

/** Monaco language for an artifact. Patches render as a diff; everything else follows its media type. */
export function artifactEditorLanguage(kind: WorkSessionArtifactKind, mediaType: string | undefined): string {
	if (kind === "Patch") {
		return "diff";
	}
	const type = (mediaType ?? "").toLowerCase();
	if (type.includes("json")) {
		return "json";
	}
	if (type.includes("markdown")) {
		return "markdown";
	}
	if (type.includes("xml") || type.includes("html")) {
		return "xml";
	}
	if (type.includes("patch") || type.includes("diff")) {
		return "diff";
	}
	return "plaintext";
}

/**
 * Decodes an artifact body for the read-only editor. The server sends allowlisted text media types directly and
 * base64-encodes every other type, so invalid UTF-8 is reported as binary instead of rendered as mojibake.
 */
export function decodeArtifactContent(content: string, isBase64: boolean): { text: string; isBinary: boolean } {
	if (!isBase64) {
		return { text: content, isBinary: false };
	}
	try {
		const binary = atob(content);
		const bytes = Uint8Array.from(binary, (character) => character.charCodeAt(0));
		// `fatal` is what turns "this is not text" into an error instead of a page full of replacement characters.
		return { text: new TextDecoder("utf-8", { fatal: true }).decode(bytes), isBinary: false };
	} catch {
		return { text: "", isBinary: true };
	}
}
