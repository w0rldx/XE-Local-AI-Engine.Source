import { HubConnectionState } from "@microsoft/signalr";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useCallback, useEffect, useRef, useState } from "react";

import { acquireHubConnection } from "@/core/api/signalr/SharedHubConnection";
import { type CaptureChannel, CaptureError } from "@/features/transcription/capture/CaptureSource";
import {
	TRANSCRIPTION_ADMISSION_CLOSED,
	TRANSCRIPTION_CATCH_UP_PROGRESS,
	TRANSCRIPTION_PARTIAL_UPDATED,
	TRANSCRIPTION_SEGMENT_COMMITTED,
	TRANSCRIPTION_SESSION_STATUS_CHANGED,
	type TranscriptionSegmentPush,
	channelToWire,
	fromWireChannel,
	transcriptionAdmissionClosedPushSchema,
	transcriptionCatchUpProgressPushSchema,
	transcriptionPartialPushSchema,
	transcriptionSegmentPushSchema,
	transcriptionSnapshotSchema,
	transcriptionStatusPushSchema,
} from "@/features/transcription/models/TranscriptionLiveModels";

// Live transcript for one session over `transcription/hub`.
//
// Connection lifetime follows `useImageJobHub` (one shared refcounted connection, handlers per mount, invokes guarded
// on Connected, re-applied on reconnect). The SUBSCRIPTION coordination follows
// `useGraphWorkflowRunHub`: `SubscribeSession` is an awaited invoke, so live pushes can arrive between issuing it and
// its snapshot resolving, and those are buffered and replayed through the same apply the live path uses.
//
// Unlike the graph hub, the pushes here carry the content: there is no REST read behind them, so a dropped push is a
// hole in the operator's transcript that nothing refetches. That is why merging is by exact sequence identity rather
// than by a `<= lastSeq` drop, why a truncated replay is drained to the end before the cursor moves, and why a frame
// the client cannot send raises instead of being dropped.

const HUB_PATH = "transcription/hub";
// An acknowledgement timeout, not a health check: the node acknowledges a frame once it is queued, so an invoke
// unacknowledged for 30 s is a stalled transport.
const PUSH_FRAME_TIMEOUT_MS = 30_000;

export interface CommittedSegment {
	readonly seq: number;
	readonly startMs: number;
	readonly endMs: number;
	readonly text: string;
	readonly channel: CaptureChannel;
	readonly confidence: number | null;
}

export interface LiveTranscriptView {
	/** Ordered by `(startMs, seq)`: the sequence is publication order across two lanes, which is not speech order. */
	readonly committed: readonly CommittedSegment[];
	/** One provisional line per channel. Replaced on each update — provisional text is never accumulated. */
	readonly partials: Readonly<Partial<Record<CaptureChannel, string>>>;
	readonly status: string;
	/**
	 * Captured audio the node has buffered but not yet transcribed, from `transcriptionCatchUpProgress`. Null until the
	 * first progress push: "no report yet" and "caught up" (0) read differently while a stop drains.
	 */
	readonly bufferedMs: number | null;
	/** The watermark every row up to which has actually been delivered. What a reconnect resumes from. */
	readonly lastSeq: number;
	readonly replayTruncated: boolean;
}

/**
 * The replay drain stopped because the node stopped advancing the cursor — a page came back claiming more rows exist
 * but reporting a `lastSeq` no higher than the one asked for. Looping on that is an infinite subscribe storm, so the
 * hook fails here instead, and keeps `lastDrainedSeq`: the last cursor every row of which was actually delivered, so
 * a later reconnect resumes from a whole watermark rather than a half-drained one.
 */
export interface TranscriptionReplayStalled {
	readonly code: "transcription-replay-stalled";
	readonly lastDrainedSeq: number;
}

/**
 * The node refused the subscription, or answered it with something that did not match the contract. Nothing
 * retries it: `withAutomaticReconnect` does not retry an initial start either, so without this the view would sit
 * permanently empty and `connected: false` with no signal at all while the UI kept offering Start.
 */
export interface TranscriptionSubscribeFailed {
	/** The hub's own stable refusal code, or `transcription-subscribe-failed` for anything it did not name. */
	readonly code: string;
}

/** Refusals the hub names itself (`TranscriptionHubErrors`); everything else is reported under one code of ours. */
const HUB_REFUSAL_CODE = /^transcription-[a-z0-9-]+$/;

function toSubscribeFailureCode(error: unknown): string {
	const message = error instanceof Error ? error.message : "";
	return HUB_REFUSAL_CODE.test(message) ? message : "transcription-subscribe-failed";
}

/** The terminal status the node pushed for this session while this hook was subscribed. */
export interface TranscriptionTerminalStatus {
	readonly status: string;
	readonly errorCode: string | null;
}

export interface TranscriptionHubHandle {
	/**
	 * True when the transport is connected AND the subscription snapshot has resolved AND every `ReplayTruncated`
	 * page has been drained — i.e. when what the view renders is complete. Transport-only connectedness is not a
	 * separate state in this hook.
	 */
	readonly connected: boolean;
	/** The same condition under the name that says what it means, so a caller need not infer it. */
	readonly subscriptionReady: boolean;
	/** Non-null once the replay drain gave up; cleared by the next subscribe that completes. */
	readonly replayStalled: TranscriptionReplayStalled | null;
	/** Non-null once the node refused the subscription; cleared by the next subscribe that completes. */
	readonly subscribeFailed: TranscriptionSubscribeFailed | null;
	/**
	 * True once the node announced it stopped accepting audio for this session (`transcriptionAdmissionClosed`): a
	 * graceful end is draining, so a frame sent now would be discarded. Cleared by every subscribe.
	 */
	readonly admissionClosed: boolean;
	/**
	 * Non-null once the node pushed this session's terminal status live. Unlike `admissionClosed` it is not cleared by a
	 * re-subscribe: a session that ended stays ended.
	 */
	readonly terminal: TranscriptionTerminalStatus | null;
	/**
	 * Rejects when the send fails, the transport is down, or an invoke stays open past the stall timeout; never drops a
	 * frame. There is no in-flight limit: SignalR runs one connection's invocations in order and the node returns as
	 * soon as the frame is queued, buffering while it is behind.
	 */
	pushFrame(channel: CaptureChannel, pcm: Int16Array): Promise<void>;
	/**
	 * Ends the live session over the hub. Resolves **true** once `EndSession` was invoked, **false** when there was no
	 * connected hub to invoke it on — the caller has to finish the job some other way, because a silent false here is
	 * a node that goes on recording. Rejects only when the invoke itself failed.
	 */
	endSession(): Promise<boolean>;
}

/** Cache key for one session's live transcript. Push-fed only — it has no endpoint behind it. */
export function liveTranscriptKey(sessionId: string): readonly [string, string] {
	return ["liveTranscript", sessionId];
}

/**
 * Reads the live transcript for one session, or `null` before the hook has written anything. Push-fed: the query has
 * no fetcher, so it never goes to the network and never overwrites what the hub wrote.
 */
export function useLiveTranscript(sessionId: string): LiveTranscriptView | null {
	const query = useQuery({
		queryKey: liveTranscriptKey(sessionId),
		queryFn: (): LiveTranscriptView | null => null,
		staleTime: Number.POSITIVE_INFINITY,
	});
	return query.data ?? null;
}

// The hub fans a client method out to every handler on the shared connection, so a second session subscribed from
// another mount would otherwise land in this one's cache. Guids are compared case-insensitively: the route parameter
// and the node's own formatting need not agree on case.
function isSameSession(left: string, right: string): boolean {
	return left.toLowerCase() === right.toLowerCase();
}

/** The frame goes over the JSON protocol, whose `byte[]` binding is base64 — a `Uint8Array` would stringify as an object. */
function toBase64(pcm: Int16Array): string {
	const bytes = new Uint8Array(pcm.buffer, pcm.byteOffset, pcm.byteLength);
	let binary = "";
	for (let index = 0; index < bytes.length; index += 1) {
		binary += String.fromCharCode(bytes[index] ?? 0);
	}
	return btoa(binary);
}

export function useTranscriptionHub(sessionId: string | null): TranscriptionHubHandle {
	const queryClient = useQueryClient();
	const [connected, setConnected] = useState(false);
	const [replayStalled, setReplayStalled] = useState<TranscriptionReplayStalled | null>(null);
	const [subscribeFailed, setSubscribeFailed] = useState<TranscriptionSubscribeFailed | null>(null);
	const [admissionClosed, setAdmissionClosed] = useState(false);
	const [terminal, setTerminal] = useState<TranscriptionTerminalStatus | null>(null);
	// The live connection, read by pushFrame/endSession without re-creating them on every reconnect.
	const connectionRef = useRef<ReturnType<typeof acquireHubConnection>["connection"] | null>(null);

	useEffect(() => {
		if (sessionId === null) {
			setConnected(false);
			setReplayStalled(null);
			setSubscribeFailed(null);
			setAdmissionClosed(false);
			setTerminal(null);
			return;
		}

		const hub = acquireHubConnection(HUB_PATH);
		const { connection } = hub;
		connectionRef.current = connection;

		let disposed = false;
		// The subscription gate, reset at the top of every (re)subscribe: while it is false a live push is parked in
		// `buffered` rather than applied, so a push that overtakes the snapshot cannot advance the cursor past rows the
		// snapshot is still carrying.
		let snapshotResolved = false;
		let buffered: TranscriptionSegmentPush[] = [];
		// Set when the replay drain gives up. The published cursor is then frozen at the last whole watermark, so a
		// push arriving behind it can never be rendered in order — buffering it would grow without bound for the
		// life of the session while nothing ever released it. Cleared by the next subscribe.
		let stalled = false;
		// Committed rows keyed by sequence. Inserting an existing key is a no-op, which is the whole dedupe rule: the
		// client never depends on the node's ordering for correctness, so a reordered or repeated push is harmless and
		// an out-of-order LOWER sequence is still rendered.
		const committed = new Map<number, CommittedSegment>();
		const partials: Partial<Record<CaptureChannel, string>> = {};
		// The published watermark. It only ever names a cursor every row up to which has been delivered.
		let lastSeq = 0;
		let status = "";
		let bufferedMs: number | null = null;
		let replayTruncated = false;
		// A terminal status that arrived live must not be overwritten by a snapshot still reporting `Transcribing`.
		let liveStatusSeen = false;

		const render = (): void => {
			queryClient.setQueryData(liveTranscriptKey(sessionId), {
				committed: [...committed.values()].sort((left, right) => left.startMs - right.startMs || left.seq - right.seq),
				partials: { ...partials },
				status,
				bufferedMs,
				lastSeq,
				replayTruncated,
			} satisfies LiveTranscriptView);
		};

		const insert = (row: CommittedSegment): void => {
			if (!committed.has(row.seq)) {
				committed.set(row.seq, row);
			}
		};

		// The one apply the live path and the buffered replay share.
		const apply = (push: TranscriptionSegmentPush): void => {
			insert({
				seq: push.seq,
				startMs: push.startMs,
				endMs: push.endMs,
				text: push.text,
				channel: fromWireChannel(push.channel),
				confidence: push.confidence ?? null,
			});
			lastSeq = Math.max(lastSeq, push.seq);
		};

		const onSegment = (payload: unknown): void => {
			const parsed = transcriptionSegmentPushSchema.safeParse(payload);
			if (!parsed.success || !isSameSession(parsed.data.sessionId, sessionId) || stalled) {
				return;
			}
			if (!snapshotResolved) {
				buffered.push(parsed.data);
				return;
			}
			apply(parsed.data);
			render();
		};

		const onPartial = (payload: unknown): void => {
			const parsed = transcriptionPartialPushSchema.safeParse(payload);
			if (!parsed.success || !isSameSession(parsed.data.sessionId, sessionId)) {
				return;
			}
			partials[fromWireChannel(parsed.data.channel)] = parsed.data.text;
			render();
		};

		const onCatchUpProgress = (payload: unknown): void => {
			const parsed = transcriptionCatchUpProgressPushSchema.safeParse(payload);
			if (!parsed.success || !isSameSession(parsed.data.sessionId, sessionId)) {
				return;
			}
			bufferedMs = parsed.data.bufferedMs;
			render();
		};

		const onAdmissionClosed = (payload: unknown): void => {
			const parsed = transcriptionAdmissionClosedPushSchema.safeParse(payload);
			if (parsed.success && isSameSession(parsed.data.sessionId, sessionId)) {
				setAdmissionClosed(true);
			}
		};

		const onStatus = (payload: unknown): void => {
			const parsed = transcriptionStatusPushSchema.safeParse(payload);
			if (!parsed.success || !isSameSession(parsed.data.sessionId, sessionId)) {
				return;
			}
			liveStatusSeen = true;
			status = parsed.data.status;
			setTerminal({ status: parsed.data.status, errorCode: parsed.data.errorCode ?? null });
			render();
		};

		/**
		 * Subscribe and drain. There is no page cap: a cap leaves a long session permanently half-replayed, and the
		 * buffered newer pushes then advance the reconnect cursor past rows the client never saw. The runaway guard is
		 * a LIVENESS check instead — a page that does not move the cursor fails typed.
		 */
		const subscribe = async (): Promise<void> => {
			if (disposed || connection.state !== HubConnectionState.Connected) {
				return;
			}
			snapshotResolved = false;
			buffered = [];
			stalled = false;
			setConnected(false);
			setAdmissionClosed(false);
			// The drain's own cursor. The published `lastSeq` is only moved once the LAST page has landed.
			let cursor = lastSeq;
			try {
				for (;;) {
					// The drain is sequential by definition: each page's `afterSeq` is the previous page's `lastSeq`, and
					// the node decides when there is no next page. There is nothing here to parallelise.
					// biome-ignore lint/performance/noAwaitInLoops: Promise.all cannot express a cursor-chained drain.
					const raw: unknown = await connection.invoke("SubscribeSession", sessionId, cursor);
					if (disposed) {
						return;
					}
					const parsed = transcriptionSnapshotSchema.safeParse(raw);
					if (!parsed.success) {
						throw new Error("transcription subscription snapshot did not match the hub contract");
					}
					const snapshot = parsed.data;
					for (const segment of snapshot.segments) {
						insert({
							seq: segment.seq,
							startMs: segment.startMs,
							endMs: segment.endMs,
							text: segment.text,
							channel: fromWireChannel(segment.channel),
							confidence: segment.confidence ?? null,
						});
					}
					if (!liveStatusSeen) {
						status = snapshot.status;
					}
					replayTruncated = snapshot.replayTruncated;
					if (!snapshot.replayTruncated) {
						cursor = Math.max(cursor, snapshot.lastSeq);
						break;
					}
					if (snapshot.lastSeq <= cursor) {
						setReplayStalled({ code: "transcription-replay-stalled", lastDrainedSeq: lastSeq });
						// `lastSeq` is deliberately left where it was: the pages merged so far are rendered, but the
						// watermark a reconnect would resume from stays the last one that was drained in full. What
						// was buffered behind the frozen cursor is dropped rather than held for ever — the alert's
						// own advice is to reload, which is the only thing that can complete this transcript.
						buffered = [];
						stalled = true;
						render();
						return;
					}
					cursor = snapshot.lastSeq;
				}
				lastSeq = Math.max(lastSeq, cursor);
				snapshotResolved = true;
				// One gate, one code path: a buffered push goes through the same apply the live path uses.
				for (const push of buffered.toSorted((left, right) => left.seq - right.seq)) {
					apply(push);
				}
				buffered = [];
				setReplayStalled(null);
				setSubscribeFailed(null);
				setConnected(true);
				render();
			} catch (error: unknown) {
				if (!disposed) {
					console.warn("transcription hub failed to subscribe to session", sessionId, error);
					// Named, not just logged: nothing retries an initial subscribe, so this is the only thing that
					// tells the operator why the transcript is empty and why Start is not on offer.
					setSubscribeFailed({ code: toSubscribeFailureCode(error) });
					setConnected(false);
				}
			}
		};

		connection.on(TRANSCRIPTION_SEGMENT_COMMITTED, onSegment);
		connection.on(TRANSCRIPTION_PARTIAL_UPDATED, onPartial);
		connection.on(TRANSCRIPTION_SESSION_STATUS_CHANGED, onStatus);
		connection.on(TRANSCRIPTION_CATCH_UP_PROGRESS, onCatchUpProgress);
		connection.on(TRANSCRIPTION_ADMISSION_CLOSED, onAdmissionClosed);

		// A reconnect is a subscription like any other: it re-subscribes from the watermark it has, never from 0, and
		// goes through the same buffer-then-merge path.
		const removeReconnected = hub.onReconnected(() => {
			subscribe().catch(() => undefined);
		});
		// While the transport is down nothing renders as complete, so `connected` must not keep saying it is.
		const removeReconnecting = hub.onReconnecting(() => setConnected(false));
		const removeClosed = hub.onClosed(() => setConnected(false));
		hub.whenStarted.then(() => subscribe()).catch(() => undefined);

		return () => {
			disposed = true;
			connection.off(TRANSCRIPTION_SEGMENT_COMMITTED, onSegment);
			connection.off(TRANSCRIPTION_PARTIAL_UPDATED, onPartial);
			connection.off(TRANSCRIPTION_SESSION_STATUS_CHANGED, onStatus);
			connection.off(TRANSCRIPTION_CATCH_UP_PROGRESS, onCatchUpProgress);
			connection.off(TRANSCRIPTION_ADMISSION_CLOSED, onAdmissionClosed);
			removeReconnected();
			removeReconnecting();
			removeClosed();
			if (connection.state === HubConnectionState.Connected) {
				// An explicit unsubscribe means what a disconnect means, and it arms the node's abandonment grace.
				connection.invoke("UnsubscribeSession", sessionId).catch(() => undefined);
			}
			connectionRef.current = null;
			hub.release();
		};
	}, [queryClient, sessionId]);

	const pushFrame = useCallback(
		async (channel: CaptureChannel, pcm: Int16Array): Promise<void> => {
			const connection = connectionRef.current;
			if (sessionId === null || connection === null || connection.state !== HubConnectionState.Connected) {
				// Not a drop: speech the node never received is a hole in the transcript, and a hole nobody is told about
				// is worse than a stopped capture.
				throw new CaptureError("disconnected", "the transcription hub is not connected");
			}
			let timer: ReturnType<typeof setTimeout> | undefined;
			const stalled = new Promise<never>((_, reject) => {
				timer = setTimeout(
					() => reject(new CaptureError("disconnected", "the transcription hub stopped completing frames")),
					PUSH_FRAME_TIMEOUT_MS,
				);
			});
			try {
				await Promise.race([connection.invoke("PushAudioFrame", sessionId, channelToWire(channel), toBase64(pcm)), stalled]);
			} finally {
				clearTimeout(timer);
			}
		},
		[sessionId],
	);

	// Never waits behind an outstanding frame: the caller stops its sources first. The node returns from
	// `PushAudioFrame` as soon as the frame is queued, so a frame still pending here is a slow transport, not inference.
	const endSession = useCallback(async (): Promise<boolean> => {
		const connection = connectionRef.current;
		if (sessionId === null || connection === null || connection.state !== HubConnectionState.Connected) {
			// Reported, not swallowed. Returning void here let a Stop pressed while the transport was down end nothing
			// at all: the UI went idle, the reconnect re-subscribed, and the node kept the session alive.
			return false;
		}
		await connection.invoke("EndSession", sessionId);
		return true;
	}, [sessionId]);

	return {
		connected,
		subscriptionReady: connected,
		replayStalled,
		subscribeFailed,
		admissionClosed,
		terminal,
		pushFrame,
		endSession,
	};
}
