import { HubConnectionState } from "@microsoft/signalr";
import { useCallback, useEffect, useRef, useState } from "react";

import { ApiError } from "@/core/api/errors/ApiError";
import { startDictationSession } from "@/core/api/generated";
import { acquireHubConnection } from "@/core/api/signalr/SharedHubConnection";
import { CaptureError, type CaptureErrorCode, type CaptureSource, encodePcmFrame } from "@/core/audio/capture/CaptureSource";

// The mic button on the command page: one ephemeral dictation session (`POST transcription/dictation-sessions` with the
// chosen language, no rows stored), microphone frames pushed over `transcription/hub`, committed segments handed to the
// page, partials shown and never kept. Nothing here sends: the user reads the text and presses Enter.
//
// Much smaller than the transcription page's `useTranscriptionHub` on purpose: a dictation is seconds long and
// throwaway, so there is no replay drain, no reconnect re-subscribe and no transcript cache. A dropped transport ends
// the dictation with an inline error instead.

const HUB_PATH = "transcription/hub";
/** The `TranscriptChannel` wire value for a single microphone (`Mono`). */
const MONO_CHANNEL = 0;
/**
 * How long Stop waits for the node to drain and report the terminal status before it lets go anyway. The last committed
 * segment arrives inside this window, so it must outlast one whisper pass over a short utterance.
 */
const DICTATION_STOP_GRACE_MS = 15_000;

export type DictationState = "idle" | "starting" | "listening" | "stopping";

/** `auto` lets whisper detect the language per window, which flips short English commands to German for some speakers. */
export type DictationLanguage = "auto" | "en" | "de";

/** Pushes the hub sends every session subscriber that a dictation has no use for; listened to so SignalR does not warn. */
const IGNORED_EVENTS = ["transcriptionCatchUpProgress", "transcriptionAdmissionClosed", "transcriptionSourceQuiet"] as const;
const ignore = (): void => undefined;

/** `transcription-disabled` is the 404 of a node with transcription off; `runtime` is the 400 whisper-server refusal. */
export type DictationErrorCode = CaptureErrorCode | "transcription-disabled" | "runtime" | "hub";

export interface DictationError {
	readonly code: DictationErrorCode;
	/** The node's sanitized runtime message, for `runtime` only. */
	readonly detail?: string;
}

interface SessionPush {
	readonly sessionId: string;
	readonly text?: string;
	readonly startMs?: number;
}

function isSessionPush(payload: unknown, sessionId: string): payload is SessionPush {
	const candidate = payload as Partial<SessionPush> | null;
	return typeof candidate?.sessionId === "string" && candidate.sessionId.toLowerCase() === sessionId.toLowerCase();
}

/** Ends a session the node created for a page that is already gone; best effort, the node reaps an unattached one anyway. */
async function endOrphanSession(sessionId: string): Promise<void> {
	const hub = acquireHubConnection(HUB_PATH);
	try {
		await hub.whenStarted;
		if (hub.connection.state === HubConnectionState.Connected) {
			await hub.connection.invoke("EndSession", sessionId);
		}
	} catch {
		// Nothing to show: the page is gone and the node's attachment deadline ends the session on its own.
	} finally {
		hub.release();
	}
}

function toStartError(error: unknown): DictationError {
	if (error instanceof ApiError && error.statusCode === 404) {
		return { code: "transcription-disabled" };
	}
	if (error instanceof ApiError && error.statusCode === 400) {
		return { code: "runtime", detail: error.message };
	}
	return { code: "hub" };
}

interface ActiveDictation {
	source: CaptureSource | null;
	stopping: boolean;
	readonly end: () => Promise<void>;
	readonly finish: () => void;
}

/**
 * @param onCommitted receives each committed segment's text and when it was said: the session's start on the node's
 *   clock plus the segment's offset, which is what the intent endpoint's `utteredAt` means.
 */
export function useCommandDictation(onCommitted: (text: string, saidAt: Date) => void): {
	readonly state: DictationState;
	readonly partial: string;
	readonly error: DictationError | null;
	start(language: DictationLanguage): Promise<void>;
	stop(): Promise<void>;
} {
	const [state, setState] = useState<DictationState>("idle");
	const [partial, setPartial] = useState("");
	const [error, setError] = useState<DictationError | null>(null);
	const activeRef = useRef<ActiveDictation | null>(null);
	// Set by the unmount cleanup so a start that is still awaiting the node never opens the microphone afterwards.
	const unmountedRef = useRef(false);
	// Bumped by every dismissal (blur, hidden document, Stop): a start still awaiting the node compares it afterwards
	// and ends the session it got instead of opening the microphone for a window nobody is looking at.
	const dismissalsRef = useRef(0);
	const onCommittedRef = useRef(onCommitted);

	useEffect(() => {
		onCommittedRef.current = onCommitted;
	}, [onCommitted]);

	const stop = useCallback(async (): Promise<void> => {
		dismissalsRef.current += 1;
		const active = activeRef.current;
		if (active === null || active.stopping) {
			return;
		}
		active.stopping = true;
		setState("stopping");
		const source = active.source;
		active.source = null;
		await source?.stop();
		await active.end();
	}, []);

	const start = useCallback(
		async (language: DictationLanguage): Promise<void> => {
			if (activeRef.current !== null) {
				return;
			}
			setError(null);
			setPartial("");
			setState("starting");
			const dismissalsAtStart = dismissalsRef.current;

			let sessionId: string;
			let startedAtUtc: number;
			try {
				const { data } = await startDictationSession({
					body: language === "auto" ? {} : { language },
				});
				sessionId = data.sessionId;
				startedAtUtc = data.startedAtUtc;
			} catch (startError) {
				setError(toStartError(startError));
				setState("idle");
				return;
			}
			if (unmountedRef.current || dismissalsRef.current !== dismissalsAtStart) {
				// The page went away or was dismissed while the node was answering: end the session it created, open nothing.
				await endOrphanSession(sessionId);
				setState("idle");
				return;
			}

			const hub = acquireHubConnection(HUB_PATH);
			const { connection } = hub;
			let graceTimer: ReturnType<typeof setTimeout> | undefined;

			const onSegment = (payload: unknown): void => {
				if (isSessionPush(payload, sessionId) && typeof payload.text === "string" && payload.text.trim().length > 0) {
					setPartial("");
					onCommittedRef.current(payload.text.trim(), new Date(startedAtUtc + (payload.startMs ?? 0)));
				}
			};
			const onPartial = (payload: unknown): void => {
				if (isSessionPush(payload, sessionId) && typeof payload.text === "string") {
					setPartial(payload.text);
				}
			};

			const active: ActiveDictation = {
				source: null,
				stopping: false,
				// The node drains what it buffered, pushes the last segments, then the terminal status that calls `finish`.
				end: async () => {
					graceTimer = setTimeout(() => active.finish(), DICTATION_STOP_GRACE_MS);
					if (connection.state !== HubConnectionState.Connected) {
						active.finish();
						return;
					}
					await connection.invoke("EndSession", sessionId).catch(() => active.finish());
				},
				// Terminal cleanup for every path: a Stop, a start that failed, the node ending the session on its own, or the
				// page unmounting. The microphone is released here too, so a session the node ended never leaves it hot.
				finish: () => {
					// Terminal for this dictation whatever the path: a start still awaiting the hub sees it and opens nothing.
					active.stopping = true;
					clearTimeout(graceTimer);
					const source = active.source;
					active.source = null;
					source?.stop().catch(() => undefined);
					connection.off("transcriptionSegmentCommitted", onSegment);
					connection.off("transcriptionPartialUpdated", onPartial);
					connection.off("transcriptionSessionStatusChanged", onStatus);
					for (const event of IGNORED_EVENTS) {
						connection.off(event, ignore);
					}
					if (activeRef.current === active) {
						activeRef.current = null;
						hub.release();
						setPartial("");
						setState("idle");
					}
				},
			};
			// Every status this push carries is terminal (`Completed | Cancelled | Abandoned | Failed`).
			const onStatus = (payload: unknown): void => {
				if (isSessionPush(payload, sessionId)) {
					active.finish();
				}
			};
			activeRef.current = active;
			connection.on("transcriptionSegmentCommitted", onSegment);
			connection.on("transcriptionPartialUpdated", onPartial);
			connection.on("transcriptionSessionStatusChanged", onStatus);
			for (const event of IGNORED_EVENTS) {
				connection.on(event, ignore);
			}

			try {
				await hub.whenStarted;
				if (connection.state !== HubConnectionState.Connected) {
					throw new Error("the transcription hub is not connected");
				}
				await connection.invoke("SubscribeSession", sessionId, 0);
				// A Stop, an unmount or the node ending the session during the awaits above has already finished this
				// dictation: open no microphone for it.
				if (active.stopping || activeRef.current !== active) {
					return;
				}
				// Loaded on first use: the worklet and the audio graph are not part of the page's own chunk.
				const { MicrophoneCaptureSource } = await import("@/core/audio/capture/MicrophoneCaptureSource");
				if (active.stopping || activeRef.current !== active) {
					return;
				}
				const source = new MicrophoneCaptureSource("mono");
				active.source = source;
				await source.start((frame) => {
					connection.invoke("PushAudioFrame", sessionId, MONO_CHANNEL, encodePcmFrame(frame.pcm)).catch(() => {
						setError({ code: "disconnected" });
						stop().catch(() => undefined);
					});
				});
				if (!active.stopping) {
					setState("listening");
				}
			} catch (captureError) {
				setError(captureError instanceof CaptureError ? { code: captureError.code } : { code: "hub" });
				active.stopping = true;
				const source = active.source;
				active.source = null;
				await source?.stop();
				await active.end();
			}
		},
		[stop],
	);

	// A dismissed command window must not keep recording. The desktop overlay only hides on Escape or focus loss and
	// cannot reach into the page (no bridge), so the page ends the dictation itself when its window loses focus or is
	// hidden; a browser tab gets the same conservative rule.
	useEffect(() => {
		const onDismissed = (): void => {
			stop().catch(() => undefined);
		};
		const onVisibility = (): void => {
			if (document.visibilityState === "hidden") {
				onDismissed();
			}
		};
		window.addEventListener("blur", onDismissed);
		document.addEventListener("visibilitychange", onVisibility);
		return () => {
			window.removeEventListener("blur", onDismissed);
			document.removeEventListener("visibilitychange", onVisibility);
		};
	}, [stop]);

	// Leaving the page must not leave the microphone hot or the session open, whether the dictation is listening or
	// still starting.
	useEffect(() => {
		unmountedRef.current = false;
		return () => {
			unmountedRef.current = true;
			const active = activeRef.current;
			if (active !== null) {
				active.stopping = true;
				const source = active.source;
				active.source = null;
				source?.stop().catch(() => undefined);
				active.end().finally(() => active.finish());
			}
		};
	}, []);

	return { state, partial, error, start, stop };
}
