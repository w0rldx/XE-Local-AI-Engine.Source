import { HubConnectionState } from "@microsoft/signalr";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useCallback, useEffect, useRef, useState } from "react";

import { acquireHubConnection } from "@/core/api/signalr/SharedHubConnection";
import { type CaptureChannel, CaptureError } from "@/features/transcription/capture/CaptureSource";
import {
	TRANSCRIPTION_PARTIAL_UPDATED,
	TRANSCRIPTION_SEGMENT_COMMITTED,
	TRANSCRIPTION_SESSION_STATUS_CHANGED,
	type TranscriptionSegmentPush,
	channelToWire,
	fromWireChannel,
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

/**
 * At most two `PushAudioFrame` invocations awaited at once. `connection.invoke` resolves when the server METHOD
 * completes, and the node holds its lane semaphore across inference, so unbounded sends against a transcriber slower
 * than real time pile up in the client and as pending audio in the node. Two covers one 250 ms frame being processed
 * while the next is on the wire.
 */
const MAX_FRAMES_IN_FLIGHT = 2;

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
	/** Rejects when the send fails, at the in-flight limit, or while the transport is down. Never drops a frame. */
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
	// The live connection, read by pushFrame/endSession without re-creating them on every reconnect.
	const connectionRef = useRef<ReturnType<typeof acquireHubConnection>["connection"] | null>(null);
	const inFlightRef = useRef(0);

	useEffect(() => {
		if (sessionId === null) {
			setConnected(false);
			setReplayStalled(null);
			setSubscribeFailed(null);
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
		let replayTruncated = false;
		// A terminal status that arrived live must not be overwritten by a snapshot still reporting `Transcribing`.
		let liveStatusSeen = false;

		const render = (): void => {
			queryClient.setQueryData(liveTranscriptKey(sessionId), {
				committed: [...committed.values()].sort((left, right) => left.startMs - right.startMs || left.seq - right.seq),
				partials: { ...partials },
				status,
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

		const onStatus = (payload: unknown): void => {
			const parsed = transcriptionStatusPushSchema.safeParse(payload);
			if (!parsed.success || !isSameSession(parsed.data.sessionId, sessionId)) {
				return;
			}
			liveStatusSeen = true;
			status = parsed.data.status;
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
				// is worse than a stopped capture. `disconnected` rather than `overloaded`: both stop capture, but a dead
				// transport told as "this node could not keep up" sends the operator after a performance problem it
				// does not have.
				throw new CaptureError("disconnected", "the transcription hub is not connected");
			}
			if (inFlightRef.current >= MAX_FRAMES_IN_FLIGHT) {
				throw new CaptureError("overloaded", "the transcription hub already has the maximum frames in flight");
			}
			inFlightRef.current += 1;
			try {
				await connection.invoke("PushAudioFrame", sessionId, channelToWire(channel), toBase64(pcm));
			} finally {
				inFlightRef.current -= 1;
			}
		},
		[sessionId],
	);

	// Never waits behind an outstanding frame: the caller stops its sources first, and a pending `PushAudioFrame` may
	// still be inside a 30 s inference when the operator presses stop.
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

	return { connected, subscriptionReady: connected, replayStalled, subscribeFailed, pushFrame, endSession };
}
