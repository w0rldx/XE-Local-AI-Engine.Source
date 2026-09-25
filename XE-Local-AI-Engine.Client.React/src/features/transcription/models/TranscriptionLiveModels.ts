import { z } from "zod";

import type { CaptureChannel } from "@/features/transcription/capture/CaptureSource";
import type { TranscriptChannel } from "@/features/transcription/models/TranscriptionModels";

// The wire contract of the live transcription hub, frozen by S3 (`TranscriptionHub`, `TranscriptionHubEvents`).
// Everything the hook parses off the socket is validated here first: a push is attacker-adjacent input in the same
// sense every other hub payload is (a newer or older node on the other end), and a shape the UI cannot render must
// leave the cache untouched rather than paint half a row. This mirrors `imageJobStatusPushSchema`.

/** `TranscriptSegmentCommittedPush` — one committed row, pushed as its sequence is allocated. */
export const TRANSCRIPTION_SEGMENT_COMMITTED = "transcriptionSegmentCommitted";

/** `TranscriptPartialUpdatedPush` — one lane's provisional text. Replaced on every update, never accumulated. */
export const TRANSCRIPTION_PARTIAL_UPDATED = "transcriptionPartialUpdated";

/** `TranscriptionSessionStatusPush` — the session reached a terminal state (`Completed | Cancelled | Abandoned | Failed`). */
export const TRANSCRIPTION_SESSION_STATUS_CHANGED = "transcriptionSessionStatusChanged";

/**
 * `TranscriptionCatchUpProgressPush` — how much captured audio the node has buffered but not yet transcribed. Sent at
 * most once per second of audio consumed while behind, once with 0 when caught up, and once as a graceful drain starts.
 */
export const TRANSCRIPTION_CATCH_UP_PROGRESS = "transcriptionCatchUpProgress";

/**
 * `TranscriptionAdmissionClosedPush` — the node stopped accepting audio for this session and is draining what it has.
 * Sent once at the start of every graceful end (the buffered-audio cap or a requested Stop), never for Cancel.
 */
export const TRANSCRIPTION_ADMISSION_CLOSED = "transcriptionAdmissionClosed";

/**
 * The `int channel` argument of `PushAudioFrame`, matching the backend `TranscriptChannel` enum (Mono 0, You 1,
 * Others 2). Written out rather than derived from an index, the same rule `TranscriptionMapper.ToWireChannel`
 * follows server-side: a reordered union would silently relabel every frame.
 */
export function channelToWire(channel: CaptureChannel): number {
	switch (channel) {
		case "you":
			return 1;
		case "others":
			return 2;
		default:
			return 0;
	}
}

/**
 * The inverse: the hub spells the channel PascalCase (`"Mono" | "You" | "Others"`, shared with the REST rows), the
 * client spells it lowercase. An explicit switch, never `raw.toLowerCase()` — a value the node adds later would then
 * become a `CaptureChannel` the UI has no label for. Anything unknown degrades to `mono`, which renders without a
 * badge, exactly as `toTranscriptChannel` does for the REST rows.
 */
export function fromWireChannel(raw: string): CaptureChannel {
	switch (raw) {
		case "You":
			return "you";
		case "Others":
			return "others";
		default:
			return "mono";
	}
}

export const transcriptionSegmentPushSchema = z.object({
	sessionId: z.string(),
	seq: z.number(),
	startMs: z.number(),
	endMs: z.number(),
	text: z.string(),
	channel: z.string(),
	confidence: z.number().nullish(),
});

export type TranscriptionSegmentPush = z.infer<typeof transcriptionSegmentPushSchema>;

export const transcriptionPartialPushSchema = z.object({
	sessionId: z.string(),
	channel: z.string(),
	text: z.string(),
});

export const transcriptionCatchUpProgressPushSchema = z.object({
	sessionId: z.string(),
	bufferedMs: z.number(),
});

export const transcriptionAdmissionClosedPushSchema = z.object({
	sessionId: z.string(),
});

export const transcriptionStatusPushSchema = z.object({
	sessionId: z.string(),
	status: z.string(),
	/** `live-never-attached` (no audio ever arrived; the status reads `Abandoned`) or `live-failed`; null otherwise. */
	errorCode: z.string().nullish(),
});

// `TranscriptionSessionSubscriptionSnapshot`. `segments` is the S2 REST DTO verbatim; the row's `id` is not read
// here (a committed row is identified by its sequence), so it is not required of the payload either.
export const transcriptionSnapshotSchema = z.object({
	sessionId: z.string(),
	status: z.string(),
	lastSeq: z.number(),
	segments: z.array(
		z.object({
			seq: z.number(),
			startMs: z.number(),
			endMs: z.number(),
			text: z.string(),
			channel: z.string(),
			confidence: z.number().nullish(),
		}),
	),
	replayTruncated: z.boolean(),
});

export type TranscriptionSnapshot = z.infer<typeof transcriptionSnapshotSchema>;

/**
 * The spelling the committed-row renderer wants. A live row carries the client's lowercase `CaptureChannel` while the
 * REST rows carry the backend's PascalCase names, and both go through the same row component and the same
 * `pages.transcription.channel.*` labels — so one of the two has to be translated, and the live one is the one with
 * no persisted identity to protect.
 */
export function toDisplayChannel(channel: CaptureChannel): TranscriptChannel {
	switch (channel) {
		case "you":
			return "You";
		case "others":
			return "Others";
		default:
			return "Mono";
	}
}
