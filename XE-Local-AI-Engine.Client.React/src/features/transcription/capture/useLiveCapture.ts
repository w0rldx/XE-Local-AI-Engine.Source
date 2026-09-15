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
import {
	type TranscriptionSubscribeFailed,
	useLiveTranscript,
	useTranscriptionHub,
} from "@/features/transcription/hooks/useTranscriptionHub";
import { useStartLiveTranscriptionSession } from "@/features/transcription/queries/useTranscriptionQueries";

// Orchestrates one live capture: open the session on the node, acquire the browser sources, forward their frames to
// the hub, and tear all of it down again on stop, on a failure, or when the node says it is overloaded.

export type LiveCaptureRequest =
	| { readonly kind: "microphone"; readonly deviceId?: string }
	| { readonly kind: "systemAudio" }
	| { readonly kind: "both"; readonly deviceId?: string };

export type LiveCaptureState = "idle" | "starting" | "capturing" | "stopping";

/**
 * Everything the error alert can be keyed on. `start-failed` is the one code that is not a `CaptureErrorCode`: it is
 * the node refusing to open the session (a File session, an unknown one, or one that already reached a terminal
 * status), which is neither a browser capability nor a device problem and must not be reported as one.
 */
export type LiveCaptureErrorCode = CaptureErrorCode | "start-failed";

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
	return error instanceof CaptureError ? error.code : "start-failed";
}

export function useLiveCapture(sessionId: string | null): LiveCaptureHandle {
	const { pushFrame, endSession, replayStalled, connected, subscribeFailed } = useTranscriptionHub(sessionId);
	const live = useLiveTranscript(sessionId ?? "");
	const startLive = useStartLiveTranscriptionSession();
	const [state, setState] = useState<LiveCaptureState>("idle");
	const [error, setError] = useState<LiveCaptureErrorCode | null>(null);

	// The state machine is read from inside promise continuations that outlive a render, so it is mirrored in a ref:
	// a stale `state` closure would let a second start run over a capture that is already up.
	const stateRef = useRef<LiveCaptureState>("idle");
	const sourcesRef = useRef<CaptureSource[]>([]);
	const forwardingRef = useRef(false);
	const sessionOpenRef = useRef(false);
	// Bumped by every `teardown`. A `start` whose generation has moved is running against a capture that was already
	// torn down — by an unmount, a stop or an abort — and `teardown` only stopped what `sourcesRef` held at that
	// instant. Anything acquired after it is reachable from this frame alone, so this frame has to stop it.
	const startGenerationRef = useRef(0);

	const setPhase = useCallback((next: LiveCaptureState): void => {
		stateRef.current = next;
		setState(next);
	}, []);

	// R34: sources and the audio graph go down FIRST, then the session is ended. `EndSession` is awaited but a pending
	// `PushAudioFrame` never is — it may be parked behind a 30 s inference, and the microphone must not stay hot for it.
	const teardown = useCallback(async (): Promise<void> => {
		startGenerationRef.current += 1;
		forwardingRef.current = false;
		const sources = sourcesRef.current;
		sourcesRef.current = [];
		await Promise.allSettled(sources.map((source) => source.stop()));
		if (sessionOpenRef.current) {
			sessionOpenRef.current = false;
			await Promise.allSettled([endSession()]);
		}
	}, [endSession]);

	const abort = useCallback(
		async (code: LiveCaptureErrorCode): Promise<void> => {
			if (stateRef.current === "idle") {
				return;
			}
			setPhase("stopping");
			await teardown();
			setError(code);
			setPhase("idle");
		},
		[setPhase, teardown],
	);

	const start = useCallback(
		async (request: LiveCaptureRequest, createSource: CaptureSourceFactory = defaultCaptureSourceFactory): Promise<void> => {
			if (sessionId === null || stateRef.current !== "idle") {
				return;
			}
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
				// R34a: a refused frame is never swallowed. The send limit and a dead transport both reject, and both mean
				// speech the node did not receive — so capture stops loudly instead of leaving a hole in the transcript.
				pushFrame(frame.channel, frame.pcm).catch((pushError: unknown) => {
					abort(toErrorCode(pushError)).catch(() => undefined);
				});
			};

			// R39a: the display picker is invoked HERE, synchronously, before this function's first await. Everything
			// else — the endpoint, the microphone prompt, `addModule` — can outlive the transient user activation the
			// Screen Capture specification requires at the moment `getDisplayMedia` is called.
			let displayStarted: Promise<void> | null = null;
			if (request.kind !== "microphone") {
				const display = createSource("systemAudio", systemChannel(), undefined);
				acquired.push(display);
				displayStarted = display.start(onFrame);
				// Parked until it is awaited below; without this a picker cancelled while `live/start` is in flight is an
				// unhandled rejection.
				displayStarted.catch(() => undefined);
			}

			// The stale path, taken when a teardown ran while this start was awaiting something. It touches no state:
			// the component it belonged to is gone, or a later start already owns the state machine.
			const abandon = async (endTheSession: boolean): Promise<void> => {
				forwardingRef.current = false;
				if (displayStarted !== null) {
					await Promise.allSettled([displayStarted]);
				}
				await Promise.allSettled(acquired.map((source) => source.stop()));
				if (endTheSession) {
					await Promise.allSettled([endSession()]);
				}
			};

			try {
				await startLive.mutateAsync(sessionId);
				if (startGenerationRef.current !== generation) {
					// The teardown ran while `live/start` was in flight, so it saw no open session and ended nothing.
					// Closing it here is a no-op once the transport has gone with the unmount, which is what the
					// node's abandonment grace exists for; what matters is that no source survives this return.
					await abandon(true);
					return;
				}
				sessionOpenRef.current = true;

				if (request.kind !== "systemAudio") {
					const microphone = createSource("microphone", microphoneChannel(request), request.deviceId);
					acquired.push(microphone);
					await microphone.start(onFrame);
					// Past the assignment above, a teardown had the session registered and has already ended it.
					if (startGenerationRef.current !== generation) {
						await abandon(false);
						return;
					}
				}
				if (displayStarted !== null) {
					await displayStarted;
					if (startGenerationRef.current !== generation) {
						await abandon(false);
						return;
					}
				}
				forwardingRef.current = true;
				setPhase("capturing");
			} catch (startError: unknown) {
				// Failure disposes everything: a cancelled picker must not leave a hot microphone, and a microphone that
				// failed must not leave a screen share running. The display promise is settled first so its own stream is
				// registered before the sources are stopped.
				if (displayStarted !== null) {
					await Promise.allSettled([displayStarted]);
				}
				await teardown();
				setError(toErrorCode(startError));
				setPhase("idle");
			}
		},
		[abort, endSession, pushFrame, sessionId, setPhase, startLive, teardown],
	);

	const stop = useCallback(async (): Promise<void> => {
		if (stateRef.current === "idle" || stateRef.current === "stopping") {
			return;
		}
		setPhase("stopping");
		await teardown();
		setPhase("idle");
	}, [setPhase, teardown]);

	// Leaving the page stops capture. A `MediaStream` is browser-global: without this the microphone stays hot and the
	// screen-share indicator stays lit after a navigation, and the node's abandonment grace would be the only thing that
	// ever closed the session. Unmount-only, through a ref, so a new `endSession` identity does not tear down a live
	// capture mid-session.
	const teardownRef = useRef(teardown);
	teardownRef.current = teardown;
	useEffect(
		() => () => {
			teardownRef.current().catch(() => undefined);
		},
		[],
	);

	// R34a: `Overloaded` is the node saying it cannot keep up. Capture stops and the operator is told, because the
	// alternative is a microphone that stays on producing audio nothing is transcribing.
	const status = live?.status ?? "";
	useEffect(() => {
		if (status !== "Overloaded" || stateRef.current === "idle" || stateRef.current === "stopping") {
			return;
		}
		abort("overloaded").catch(() => undefined);
	}, [abort, status]);

	return { state, error, replayStalled: replayStalled !== null, connected, subscribeFailed, start, stop };
}
