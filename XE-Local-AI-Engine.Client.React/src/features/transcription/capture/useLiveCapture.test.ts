// @vitest-environment jsdom

import { act, renderHook } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { CaptureError, type CaptureChannel } from "@/features/transcription/capture/CaptureSource";
import { FakeCaptureSource } from "@/features/transcription/capture/FakeCaptureSource";
import {
	type CaptureSourceFactory,
	type LiveCaptureRequest,
	useLiveCapture,
} from "@/features/transcription/capture/useLiveCapture";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { createProvidersWrapper } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

const sessionId = "11111111-0000-4000-8000-000000000001";

// The hub hook has its own tests (`useTranscriptionHub.test.tsx`); what this file drives is the orchestration ON TOP
// of it, so the transport is replaced by a scriptable double rather than a second copy of the SignalR fake.
const hub = vi.hoisted(() => ({
	pushFrame: vi.fn<(channel: CaptureChannel, pcm: Int16Array) => Promise<void>>(),
	endSession: vi.fn<() => Promise<void>>(),
	status: "Transcribing",
}));

vi.mock("@/features/transcription/hooks/useTranscriptionHub", () => ({
	useTranscriptionHub: () => ({
		connected: true,
		subscriptionReady: true,
		replayStalled: null,
		subscribeFailed: null,
		pushFrame: hub.pushFrame,
		endSession: hub.endSession,
	}),
	useLiveTranscript: () => ({ committed: [], partials: {}, status: hub.status, lastSeq: 0, replayTruncated: false }),
}));

interface Deferred<T> {
	readonly promise: Promise<T>;
	resolve(value: T): void;
	reject(error: unknown): void;
}

// Every wait in this file is a promise the test settles itself. No timers, no sleeps: "did this happen before that"
// is decided by what has been resolved, never by how long something was given to finish.
function deferred<T>(): Deferred<T> {
	let resolve!: (value: T) => void;
	let reject!: (error: unknown) => void;
	const promise = new Promise<T>((resolveInner, rejectInner) => {
		resolve = resolveInner;
		reject = rejectInner;
	});
	return { promise, resolve, reject };
}

const livePath = localApiPath(`transcription/sessions/${sessionId}/live/start`);

interface Harness {
	readonly order: string[];
	readonly sources: Map<string, FakeCaptureSource>;
	readonly factory: CaptureSourceFactory;
}

/**
 * A capture factory over the repo's `FakeCaptureSource`, recording the order the sources were asked for.
 *
 * The record is taken at CONSTRUCTION, which is what the R39a assertions need: the hook constructs a source and calls
 * `start` on it in the same synchronous statement pair, so the construction order is the invocation order — and it is
 * observable from the test's own frame, before any await has had the chance to run.
 */
function harness(failures: Partial<Record<"microphone" | "systemAudio", CaptureError>> = {}): Harness {
	const order: string[] = [];
	const sources = new Map<string, FakeCaptureSource>();
	const factory: CaptureSourceFactory = (kind, channel) => {
		order.push(kind);
		const failWith = failures[kind];
		const source = new FakeCaptureSource(channel, failWith === undefined ? undefined : { failWith });
		sources.set(kind, source);
		return source;
	};
	return { order, sources, factory };
}

/** Answers `live/start` only once the returned gate is settled, so a test can hold the endpoint open. */
function liveStartGate(): Deferred<void> {
	const gate = deferred<void>();
	server.use(
		http.post(livePath, async () => {
			await gate.promise;
			return HttpResponse.json({ sessionId, status: "Transcribing", lastSeq: 0 });
		}),
	);
	return gate;
}

function liveStartOk(): void {
	server.use(http.post(livePath, () => HttpResponse.json({ sessionId, status: "Transcribing", lastSeq: 0 })));
}

function renderCapture() {
	const { wrapper } = createProvidersWrapper();
	return renderHook(() => useLiveCapture(sessionId), { wrapper });
}

async function startCapture(
	capture: { current: ReturnType<typeof useLiveCapture> },
	request: LiveCaptureRequest,
	factory: CaptureSourceFactory,
): Promise<void> {
	await act(async () => {
		await capture.current.start(request, factory);
	});
}

describe("useLiveCapture", () => {
	beforeEach(() => {
		hub.pushFrame.mockReset();
		hub.pushFrame.mockResolvedValue(undefined);
		hub.endSession.mockReset();
		hub.endSession.mockResolvedValue(undefined);
		hub.status = "Transcribing";
	});

	afterEach(() => {
		vi.clearAllMocks();
	});

	// Plan §4.2: WhenSystemAudioHasNoAudioTrack_TheSessionIsNeverStarted
	// R19: a shared surface that came back silent is a failure, not a session that quietly records nothing. The picker
	// is opened from the click (so `live/start` has already been sent by the time the stream is inspected), which is
	// why "never started" is proved as: no frame ever reached the node, and the session was ended again.
	it("never runs a system-audio session whose shared surface has no audio track", async () => {
		liveStartOk();
		const { factory, sources } = harness({ systemAudio: new CaptureError("no-audio-track", "no audio track") });
		const { result } = renderCapture();

		await startCapture(result, { kind: "systemAudio" }, factory);

		expect(result.current.error).toBe("no-audio-track");
		expect(result.current.state).toBe("idle");
		expect(sources.get("systemAudio")?.started).toBe(false);
		expect(hub.pushFrame).not.toHaveBeenCalled();
		expect(hub.endSession).toHaveBeenCalledTimes(1);
	});

	// Plan §4.2: WhenTheDisplayPickerIsCancelledOnBoth_TheMicrophoneIsStopped
	it("stops the microphone when the display picker is cancelled on a both-sources session", async () => {
		liveStartOk();
		const { factory, sources } = harness({ systemAudio: new CaptureError("permission-denied", "picker cancelled") });
		const { result } = renderCapture();

		await startCapture(result, { kind: "both" }, factory);

		expect(result.current.error).toBe("permission-denied");
		expect(sources.get("microphone")?.started).toBe(true);
		expect(sources.get("microphone")?.stopped).toBe(true);
	});

	// Plan §4.2: BothSources_MapMicrophoneToYouAndSystemToOthers
	// D2 / R19: the two lanes are attributed, never mixed. A swap here relabels every segment in the transcript.
	it("maps the microphone to you and the system audio to others", async () => {
		liveStartOk();
		const { factory, sources } = harness();
		const { result } = renderCapture();

		await startCapture(result, { kind: "both" }, factory);

		expect(sources.get("microphone")?.channel).toBe("you");
		expect(sources.get("systemAudio")?.channel).toBe("others");
		expect(result.current.state).toBe("capturing");
	});

	it("gives a lone microphone the mono lane", async () => {
		liveStartOk();
		const { factory, sources } = harness();
		const { result } = renderCapture();

		await startCapture(result, { kind: "microphone" }, factory);

		expect(sources.get("microphone")?.channel).toBe("mono");
	});

	// The node registers only the Others lane for a SystemAudio session (`TranscriptionService.LiveChannelsFor`), so a
	// mono frame would be refused as transcription-unknown-channel on the first push and nothing would transcribe.
	it("gives lone system audio the others lane, the one the node registered for it", async () => {
		liveStartOk();
		const { factory, sources } = harness();
		const { result } = renderCapture();

		await startCapture(result, { kind: "systemAudio" }, factory);

		expect(sources.get("systemAudio")?.channel).toBe("others");
	});

	// Plan §4.2: Stop_WithAPendingPush_StopsEverySourceWithoutAwaitingIt
	// R34: the operator pressing stop must not wait behind a frame parked inside a 30 s inference.
	it("stops every source without awaiting a pending push", async () => {
		liveStartOk();
		const parked = deferred<void>();
		hub.pushFrame.mockReturnValue(parked.promise);
		const { factory, sources } = harness();
		const { result } = renderCapture();

		await startCapture(result, { kind: "microphone" }, factory);
		act(() => sources.get("microphone")?.emit(new Int16Array([1, 2, 3])));
		expect(hub.pushFrame).toHaveBeenCalledTimes(1);

		await act(async () => {
			await result.current.stop();
		});

		expect(sources.get("microphone")?.stopped).toBe(true);
		expect(hub.endSession).toHaveBeenCalledTimes(1);
		expect(result.current.state).toBe("idle");
		// Only now is the frame allowed to finish; the stop above did not wait for it.
		parked.resolve(undefined);
	});

	// Plan §4.2: OverloadedStatusPush_StopsCaptureAndSurfacesTheNamedError
	it("stops capture and names the error when the node reports overloaded", async () => {
		liveStartOk();
		const { factory, sources } = harness();
		const { result, rerender } = renderCapture();

		await startCapture(result, { kind: "microphone" }, factory);
		expect(result.current.state).toBe("capturing");

		hub.status = "Overloaded";
		await act(async () => {
			rerender();
		});

		expect(result.current.error).toBe("overloaded");
		expect(result.current.state).toBe("idle");
		expect(sources.get("microphone")?.stopped).toBe(true);
		expect(hub.endSession).toHaveBeenCalledTimes(1);
	});

	// Plan §4.2: MicrophoneOnly_AwaitsLiveStartBeforeGetUserMedia
	// R39a: no display picker is involved, so there is no activation window to protect and R31's rule stands unaltered
	// — nothing is acquired until the node has accepted the session.
	it("acquires no microphone until live/start has resolved", async () => {
		const gate = liveStartGate();
		const { factory, order } = harness();
		const { result } = renderCapture();

		let orderWhilePending: string[] = [];
		await act(async () => {
			const started = result.current.start({ kind: "microphone" }, factory);
			// One turn of the event loop: enough for the endpoint to be issued, not enough for the gate to open.
			await Promise.resolve();
			orderWhilePending = [...order];
			gate.resolve(undefined);
			await started;
		});

		expect(orderWhilePending).toEqual([]);
		expect(order).toEqual(["microphone"]);
	});

	// Plan §4.2: SystemAudio_InvokesGetDisplayMediaBeforeAnyAwait
	it("opens the display picker before any await on a system-audio session", async () => {
		const gate = liveStartGate();
		const { factory, order } = harness();
		const { result } = renderCapture();

		let orderAtEntry: string[] = [];
		await act(async () => {
			const started = result.current.start({ kind: "systemAudio" }, factory);
			orderAtEntry = [...order];
			gate.resolve(undefined);
			await started;
		});

		expect(orderAtEntry).toEqual(["systemAudio"]);
	});

	// Plan §4.2: Both_InvokesGetDisplayMediaBeforeAnyAwait
	it("opens the display picker before any await on a both-sources session", async () => {
		const gate = liveStartGate();
		const { factory, order } = harness();
		const { result } = renderCapture();

		let orderAtEntry: string[] = [];
		await act(async () => {
			const started = result.current.start({ kind: "both" }, factory);
			orderAtEntry = [...order];
			gate.resolve(undefined);
			await started;
		});

		expect(orderAtEntry).toEqual(["systemAudio"]);
		expect(order).toEqual(["systemAudio", "microphone"]);
	});

	// Plan §4.2: DisplayPaths_ForwardNoFrameUntilLiveStartResolves
	// R31: the picker may be open for a minute. What it captured before the node accepted the session is not this
	// session's audio, and is dropped rather than sent.
	it("forwards no frame on a display path until live/start resolves", async () => {
		const gate = liveStartGate();
		const { factory, sources } = harness();
		const { result } = renderCapture();

		let pushesWhilePending = 0;
		await act(async () => {
			const started = result.current.start({ kind: "systemAudio" }, factory);
			await Promise.resolve();
			sources.get("systemAudio")?.emit(new Int16Array([7, 7]));
			pushesWhilePending = hub.pushFrame.mock.calls.length;
			gate.resolve(undefined);
			await started;
		});

		expect(pushesWhilePending).toBe(0);
		act(() => sources.get("systemAudio")?.emit(new Int16Array([7, 7])));
		expect(hub.pushFrame).toHaveBeenCalledTimes(1);
	});

	// Plan §4.2: WhenLiveStartFails_StopsEveryTrackOfTheDisplayStream
	it("stops the display stream when live/start fails", async () => {
		server.use(http.post(livePath, () => HttpResponse.json({ sessionId, status: "Cancelled", lastSeq: 3 }, { status: 409 })));
		const { factory, sources } = harness();
		const { result } = renderCapture();

		await startCapture(result, { kind: "systemAudio" }, factory);

		expect(sources.get("systemAudio")?.stopped).toBe(true);
		// A refusal from the node is neither a browser capability nor a device problem, and must not be reported as one.
		expect(result.current.error).toBe("start-failed");
		expect(hub.endSession).not.toHaveBeenCalled();
	});

	// Plan §4.2: Both_WhenTheMicrophoneFails_StopsEveryTrackOfTheDisplayStream
	it("stops the display stream when the microphone fails on a both-sources session", async () => {
		liveStartOk();
		const { factory, sources } = harness({ microphone: new CaptureError("permission-denied", "microphone refused") });
		const { result } = renderCapture();

		await startCapture(result, { kind: "both" }, factory);

		expect(sources.get("systemAudio")?.stopped).toBe(true);
		expect(result.current.error).toBe("permission-denied");
		expect(hub.endSession).toHaveBeenCalledTimes(1);
	});

	// Plan §4.2: the mirror case — the display fails and the microphone must not be left hot.
	it("stops the microphone when the display source fails on a both-sources session", async () => {
		liveStartOk();
		const { factory, sources } = harness({ systemAudio: new CaptureError("no-audio-track", "no audio track") });
		const { result } = renderCapture();

		await startCapture(result, { kind: "both" }, factory);

		expect(sources.get("microphone")?.stopped).toBe(true);
		expect(result.current.error).toBe("no-audio-track");
	});

	// S4 review B1 — WhenUnmountedDuringStart_StopsEverySourceAcquiredAfterwards.
	// The unmount teardown stops whatever `sourcesRef` holds at that instant. A start still awaiting `live/start`
	// used to resume afterwards, acquire the microphone into an array nothing pointed at any more, and reach
	// "capturing" on a component that was gone — leaving the microphone hot for the life of the tab.
	it("acquires nothing more and forwards nothing when the page is left during start", async () => {
		const gate = liveStartGate();
		const { factory, sources } = harness();
		const { result, unmount } = renderCapture();

		let started: Promise<void> = Promise.resolve();
		await act(async () => {
			started = result.current.start({ kind: "both" }, factory);
			// One turn of the event loop: the display source is acquired and the endpoint issued, and the gate holds
			// `live/start` open across the unmount below.
			await Promise.resolve();
		});
		unmount();
		await act(async () => {
			gate.resolve(undefined);
			await started;
		});

		expect(sources.get("systemAudio")?.stopped).toBe(true);
		// Acquiring it at all would leave it hot: nothing after the unmount can reach it.
		expect(sources.has("microphone")).toBe(false);
		sources.get("systemAudio")?.emit(new Int16Array([1]));
		expect(hub.pushFrame).not.toHaveBeenCalled();
	});

	// Plan §4.2: WhenAPushReportsOverloaded_StopsEverySourceEndsTheSessionAndShowsTheError
	// R34a: hitting the hub's in-flight limit is a visible failure. A dropped frame is a hole in the transcript that
	// nothing refetches, so capture stops instead.
	it("stops every source and ends the session when a push reports overloaded", async () => {
		liveStartOk();
		const { factory, sources } = harness();
		const { result } = renderCapture();

		await startCapture(result, { kind: "both" }, factory);
		hub.pushFrame.mockRejectedValue(new CaptureError("overloaded", "maximum frames in flight"));

		await act(async () => {
			sources.get("microphone")?.emit(new Int16Array([1]));
			await Promise.resolve();
		});

		expect(result.current.error).toBe("overloaded");
		expect(result.current.state).toBe("idle");
		expect(sources.get("microphone")?.stopped).toBe(true);
		expect(sources.get("systemAudio")?.stopped).toBe(true);
		expect(hub.endSession).toHaveBeenCalledTimes(1);
		// Outstanding work stayed bounded: the rejected frame stopped capture rather than being retried.
		expect(hub.pushFrame).toHaveBeenCalledTimes(1);
	});
});
