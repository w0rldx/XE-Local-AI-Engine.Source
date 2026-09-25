import { useCallback, useEffect, useRef, useState } from "react";

import {
	CaptureError,
	type CaptureChannel,
	type CaptureErrorCode,
	type CaptureFrame,
	type CaptureSource,
} from "@/features/transcription/capture/CaptureSource";
import { MicrophoneCaptureSource } from "@/features/transcription/capture/MicrophoneCaptureSource";
import { SystemAudioCaptureSource } from "@/features/transcription/capture/SystemAudioCaptureSource";
import { type TranscriptionSubscribeFailed, useTranscriptionHub } from "@/features/transcription/hooks/useTranscriptionHub";
import {
	type ProcessCaptureBlockedReason,
	processCaptureBlockedReason,
} from "@/features/transcription/models/TranscriptionModels";
import {
	useCancelTranscriptionSession,
	useStartLiveTranscriptionSession,
	useStartProcessCapture,
} from "@/features/transcription/queries/useTranscriptionQueries";

// Orchestrates one live capture: open the session on the node, acquire the browser sources, forward their frames to
// the hub, and tear all of it down again on stop or on a failure.

export type LiveCaptureRequest =
	| { readonly kind: "microphone"; readonly deviceId?: string }
	| { readonly kind: "systemAudio" }
	| { readonly kind: "both"; readonly deviceId?: string }
	// Windows per-application capture: the node records the process itself, so the browser opens no source and pushes
	// no frame. Only the node's Others lane ever carries this session's audio.
	| { readonly kind: "process"; readonly processId: number };

export type LiveCaptureState = "idle" | "starting" | "capturing" | "stopping";
type PendingEndIntent = "graceful" | "cancel";

/**
 * Everything the error alert can be keyed on. `start-failed` is the one code that is not a `CaptureErrorCode`: it is
 * the node refusing to open the session (a File session, an unknown one, or one that already reached a terminal
 * status), which is neither a browser capability nor a device problem and must not be reported as one.
 */
export type LiveCaptureErrorCode =
	| CaptureErrorCode
	| "start-failed"
	| "stop-failed"
	// The node ended the session because no audio frame ever arrived (`transcriptionSessionStatusChanged.errorCode`).
	| "live-never-attached"
	| ProcessCaptureBlockedReason;

export type CaptureSourceFactory = (
	kind: "microphone" | "systemAudio",
	channel: CaptureChannel,
	deviceId: string | undefined,
) => CaptureSource;

export interface LiveCaptureHandle {
	readonly state: LiveCaptureState;
	readonly error: LiveCaptureErrorCode | null;
	/** Non-null once the hub's replay drain gave up — the transcript on screen may be missing rows. */
	readonly replayStalled: boolean;
	/**
	 * True once the hub subscription is live. Start is gated on it: a capture begun before the subscription is up
	 * fails on its first frame a quarter of a second later, which is a worse way to learn the node is not there.
	 */
	readonly connected: boolean;
	/** Non-null once the node refused the subscription, so the reason can be named instead of silently swallowed. */
	readonly subscribeFailed: TranscriptionSubscribeFailed | null;
	/**
	 * MUST be invoked directly from the click handler, with nothing awaited between the user gesture and it (R39a).
	 * Every display path calls `getDisplayMedia` synchronously inside this call, and the Screen Capture specification
	 * requires the browser to reject a picker opened outside the transient-activation window — so routing this through
	 * a confirmation dialog, an async guard or an `await` first silently breaks system-audio capture, with no error the
	 * operator can act on. The call-order tests in `useLiveCapture.test.ts` are the only thing that catches it.
	 */
	start(request: LiveCaptureRequest, createSource?: CaptureSourceFactory): Promise<void>;
	stop(): Promise<void>;
	cancel(): Promise<void>;
}

const defaultCaptureSourceFactory: CaptureSourceFactory = (kind, channel, deviceId) =>
	kind === "microphone" ? new MicrophoneCaptureSource(channel, deviceId) : new SystemAudioCaptureSource(channel);

/**
 * D2 / R19: nothing is ever mixed. The lanes are the node's (`TranscriptionService.LiveChannelsFor`): a microphone
 * alone is `Mono`, system audio alone is `Others` (the same lane a Windows per-process capture feeds), and the pair
 * is `You` + `Others`. A frame on a lane the session did not register is refused as `transcription-unknown-channel`.
 */
function microphoneChannel(request: LiveCaptureRequest): CaptureChannel {
	return request.kind === "both" ? "you" : "mono";
}

function systemChannel(): CaptureChannel {
	return "others";
}

function toErrorCode(error: unknown): LiveCaptureErrorCode {
	if (error instanceof CaptureError) {
		return error.code;
	}
	// The capture/process endpoint answers its refusals with a typed reason, and the three mean different things:
	// this box cannot capture an application at all, the session is not live, or something is already capturing it.
	// Reporting all three as "the node refused to open this live session. It may already have finished." is wrong in
	// every direction, so the reason wins where there is one.
	return processCaptureBlockedReason(error) ?? "start-failed";
}

export function useLiveCapture(sessionId: string | null): LiveCaptureHandle {
	const { pushFrame, endSession, replayStalled, connected, subscribeFailed, admissionClosed, terminal } =
		useTranscriptionHub(sessionId);
	const startLive = useStartLiveTranscriptionSession();
	const startProcessCapture = useStartProcessCapture();
	const cancelSession = useCancelTranscriptionSession();
	// Read through a ref so `teardown` — the primitive every stop path funnels into — keeps its identity when the
	// mutation's own state changes.
	const cancelSessionRef = useRef(cancelSession.mutateAsync);
	cancelSessionRef.current = cancelSession.mutateAsync;
	const [state, setState] = useState<LiveCaptureState>("idle");
	const [error, setError] = useState<LiveCaptureErrorCode | null>(null);
	const mountedRef = useRef(true);

	// The state machine is read from inside promise continuations that outlive a render, so it is mirrored in a ref:
	// a stale `state` closure would let a second start run over a capture that is already up.
	const stateRef = useRef<LiveCaptureState>("idle");
	const sourcesRef = useRef<CaptureSource[]>([]);
	const forwardingRef = useRef(false);
	const sessionOpenRef = useRef(false);
	// The termination this browser still needs the node to acknowledge.
	const pendingEndRef = useRef<PendingEndIntent | null>(null);
	// Explicit cancellation can race a graceful EndSession or an unmount. One request is enough to interrupt either,
	// and sharing its promise keeps those paths from issuing duplicate cancels for the same session.
	const cancelPromiseRef = useRef<Promise<void> | null>(null);
	// Bumped by every `teardown`. A `start` whose generation has moved is running against a capture that was already
	// torn down — by an unmount, a stop or an abort — and `teardown` only stopped what `sourcesRef` held at that
	// instant. Anything acquired after it is reachable from this frame alone, so this frame has to stop it.
	const startGenerationRef = useRef(0);
	// Separately guards async state continuations: a Cancel that finishes before a stuck graceful EndSession, an
	// unmount, or a new start must make that older continuation unable to change the current phase.
	const terminationGenerationRef = useRef(0);
	// Set by the node's terminal status push: the session is over there, so nothing is left to end or cancel, and a
	// REST cancel against it could only fail and be misreported as `stop-failed`.
	const nodeEndedRef = useRef(false);
	nodeEndedRef.current = terminal !== null;

	const setPhase = useCallback((next: LiveCaptureState): void => {
		stateRef.current = next;
		if (mountedRef.current) {
			setState(next);
		}
	}, []);

	/**
	 * Ends the live session, over the hub where it can and over REST where it cannot.
	 *
	 * `endSession` resolves false when there is no connected hub to invoke `EndSession` on, and until this fallback
	 * existed that was silent: a Stop pressed while the transport was down ended nothing, the UI went idle, and the
	 * reconnect re-subscribed and disarmed the node's abandonment grace. The node then kept the session — and, for an
	 * `ApplicationProcess` session, its recorder on the operator's application — running indefinitely. The cancel
	 * endpoint is the interruptible fallback when graceful delivery is unavailable.
	 *
	 * Applied for every request kind, not just `process`: the same gap leaves a browser-fed session stuck in
	 * Transcribing, which is the same bug with a quieter symptom.
	 *
	 * When NEITHER transport acknowledges, the end is not dropped: `pendingEndRef` holds it, the operator is told
	 * (`stop-failed` rather than a silent return to idle), and the effect below redelivers it the moment the hub is
	 * back. That reconnect is the same event that re-subscribes and disarms the node's abandonment grace, so a stop
	 * left only in this browser would never reach the node at all.
	 */
	const acknowledgeEnd = useCallback((): void => {
		pendingEndRef.current = null;
		// Only this code is cleared: a teardown that ran because of a capture failure must keep saying so.
		if (mountedRef.current) {
			setError((current) => (current === "stop-failed" ? null : current));
		}
	}, []);

	const cancelLiveSession = useCallback(async (): Promise<void> => {
		if (sessionId === null || nodeEndedRef.current) {
			return;
		}
		if (cancelPromiseRef.current !== null) {
			return await cancelPromiseRef.current;
		}
		pendingEndRef.current = "cancel";
		const cancellation = cancelSessionRef
			.current(sessionId)
			.then(acknowledgeEnd)
			.catch((cancelError: unknown) => {
				// A graceful end may have won the race while REST was in flight. Only that acknowledgement makes a
				// terminal-looking 404 safe to ignore; every other refusal remains visible and retryable.
				if (pendingEndRef.current !== null) {
					if (mountedRef.current) {
						setError((current) => current ?? "stop-failed");
					}
					throw cancelError;
				}
			})
			.finally(() => {
				cancelPromiseRef.current = null;
			});
		cancelPromiseRef.current = cancellation;
		return await cancellation;
	}, [acknowledgeEnd, sessionId]);

	const endLiveSession = useCallback(async (): Promise<void> => {
		if (sessionId === null || nodeEndedRef.current) {
			return;
		}
		if (pendingEndRef.current === "cancel") {
			await cancelLiveSession();
			return;
		}
		pendingEndRef.current = "graceful";
		try {
			if (await endSession()) {
				acknowledgeEnd();
				return;
			}
		} catch {
			// The invoke may or may not have reached the node before it threw, so the outcome is unknown and the
			// cancellation fallback runs rather than claiming graceful completion.
		}
		// An explicit Cancel may have completed while the hub call was still pending. Its REST acknowledgement is
		// already terminal; issuing a second cancel here can only turn the terminal 404 into a false failure.
		if (pendingEndRef.current === null) {
			return;
		}
		try {
			await cancelLiveSession();
		} catch {
			// Both transports are down. The end stays pending for the reconnect, and the operator is told rather than
			// shown an idle capture the node never heard about.
			if (mountedRef.current) {
				setError((current) => current ?? "stop-failed");
			}
		}
	}, [acknowledgeEnd, cancelLiveSession, endSession, sessionId]);

	// A stop the node never acknowledged is redelivered as soon as the hub is connected again. Without this the
	// reconnect would disarm the abandonment grace while the only record of the stop sat in this browser.
	useEffect(() => {
		if (!connected || pendingEndRef.current === null) {
			return;
		}
		const retry = pendingEndRef.current === "cancel" ? cancelLiveSession : endLiveSession;
		retry().catch(() => undefined);
	}, [cancelLiveSession, connected, endLiveSession]);

	// R34: sources and the audio graph go down FIRST, then the session is ended. `EndSession` is awaited but a pending
	// `PushAudioFrame` never is — the node returns as soon as the frame is queued, so one still pending is a slow
	// transport, and the microphone must not stay hot for it.
	const stopSources = useCallback(async (): Promise<void> => {
		startGenerationRef.current += 1;
		forwardingRef.current = false;
		const sources = sourcesRef.current;
		sourcesRef.current = [];
		await Promise.allSettled(sources.map((source) => source.stop()));
	}, []);

	const teardown = useCallback(async (): Promise<void> => {
		await stopSources();
		if (sessionOpenRef.current) {
			sessionOpenRef.current = false;
			await endLiveSession();
		}
	}, [endLiveSession, stopSources]);

	const cancelTeardown = useCallback(async (): Promise<void> => {
		await stopSources();
		if (sessionOpenRef.current || pendingEndRef.current) {
			sessionOpenRef.current = false;
			await cancelLiveSession();
		}
	}, [cancelLiveSession, stopSources]);

	const abort = useCallback(
		async (code: LiveCaptureErrorCode): Promise<void> => {
			if (stateRef.current === "idle" || stateRef.current === "stopping") {
				return;
			}
			// The actionable capture error does not wait behind a network round trip to become visible.
			setError(code);
			setPhase("stopping");
			const terminationGeneration = ++terminationGenerationRef.current;
			try {
				await cancelTeardown();
			} catch {
				// Keep the capture failure, which is more useful than replacing it with a second teardown failure.
			}
			if (terminationGenerationRef.current === terminationGeneration) {
				setPhase("idle");
			}
		},
		[cancelTeardown, setPhase],
	);

	const start = useCallback(
		async (request: LiveCaptureRequest, createSource: CaptureSourceFactory = defaultCaptureSourceFactory): Promise<void> => {
			if (sessionId === null || stateRef.current !== "idle") {
				return;
			}
			terminationGenerationRef.current += 1;
			const generation = startGenerationRef.current;
			setError(null);
			setPhase("starting");
			const acquired: CaptureSource[] = [];
			sourcesRef.current = acquired;
			forwardingRef.current = false;

			// R31: a frame produced before the node accepted the session is dropped on the floor rather than sent. The
			// picker may be open for a minute; what it captured in that minute is not this session's audio.
			const onFrame = (frame: CaptureFrame): void => {
				if (!forwardingRef.current) {
					return;
				}
				// R34a: a refused frame is never swallowed. A dead transport rejects, which means speech the node did not
				// receive — so capture stops loudly instead of leaving a hole in the transcript.
				pushFrame(frame.channel, frame.pcm).catch((pushError: unknown) => {
					abort(toErrorCode(pushError)).catch(() => undefined);
				});
			};

			// R39a: the display picker is invoked HERE, synchronously, before this function's first await. Everything
			// else — the endpoint, the microphone prompt, `addModule` — can outlive the transient user activation the
			// Screen Capture specification requires at the moment `getDisplayMedia` is called.
			let display: CaptureSource | null = null;
			let displayStarted: Promise<void> | null = null;
			// Named kinds, not "everything but the microphone": a process-capture session must open no picker at all.
			if (request.kind === "systemAudio" || request.kind === "both") {
				display = createSource("systemAudio", systemChannel(), undefined);
				acquired.push(display);
				displayStarted = display.start(onFrame);
				// Parked until it is awaited below; without this a picker cancelled while `live/start` is in flight is an
				// unhandled rejection.
				displayStarted.catch(() => undefined);
			}

			// The stale path, taken when a teardown ran while this start was awaiting something. It touches no state:
			// the component it belonged to is gone, or a later start already owns the state machine.
			// Everything already acquired stops at once — a hot microphone must not wait for the operator to answer the
			// picker — and the display stops again once the picker settles, because its stream registers only then.
			const releaseSources = async (): Promise<void> => {
				await Promise.allSettled(acquired.map((source) => source.stop()));
				if (display !== null && displayStarted !== null) {
					await Promise.allSettled([displayStarted]);
					await display.stop().catch(() => undefined);
				}
			};

			const abandon = async (cancelTheSession: boolean): Promise<void> => {
				forwardingRef.current = false;
				await releaseSources();
				if (cancelTheSession) {
					await cancelLiveSession().catch(() => undefined);
				}
			};

			try {
				// A3: the microphone (and its permission prompt) comes BEFORE `live/start`, so the operator answers the
				// prompt while the node spends its first-use minute on the runtime download and model load, not after.
				// Its frames are dropped until `forwardingRef` is set below (R31): audio captured while the session was
				// still opening is discarded on purpose. No session is open yet, so a stale generation cancels nothing.
				if (request.kind === "microphone" || request.kind === "both") {
					const microphone = createSource("microphone", microphoneChannel(request), request.deviceId);
					acquired.push(microphone);
					await microphone.start(onFrame);
					if (startGenerationRef.current !== generation) {
						await abandon(false);
						return;
					}
				}

				await startLive.mutateAsync(sessionId);
				if (startGenerationRef.current !== generation) {
					// The teardown ran while `live/start` was in flight, so it saw no open session and ended nothing.
					// Closing it here is a no-op once the transport has gone with the unmount, which is what the
					// node's abandonment grace exists for; what matters is that no source survives this return.
					await abandon(true);
					return;
				}
				sessionOpenRef.current = true;

				// R30a: the session is live, so the node has a lane to push into — only now may capture attach. No source
				// is acquired for this kind: the audio never enters this browser, and `teardown` ends the session over the
				// hub, which is the same path that stops the node's recorder.
				if (request.kind === "process") {
					await startProcessCapture.mutateAsync({ sessionId, processId: request.processId });
					if (startGenerationRef.current !== generation) {
						await abandon(false);
						return;
					}
					// No frame ever reaches `onFrame` for this kind; the flag is set anyway so "capturing implies
					// forwarding" holds for every request kind.
					forwardingRef.current = true;
					setPhase("capturing");
					return;
				}

				if (displayStarted !== null) {
					await displayStarted;
					// Past `sessionOpenRef` above, a teardown had the session registered and has already ended it.
					if (startGenerationRef.current !== generation) {
						await abandon(false);
						return;
					}
				}
				forwardingRef.current = true;
				setPhase("capturing");
			} catch (startError: unknown) {
				// Failure disposes everything: a cancelled picker must not leave a hot microphone, and a microphone that
				// failed must not leave a screen share running. The error is reported and the acquired sources stopped
				// without waiting for a picker that may still be open.
				if (startGenerationRef.current !== generation) {
					await abandon(sessionOpenRef.current);
					return;
				}
				await abort(toErrorCode(startError));
				await releaseSources();
			}
		},
		[abort, cancelLiveSession, pushFrame, sessionId, setPhase, startLive, startProcessCapture],
	);

	const stop = useCallback(async (): Promise<void> => {
		if (stateRef.current === "idle" || stateRef.current === "stopping") {
			return;
		}
		setPhase("stopping");
		const terminationGeneration = ++terminationGenerationRef.current;
		await teardown();
		if (terminationGenerationRef.current === terminationGeneration) {
			setPhase("idle");
		}
	}, [setPhase, teardown]);

	// The node closed admission (the buffered-audio cap, or a Stop it already has): a frame sent now would be dropped,
	// so capture ends exactly as if Stop were pressed. The node joins the drain already under way, so the graceful
	// `EndSession` is idempotent; never a REST cancel, and never an error — the drain is the expected outcome.
	useEffect(() => {
		if (admissionClosed && (stateRef.current === "capturing" || stateRef.current === "starting")) {
			stop().catch(() => undefined);
		}
	}, [admissionClosed, stop]);

	// The node ended the session on its own — Cancel from another surface, a failed lane, or no audio before the
	// attachment timeout. Capture stops here without ending anything on the node; only "no audio" is worth an error,
	// a failure is already on the session row, and a cancel is neutral.
	useEffect(() => {
		if (terminal === null) {
			return;
		}
		pendingEndRef.current = null;
		sessionOpenRef.current = false;
		if (stateRef.current !== "starting" && stateRef.current !== "capturing") {
			return;
		}
		terminationGenerationRef.current += 1;
		if (terminal.errorCode === "live-never-attached") {
			setError("live-never-attached");
		}
		setPhase("idle");
		stopSources().catch(() => undefined);
	}, [setPhase, stopSources, terminal]);

	const cancel = useCallback(async (): Promise<void> => {
		if (stateRef.current !== "stopping" || (!sessionOpenRef.current && pendingEndRef.current === null)) {
			return;
		}
		const terminationGeneration = terminationGenerationRef.current;
		await cancelTeardown();
		if (terminationGenerationRef.current === terminationGeneration) {
			terminationGenerationRef.current += 1;
			setPhase("idle");
		}
	}, [cancelTeardown, setPhase]);

	// Leaving the page stops capture. A `MediaStream` is browser-global: without this the microphone stays hot and the
	// screen-share indicator stays lit after a navigation, and the node's abandonment grace would be the only thing that
	// ever closed the session. Unmount-only, through a ref, so a new `endSession` identity does not tear down a live
	// capture mid-session.
	const cancelTeardownRef = useRef(cancelTeardown);
	cancelTeardownRef.current = cancelTeardown;
	useEffect(() => {
		// React StrictMode replays effect setup/cleanup without remounting the hook's refs.
		mountedRef.current = true;
		return () => {
			mountedRef.current = false;
			terminationGenerationRef.current += 1;
			cancelTeardownRef.current().catch(() => undefined);
		};
	}, []);

	return { state, error, replayStalled: replayStalled !== null, connected, subscribeFailed, start, stop, cancel };
}
