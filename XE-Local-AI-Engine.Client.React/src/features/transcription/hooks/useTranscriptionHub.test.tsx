// @vitest-environment jsdom

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { resetSharedHubConnectionsForTest } from "@/core/api/signalr/SharedHubConnection";
import { CaptureError } from "@/features/transcription/capture/CaptureSource";
import { liveTranscriptKey, useLiveTranscript, useTranscriptionHub } from "@/features/transcription/hooks/useTranscriptionHub";
import {
	TRANSCRIPTION_ADMISSION_CLOSED,
	TRANSCRIPTION_CATCH_UP_PROGRESS,
	TRANSCRIPTION_PARTIAL_UPDATED,
	TRANSCRIPTION_SEGMENT_COMMITTED,
	TRANSCRIPTION_SESSION_STATUS_CHANGED,
} from "@/features/transcription/models/TranscriptionLiveModels";

const SESSION_ID = "11111111-1111-1111-1111-111111111111";

// Captured event handlers registered via connection.on, so a test can drive a server push by name.
const registeredHandlers = new Map<string, (payload: unknown) => void>();
const stopSpy = vi.fn(() => Promise.resolve());
const startSpy = vi.fn(() => Promise.resolve());
let connectionState = "Disconnected";
// SharedHubConnection registers exactly one fan-out callback per lifecycle event; holding it lets a test drive a
// transport reconnect without a real socket.
let reconnectedFanout: (() => void) | undefined;

// What `SubscribeSession` answers, scripted per test: it receives the `afterSeq` the hook asked for and the 0-based
// index of this subscribe, which is what a multi-page replay drain is driven from.
let subscribeHandler: (afterSeq: number, callIndex: number) => Promise<unknown> = () => Promise.resolve(snapshot({}));
let subscribeCallCount = 0;
// What `PushAudioFrame` answers. A never-settling promise models a frame parked behind the node's inference.
let pushHandler: () => Promise<unknown> = () => Promise.resolve(undefined);

const invokeSpy = vi.fn((name: string, ...args: unknown[]): Promise<unknown> => {
	if (name === "SubscribeSession") {
		const callIndex = subscribeCallCount;
		subscribeCallCount += 1;
		return subscribeHandler(args[1] as number, callIndex);
	}
	if (name === "PushAudioFrame") {
		return pushHandler();
	}
	return Promise.resolve(undefined);
});

// Mock the SignalR client exactly as useImageJobHub.test.tsx does, plus a captured `onreconnected` and an `invoke`
// that answers `SubscribeSession` with a scripted snapshot so the hook's seeding and drain paths are exercised.
vi.mock("@microsoft/signalr", () => {
	class FakeBuilder {
		withUrl() {
			return this;
		}
		withAutomaticReconnect() {
			return this;
		}
		configureLogging() {
			return this;
		}
		build() {
			return {
				get state() {
					return connectionState;
				},
				on: (name: string, handler: (payload: unknown) => void) => registeredHandlers.set(name, handler),
				off: (name: string) => registeredHandlers.delete(name),
				onreconnected: (callback: () => void) => {
					reconnectedFanout = callback;
				},
				onreconnecting: () => undefined,
				onclose: () => undefined,
				start: () => {
					connectionState = "Connected";
					return startSpy();
				},
				stop: stopSpy,
				invoke: invokeSpy,
			};
		}
	}
	return {
		HubConnectionBuilder: FakeBuilder,
		HubConnectionState: { Connected: "Connected", Disconnected: "Disconnected" },
		LogLevel: { Warning: 3 },
	};
});

vi.mock("@/core/auth/stores/NodeAuthStore", () => ({
	useNodeAuthStore: { getState: () => ({ accessToken: "token" }) },
}));

interface SnapshotRow {
	seq: number;
	startMs: number;
	endMs: number;
	text: string;
	channel: string;
	confidence: number | null;
}

function row(seq: number, over: Partial<SnapshotRow> = {}): SnapshotRow {
	return { seq, startMs: seq * 1000, endMs: seq * 1000 + 500, text: `row ${seq}`, channel: "Mono", confidence: null, ...over };
}

function snapshot(over: Partial<{ status: string; lastSeq: number; segments: SnapshotRow[]; replayTruncated: boolean }>) {
	return {
		sessionId: SESSION_ID,
		status: "Transcribing",
		lastSeq: 0,
		segments: [] as SnapshotRow[],
		replayTruncated: false,
		...over,
	};
}

function segmentPush(seq: number, over: Partial<SnapshotRow> = {}): Record<string, unknown> {
	return { sessionId: SESSION_ID, ...row(seq, over) };
}

interface Deferred<T> {
	promise: Promise<T>;
	resolve: (value: T) => void;
}

function deferred<T>(): Deferred<T> {
	let resolve: ((value: T) => void) | undefined;
	const promise = new Promise<T>((innerResolve) => {
		resolve = innerResolve;
	});
	return { promise, resolve: (value: T) => resolve?.(value) };
}

function fire(eventName: string, payload: Record<string, unknown>): void {
	act(() => {
		registeredHandlers.get(eventName)?.(payload);
	});
}

function pushFrameInvokes(): unknown[][] {
	return invokeSpy.mock.calls.filter((call) => call[0] === "PushAudioFrame");
}

function subscribeInvokes(): unknown[][] {
	return invokeSpy.mock.calls.filter((call) => call[0] === "SubscribeSession");
}

function renderHub() {
	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
	function Wrapper({ children }: { children: ReactNode }) {
		return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
	}
	return {
		...renderHook(() => ({ hub: useTranscriptionHub(SESSION_ID), view: useLiveTranscript(SESSION_ID) }), {
			wrapper: Wrapper,
		}),
		// Read straight from the cache where a test must assert that NOTHING was written: the push-fed query resolves
		// its own `null` fetcher one microtask after mount, which would then mask an early write behind that null.
		written: () => queryClient.getQueryData(liveTranscriptKey(SESSION_ID)),
	};
}

describe("useTranscriptionHub", () => {
	beforeEach(() => {
		resetSharedHubConnectionsForTest();
		registeredHandlers.clear();
		startSpy.mockClear();
		stopSpy.mockClear();
		invokeSpy.mockClear();
		connectionState = "Disconnected";
		reconnectedFanout = undefined;
		subscribeCallCount = 0;
		subscribeHandler = () => Promise.resolve(snapshot({}));
		pushHandler = () => Promise.resolve(undefined);
	});

	afterEach(() => {
		vi.clearAllMocks();
	});

	// Provisional text is REPLACED, never accumulated: the lane re-transcribes its whole open window on every tick,
	// so appending would repeat the same words once per tick.
	it("PartialUpdated_ReplacesThePreviousPartialForThatChannel", async () => {
		const { result } = renderHub();
		await waitFor(() => expect(result.current.hub.connected).toBe(true));

		fire(TRANSCRIPTION_PARTIAL_UPDATED, { sessionId: SESSION_ID, channel: "You", text: "my fellow" });
		fire(TRANSCRIPTION_PARTIAL_UPDATED, { sessionId: SESSION_ID, channel: "You", text: "my fellow americans" });
		fire(TRANSCRIPTION_PARTIAL_UPDATED, { sessionId: SESSION_ID, channel: "Others", text: "ask not" });

		await waitFor(() => expect(result.current.view?.partials.you).toBe("my fellow americans"));
		expect(result.current.view?.partials.others).toBe("ask not");
	});

	it("SegmentCommitted_WithARepeatedSeq_IsIgnored", async () => {
		const { result } = renderHub();
		await waitFor(() => expect(result.current.hub.connected).toBe(true));

		fire(TRANSCRIPTION_SEGMENT_COMMITTED, segmentPush(1, { text: "once" }));
		fire(TRANSCRIPTION_SEGMENT_COMMITTED, segmentPush(1, { text: "once" }));

		await waitFor(() => expect(result.current.view?.committed).toHaveLength(1));
		expect(result.current.view?.committed[0]?.text).toBe("once");
	});

	// The sequence is publication order across two lanes, which is not speech order: a row committed later can have
	// started earlier. The rendered order is `(startMs, seq)`.
	it("SegmentCommitted_AppendsInStartTimeOrderAcrossChannels", async () => {
		const { result } = renderHub();
		await waitFor(() => expect(result.current.hub.connected).toBe(true));

		fire(TRANSCRIPTION_SEGMENT_COMMITTED, segmentPush(1, { startMs: 2_000, channel: "You", text: "second" }));
		fire(TRANSCRIPTION_SEGMENT_COMMITTED, segmentPush(2, { startMs: 1_000, channel: "Others", text: "first" }));

		await waitFor(() => expect(result.current.view?.committed).toHaveLength(2));
		expect(result.current.view?.committed.map((segment) => segment.text)).toEqual(["first", "second"]);
		expect(result.current.view?.committed.map((segment) => segment.channel)).toEqual(["others", "you"]);
	});

	// The JSON hub protocol binds a `byte[]` from a base64 STRING; a Uint8Array would stringify as {"0":12,…}.
	it("PushFrame_SendsBase64WithTheWireChannelNumber", async () => {
		const { result } = renderHub();
		await waitFor(() => expect(result.current.hub.connected).toBe(true));

		await result.current.hub.pushFrame("others", Int16Array.of(1, -2));

		const call = pushFrameInvokes()[0];
		expect(call?.[1]).toBe(SESSION_ID);
		expect(call?.[2]).toBe(2);
		const decoded = [...atob(call?.[3] as string)].map((character) => character.charCodeAt(0));
		expect(decoded).toEqual([1, 0, 254, 255]);
	});

	// A dead transport is the one send failure: the frame never left, so capture stops rather than leaving a hole.
	it("PushFrame_WhileDisconnected_DoesNotInvokeAndReportsDisconnected", async () => {
		const { result } = renderHub();
		await waitFor(() => expect(result.current.hub.connected).toBe(true));
		invokeSpy.mockClear();
		connectionState = "Disconnected";

		const rejection = await result.current.hub.pushFrame("mono", Int16Array.of(7)).catch((error: unknown) => error);

		expect(rejection).toBeInstanceOf(CaptureError);
		expect(rejection).toMatchObject({ code: "disconnected" });
		expect(pushFrameInvokes()).toHaveLength(0);
	});

	// The caller has to know whether the node was actually told. A void return let a Stop pressed while the transport
	// was down end nothing at all, silently, and the node kept the session — and its recorder — alive.
	it("EndSession_WhenConnected_InvokesAndReportsThatItWasDelivered", async () => {
		const { result } = renderHub();
		await waitFor(() => expect(result.current.hub.connected).toBe(true));
		invokeSpy.mockClear();

		const delivered = await result.current.hub.endSession();

		expect(delivered).toBe(true);
		expect(invokeSpy.mock.calls.filter((call) => call[0] === "EndSession")).toHaveLength(1);
	});

	it("EndSession_WhileDisconnected_InvokesNothingAndReportsThatItWasNot", async () => {
		const { result } = renderHub();
		await waitFor(() => expect(result.current.hub.connected).toBe(true));
		invokeSpy.mockClear();
		connectionState = "Disconnected";

		const delivered = await result.current.hub.endSession();

		expect(delivered).toBe(false);
		expect(invokeSpy.mock.calls.filter((call) => call[0] === "EndSession")).toHaveLength(0);
	});

	// Resuming from 0 would replay the whole session and, worse, re-seed rows the view already holds while the node
	// pays for the read. The watermark is what the hook already has.
	it("OnReconnected_ResubscribesFromTheLastSeqNotZero", async () => {
		subscribeHandler = () => Promise.resolve(snapshot({ lastSeq: 7, segments: [row(7)] }));
		const { result } = renderHub();
		await waitFor(() => expect(result.current.hub.connected).toBe(true));
		expect(subscribeInvokes()[0]?.[2]).toBe(0);

		act(() => reconnectedFanout?.());

		await waitFor(() => expect(subscribeInvokes()).toHaveLength(2));
		expect(subscribeInvokes()[1]?.[2]).toBe(7);
	});

	// R32: `SubscribeSession` is awaited, so a live push can arrive before its snapshot lands. Applying it there would
	// advance the cursor past rows the snapshot is still carrying — the client's half of the join-before-replay race.
	it("PushDuringSnapshotLoading_IsBufferedAndMergedBySeq", async () => {
		const gate = deferred<unknown>();
		subscribeHandler = () => gate.promise;
		const { result, written } = renderHub();
		await waitFor(() => expect(subscribeInvokes()).toHaveLength(1));

		fire(TRANSCRIPTION_SEGMENT_COMMITTED, segmentPush(2, { text: "live" }));
		// The gate's own proof: an applied push would have written the cache here, ahead of the rows the snapshot is
		// still carrying.
		expect(written()).toBeNull();

		await act(async () => {
			gate.resolve(snapshot({ lastSeq: 1, segments: [row(1, { text: "replayed" })] }));
			await gate.promise;
		});

		await waitFor(() => expect(result.current.view?.committed).toHaveLength(2));
		expect(result.current.view?.committed.map((segment) => segment.text)).toEqual(["replayed", "live"]);
		expect(result.current.view?.lastSeq).toBe(2);
	});

	// R32: merging is by exact sequence identity. The old `<= lastSeq` drop discarded a row for good whenever one
	// lane's persistence stalled behind the other's.
	it("AnOutOfOrderLowerSeq_IsStillRendered", async () => {
		subscribeHandler = () => Promise.resolve(snapshot({ lastSeq: 5, segments: [row(5)] }));
		const { result } = renderHub();
		await waitFor(() => expect(result.current.hub.connected).toBe(true));

		fire(TRANSCRIPTION_SEGMENT_COMMITTED, segmentPush(3, { text: "late but earlier" }));

		await waitFor(() => expect(result.current.view?.committed).toHaveLength(2));
		expect(result.current.view?.committed.map((segment) => segment.seq)).toEqual([3, 5]);
	});

	it("ReplayTruncated_DrainsEveryPageBeforeRendering", async () => {
		const secondPage = deferred<unknown>();
		subscribeHandler = (afterSeq, callIndex) => {
			if (callIndex === 0) {
				expect(afterSeq).toBe(0);
				return Promise.resolve(snapshot({ lastSeq: 2, segments: [row(1), row(2)], replayTruncated: true }));
			}
			expect(afterSeq).toBe(2);
			return secondPage.promise;
		};
		const { result } = renderHub();

		await waitFor(() => expect(subscribeInvokes()).toHaveLength(2));
		expect(result.current.hub.connected).toBe(false);
		expect(result.current.view).toBeNull();

		await act(async () => {
			secondPage.resolve(snapshot({ lastSeq: 3, segments: [row(3)] }));
			await secondPage.promise;
		});

		await waitFor(() => expect(result.current.hub.connected).toBe(true));
		expect(result.current.view?.committed.map((segment) => segment.seq)).toEqual([1, 2, 3]);
		expect(result.current.view?.lastSeq).toBe(3);
		expect(result.current.view?.replayTruncated).toBe(false);
	});

	// B3: a node that is behind buffers and catches up, so the client has no in-flight limit. Frames sent while
	// earlier ones are still outstanding are all invoked, none rejected — a slow round-trip is not a failure.
	it("PushFrame_WithManyFramesOutstanding_InvokesEveryOneAndRejectsNone", async () => {
		// Every send is held open, then settled at the end: sends left pending forever keep the test from finishing.
		const settle: Array<() => void> = [];
		pushHandler = () =>
			new Promise<unknown>((resolve) => {
				settle.push(() => resolve(undefined));
			});
		const { result } = renderHub();
		await waitFor(() => expect(result.current.hub.connected).toBe(true));

		const held = Array.from({ length: 20 }, (_, index) => result.current.hub.pushFrame("mono", Int16Array.of(index)));

		await waitFor(() => expect(settle).toHaveLength(20));
		expect(pushFrameInvokes()).toHaveLength(20);
		for (const resolve of settle) {
			resolve();
		}
		await expect(Promise.all(held)).resolves.toHaveLength(20);
	});

	// B2: the node reports its backlog so the capture controls can say how far behind it is. Null until the first
	// report, so "nothing reported" and "caught up" stay distinguishable; another session's report is ignored.
	it("CatchUpProgress_SurfacesBufferedMsForThisSessionOnly", async () => {
		const { result } = renderHub();
		await waitFor(() => expect(result.current.hub.connected).toBe(true));
		expect(result.current.view?.bufferedMs).toBeNull();

		fire(TRANSCRIPTION_CATCH_UP_PROGRESS, { sessionId: SESSION_ID.toUpperCase(), bufferedMs: 4200 });
		await waitFor(() => expect(result.current.view?.bufferedMs).toBe(4200));

		fire(TRANSCRIPTION_CATCH_UP_PROGRESS, { sessionId: "22222222-2222-2222-2222-222222222222", bufferedMs: 9000 });
		fire(TRANSCRIPTION_CATCH_UP_PROGRESS, { sessionId: SESSION_ID, bufferedMs: "lots" });
		expect(result.current.view?.bufferedMs).toBe(4200);

		fire(TRANSCRIPTION_CATCH_UP_PROGRESS, { sessionId: SESSION_ID, bufferedMs: 0 });
		await waitFor(() => expect(result.current.view?.bufferedMs).toBe(0));
	});

	// Codex r2 #10: the node announces it stopped accepting audio so the capture can stop instead of recording into a
	// drain that discards every frame. Only this session's well-formed push counts; a resubscribe clears it.
	it("AdmissionClosed_IsSetForThisSessionOnlyAndClearedByAResubscribe", async () => {
		const { result } = renderHub();
		await waitFor(() => expect(result.current.hub.connected).toBe(true));
		expect(result.current.hub.admissionClosed).toBe(false);

		fire(TRANSCRIPTION_ADMISSION_CLOSED, { sessionId: "22222222-2222-2222-2222-222222222222" });
		fire(TRANSCRIPTION_ADMISSION_CLOSED, { sessionId: 42 });
		expect(result.current.hub.admissionClosed).toBe(false);

		fire(TRANSCRIPTION_ADMISSION_CLOSED, { sessionId: SESSION_ID.toUpperCase() });
		expect(result.current.hub.admissionClosed).toBe(true);

		act(() => reconnectedFanout?.());
		await waitFor(() => expect(result.current.hub.admissionClosed).toBe(false));
	});

	// The capture hook stops on this. It carries the error code the wire status folds away (NeverAttached reads
	// `Abandoned`), only this session's push counts, and a resubscribe does not clear it: an ended session stays ended.
	it("StatusChanged_SurfacesTheTerminalWithItsErrorCodeForThisSessionOnly", async () => {
		const { result } = renderHub();
		await waitFor(() => expect(result.current.hub.connected).toBe(true));
		expect(result.current.hub.terminal).toBeNull();

		fire(TRANSCRIPTION_SESSION_STATUS_CHANGED, { sessionId: "22222222-2222-2222-2222-222222222222", status: "Failed" });
		expect(result.current.hub.terminal).toBeNull();

		fire(TRANSCRIPTION_SESSION_STATUS_CHANGED, { sessionId: SESSION_ID, status: "Abandoned", errorCode: "live-never-attached" });
		expect(result.current.hub.terminal).toEqual({ status: "Abandoned", errorCode: "live-never-attached" });

		act(() => reconnectedFanout?.());
		await waitFor(() => expect(result.current.hub.connected).toBe(true));
		expect(result.current.hub.terminal).toEqual({ status: "Abandoned", errorCode: "live-never-attached" });
	});

	// R32a: no page cap. An earlier draft stopped at 20, which leaves a session past 10 000 rows permanently
	// half-replayed, and the buffered newer pushes then move the reconnect cursor past rows nobody ever saw.
	it("ReplayTruncated_DrainsMoreThanTwentyPagesWhileANewerPushArrives", async () => {
		const pageCount = 25;
		subscribeHandler = (_afterSeq, callIndex) => {
			const seq = callIndex + 1;
			if (callIndex === 11) {
				fire(TRANSCRIPTION_SEGMENT_COMMITTED, segmentPush(1_000, { text: "live" }));
			}
			return Promise.resolve(snapshot({ lastSeq: seq, segments: [row(seq)], replayTruncated: callIndex < pageCount - 1 }));
		};
		const { result } = renderHub();

		await waitFor(() => expect(result.current.hub.connected).toBe(true));
		expect(subscribeInvokes()).toHaveLength(pageCount);
		const seqs = result.current.view?.committed.map((segment) => segment.seq) ?? [];
		expect(seqs).toHaveLength(pageCount + 1);
		expect(new Set(seqs).size).toBe(pageCount + 1);
		expect(seqs.at(-1)).toBe(1_000);
		expect(result.current.view?.lastSeq).toBe(1_000);
	});

	// The runaway guard is liveness, not a budget: a page claiming more rows exist while reporting a cursor that has
	// not moved would loop forever. Failing here keeps the last cursor that WAS drained in full, so a later reconnect
	// resumes from a watermark every row of which was actually delivered.
	it("WhenAReplayPageDoesNotAdvanceTheCursor_FailsTypedAndPreservesTheLastDrainedCursor", async () => {
		subscribeHandler = (_afterSeq, callIndex) =>
			callIndex === 0
				? Promise.resolve(snapshot({ lastSeq: 5, segments: [row(5)] }))
				: Promise.resolve(snapshot({ lastSeq: 9, segments: [row(9)], replayTruncated: true }));
		const { result } = renderHub();
		await waitFor(() => expect(result.current.hub.connected).toBe(true));
		expect(result.current.view?.lastSeq).toBe(5);

		act(() => reconnectedFanout?.());

		await waitFor(() => expect(result.current.hub.replayStalled).not.toBeNull());
		expect(result.current.hub.replayStalled).toEqual({ code: "transcription-replay-stalled", lastDrainedSeq: 5 });
		expect(result.current.hub.connected).toBe(false);
		expect(result.current.view?.lastSeq).toBe(5);

		// The cursor is frozen, so a push behind it can never be rendered in order. It is dropped rather than
		// buffered: a buffer nothing releases grows for the life of the session, and the next subscribe would then
		// replay the same rows from the node anyway.
		// Row 9 is the stalled page's own content, which IS rendered — what must not accumulate is everything that
		// arrives afterwards.
		expect(result.current.view?.committed.map((segment) => segment.seq)).toEqual([5, 9]);
		fire(TRANSCRIPTION_SEGMENT_COMMITTED, segmentPush(20, { text: "after the stall" }));
		expect(result.current.view?.committed.map((segment) => segment.seq)).toEqual([5, 9]);

		// And it was dropped, not parked: the subscribe that recovers does not flush it either.
		subscribeHandler = () => Promise.resolve(snapshot({ lastSeq: 5 }));
		act(() => reconnectedFanout?.());

		await waitFor(() => expect(result.current.hub.connected).toBe(true));
		expect(result.current.view?.committed.map((segment) => segment.seq)).toEqual([5, 9]);
	});

	// R40a: S6 gates its dictation button on this one flag, so it must mean "what the view renders is complete".
	it("Connected_IsFalseUntilTheSnapshotResolvesAndEveryPageIsDrained", async () => {
		const firstPage = deferred<unknown>();
		const secondPage = deferred<unknown>();
		subscribeHandler = (_afterSeq, callIndex) => (callIndex === 0 ? firstPage.promise : secondPage.promise);
		const { result } = renderHub();

		await waitFor(() => expect(subscribeInvokes()).toHaveLength(1));
		expect(result.current.hub.connected).toBe(false);
		expect(result.current.hub.subscriptionReady).toBe(false);

		await act(async () => {
			firstPage.resolve(snapshot({ lastSeq: 2, segments: [row(1), row(2)], replayTruncated: true }));
			await firstPage.promise;
		});
		await waitFor(() => expect(subscribeInvokes()).toHaveLength(2));
		expect(result.current.hub.connected).toBe(false);

		await act(async () => {
			secondPage.resolve(snapshot({ lastSeq: 3, segments: [row(3)] }));
			await secondPage.promise;
		});

		await waitFor(() => expect(result.current.hub.connected).toBe(true));
		expect(result.current.hub.subscriptionReady).toBe(true);
	});

	// A status that arrived live is newer than the one the in-flight subscribe was already carrying, so the snapshot
	// must not undo it.
	it("StatusChanged_SurfacesATerminalStatusAndIsNotOverwrittenByALaterSnapshot", async () => {
		const gate = deferred<unknown>();
		subscribeHandler = () => gate.promise;
		const { result } = renderHub();
		await waitFor(() => expect(subscribeInvokes()).toHaveLength(1));

		fire(TRANSCRIPTION_SESSION_STATUS_CHANGED, { sessionId: SESSION_ID, status: "Failed" });
		await waitFor(() => expect(result.current.view?.status).toBe("Failed"));

		await act(async () => {
			gate.resolve(snapshot({ status: "Transcribing" }));
			await gate.promise;
		});

		await waitFor(() => expect(result.current.hub.connected).toBe(true));
		expect(result.current.view?.status).toBe("Failed");
	});

	// M1: nothing retries an initial subscribe — `withAutomaticReconnect` does not retry an initial start either, so
	// a refused subscription never fires `onreconnected`. Without a named failure the view sits empty and
	// permanently disconnected while the UI keeps offering Start.
	it("SubscribeSession_WhenTheInvokeRejects_SurfacesSubscribeFailedAndStaysDisconnected", async () => {
		subscribeHandler = () => Promise.reject(new Error("transcription-disabled"));
		const { result } = renderHub();

		await waitFor(() => expect(result.current.hub.subscribeFailed).not.toBeNull());
		expect(result.current.hub.subscribeFailed).toEqual({ code: "transcription-disabled" });
		expect(result.current.hub.connected).toBe(false);

		// A later subscribe that works clears it, so the alert never outlives the condition it reports.
		subscribeHandler = () => Promise.resolve(snapshot({ lastSeq: 1, segments: [row(1)] }));
		act(() => reconnectedFanout?.());

		await waitFor(() => expect(result.current.hub.connected).toBe(true));
		expect(result.current.hub.subscribeFailed).toBeNull();
	});

	// A transport failure, or a snapshot that did not match the contract, is not one of the hub's own codes — and an
	// untranslated browser string is not something an operator can act on.
	it("SubscribeSession_WhenTheFailureIsNotAHubRefusal_ReportsTheGenericCode", async () => {
		subscribeHandler = () => Promise.reject(new Error("Failed to invoke 'SubscribeSession' due to an error"));
		const { result } = renderHub();

		await waitFor(() => expect(result.current.hub.subscribeFailed).not.toBeNull());
		expect(result.current.hub.subscribeFailed).toEqual({ code: "transcription-subscribe-failed" });
	});

	// Codex r1 #5: a transport that stays Connected but stops completing invokes is not a slow node — the node returns
	// as soon as it has copied the frame — so a frame still open after the stall timeout stops capture as disconnected.
	it("PushFrame_WhenTheInvokeNeverCompletes_RejectsAsDisconnectedAfterThirtySeconds", async () => {
		pushHandler = () => new Promise<unknown>(() => undefined);
		const { result } = renderHub();
		await waitFor(() => expect(result.current.hub.connected).toBe(true));

		vi.useFakeTimers();
		try {
			let settled = false;
			const pushed = result.current.hub.pushFrame("mono", Int16Array.of(1)).catch((error: unknown) => {
				settled = true;
				return error;
			});

			await vi.advanceTimersByTimeAsync(29_999);
			expect(settled).toBe(false);
			await vi.advanceTimersByTimeAsync(1);
			expect(settled).toBe(true);

			const rejection = await pushed;
			expect(rejection).toBeInstanceOf(CaptureError);
			expect(rejection).toMatchObject({ code: "disconnected" });
		} finally {
			vi.useRealTimers();
		}
	});

	it("PushFrame_WhenTheInvokeCompletes_LeavesNoStallTimerBehind", async () => {
		const { result } = renderHub();
		await waitFor(() => expect(result.current.hub.connected).toBe(true));

		vi.useFakeTimers();
		try {
			const before = vi.getTimerCount();
			await result.current.hub.pushFrame("mono", Int16Array.of(1));
			expect(vi.getTimerCount()).toBe(before);
		} finally {
			vi.useRealTimers();
		}
	});

	it("PushFrame_WhenTheInvokeRejects_ReturnsARejectedPromise", async () => {
		pushHandler = () => Promise.reject(new Error("transcription-frame-too-large"));
		const { result } = renderHub();
		await waitFor(() => expect(result.current.hub.connected).toBe(true));

		await expect(result.current.hub.pushFrame("you", Int16Array.of(1))).rejects.toThrow("transcription-frame-too-large");
	});
});
