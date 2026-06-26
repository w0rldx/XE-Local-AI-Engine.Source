// React context + hook for the app-root client AI (voice) runtime. Kept in a non-component module (repo convention,
// mirrors ConfirmContext/useConfirm) so the provider .tsx exports only its component. The ClientAiRuntimeProvider
// fills this context; every voice UI leaf reads it via useVoiceRuntime and stays decoupled from the runtime wiring.

import { createContext, useContext } from "react";

import type { VoiceCapabilities } from "@/core/runtime/CapabilityDetector";
import type { ModelDownloadError, ModelDownloadProgress } from "@/core/runtime/ModelCache";
import type { VoiceManifest } from "@/core/runtime/VoiceManifest";
import type { VoiceRuntime, VoiceRuntimeError } from "@/core/runtime/VoiceRuntime";
import type { AnswerLanguage } from "@/features/voice/DetectAnswerLanguage";

export interface VoiceRuntimeContextValue {
	/** The adapted backend manifest (operator gate + catalog), or undefined when disabled/unauthorized. */
	readonly manifest: VoiceManifest | undefined;
	/** True when voice is dev-gated on AND the manifest enables it (the surface gate for all voice UI). */
	readonly enabled: boolean;
	/** Probed browser capabilities; undefined until the one-shot probe resolves. */
	readonly capabilities: VoiceCapabilities | undefined;
	/** The single long-lived runtime, or undefined when voice is off / unsupported. */
	readonly runtime: VoiceRuntime | undefined;
	/** True while the owned AudioContext is still suspended (no user gesture yet) — drives the "tap to enable" hint. */
	readonly audioSuspended: boolean;
	/** Resumes the AudioContext (satisfies the autoplay-gesture unlock). Safe to call repeatedly. */
	readonly resumeAudio: () => Promise<void>;
	/** Latest model-download progress, or undefined when none is active / dismissed. */
	readonly downloadProgress: ModelDownloadProgress | undefined;
	/** Latest model-download error, or undefined. */
	readonly downloadError: ModelDownloadError | undefined;
	/** Dismisses the current download progress/error notice. */
	readonly dismissDownloadNotice: () => void;
	/** Most recent provider/runtime error (drives the capability/fallback notice). */
	readonly lastError: VoiceRuntimeError | undefined;
	/** The message id whose answer is currently being played via a per-message Play button, or undefined. */
	readonly playingMessageId: string | undefined;
	/** Barge-in then speak one message's whole (sanitized) answer; tags the playing message for the Play/Stop UI. */
	readonly playMessage: (messageId: string, text: string, language: AnswerLanguage) => Promise<void>;
	/** Halts playback and clears the playing-message tag (barge-in). */
	readonly stopPlayback: () => void;
}

/** The inert default — used when no provider is mounted (e.g. unit tests render a leaf in isolation). */
export const INERT_VOICE_RUNTIME_CONTEXT: VoiceRuntimeContextValue = {
	manifest: undefined,
	enabled: false,
	capabilities: undefined,
	runtime: undefined,
	audioSuspended: true,
	resumeAudio: () => Promise.resolve(),
	downloadProgress: undefined,
	downloadError: undefined,
	dismissDownloadNotice: () => undefined,
	lastError: undefined,
	playingMessageId: undefined,
	playMessage: () => Promise.resolve(),
	stopPlayback: () => undefined,
};

export const VoiceRuntimeContext = createContext<VoiceRuntimeContextValue>(INERT_VOICE_RUNTIME_CONTEXT);

/** Reads the app-root voice runtime context. Returns an inert value when no provider is mounted. */
export function useVoiceRuntime(): VoiceRuntimeContextValue {
	return useContext(VoiceRuntimeContext);
}
