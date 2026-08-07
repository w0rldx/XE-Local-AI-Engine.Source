import { createContext, useContext } from "react";

import type { VoiceRuntime } from "@/core/runtime/VoiceRuntime";

export interface VoiceRuntimeContextValue {
	readonly enabled: boolean;
	readonly defaultVoiceProfile: string | undefined;
	readonly runtime: VoiceRuntime | undefined;
	readonly playingMessageId: string | undefined;
	readonly playMessage: (messageId: string, text: string) => Promise<void>;
	readonly previewVoice: (voiceId: string) => Promise<void>;
	readonly stopPlayback: () => void;
}

export const INERT_VOICE_RUNTIME_CONTEXT: VoiceRuntimeContextValue = {
	enabled: false,
	defaultVoiceProfile: undefined,
	runtime: undefined,
	playingMessageId: undefined,
	playMessage: () => Promise.resolve(),
	previewVoice: () => Promise.resolve(),
	stopPlayback: () => undefined,
};

export const VoiceRuntimeContext = createContext<VoiceRuntimeContextValue>(INERT_VOICE_RUNTIME_CONTEXT);

export function useVoiceRuntime(): VoiceRuntimeContextValue {
	return useContext(VoiceRuntimeContext);
}
