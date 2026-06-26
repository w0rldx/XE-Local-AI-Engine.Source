import { useCallback, useRef } from "react";

import { SentenceBuffer } from "@/core/runtime/SentenceBuffer";
import type { ChatStreamingState } from "@/features/chat/models/ChatModels";
import { detectAnswerLanguage } from "@/features/voice/DetectAnswerLanguage";
import { useVoicePreferencesStore } from "@/features/voice/VoicePreferencesStore";
import { useVoiceRuntime } from "@/features/voice/VoiceRuntimeContext";

// The chat-stream voice tap (plan §7.3, R-A MEDIUM-1). It is intentionally DECOUPLED from the stream reducer: Chat.tsx
// calls `onAnswerProgress` with the SAME `ChatStreamingState` it just handed to `setStreamingMessage`, and this hook
// diffs the ANSWER text (`streamingMessage.content` — never reasoning/tool parts, invariant §3.7) to feed a
// SentenceBuffer. Whole sentences (never tokens, invariant §3.3) are enqueued to the runtime. Barge-in (`onTurnStart`)
// stops playback + resets the buffer on every new send / regenerate / cancel. Engages only when the user has voice +
// autoplay on and a runtime exists; otherwise every call is a cheap no-op.

export interface VoicePlaybackTap {
	/** Barge-in: stop any current playback and reset the per-turn sentence buffer. Call on send/regenerate/cancel. */
	readonly onTurnStart: () => void;
	/** Feed the latest streaming state; diffs + flushes answer sentences, and flushes the remainder on terminal. */
	readonly onAnswerProgress: (streaming: ChatStreamingState | undefined) => void;
}

export function useVoicePlayback(): VoicePlaybackTap {
	const { runtime, manifest, stopPlayback } = useVoiceRuntime();

	const bufferRef = useRef(new SentenceBuffer());
	const seenLengthRef = useRef(0);
	const messageIdRef = useRef<string | undefined>(undefined);
	// The message id whose remainder has already been flushed; guards the terminal flush against re-running (Chat.tsx
	// emits the terminal state both in-loop and once more after the loop), which would otherwise re-speak the answer.
	const completedMessageIdRef = useRef<string | undefined>(undefined);

	const enqueueSentence = useCallback(
		(sentence: string, fullText: string): void => {
			if (!runtime) {
				return;
			}

			const prefs = useVoicePreferencesStore.getState();
			const voiceId = prefs.voiceProfile || manifest?.defaultVoiceId || undefined;
			const language = detectAnswerLanguage(fullText);
			// Fire-and-forget: synthesis must not block the hot stream loop (decoupling). Errors degrade via the
			// runtime's own fallback ladder + onError; swallow here so a TTS hiccup never breaks chat rendering.
			runtime.enqueue(sentence, { voiceId, rate: prefs.speakingRate, language }).catch(() => undefined);
		},
		[runtime, manifest?.defaultVoiceId],
	);

	const onTurnStart = useCallback((): void => {
		stopPlayback();
		bufferRef.current.reset();
		seenLengthRef.current = 0;
		messageIdRef.current = undefined;
		completedMessageIdRef.current = undefined;
	}, [stopPlayback]);

	const onAnswerProgress = useCallback(
		(streaming: ChatStreamingState | undefined): void => {
			const prefs = useVoicePreferencesStore.getState();
			if (!runtime || !prefs.voiceEnabled || !prefs.autoPlayAssistant || !streaming) {
				return;
			}

			// Already finished this turn — the terminal flush is idempotent, so ignore the duplicate terminal emit.
			if (streaming.messageId !== "" && streaming.messageId === completedMessageIdRef.current) {
				return;
			}

			// A new streaming turn resets the diff cursor (the reducer reuses `content` per message id).
			if (streaming.messageId !== messageIdRef.current) {
				messageIdRef.current = streaming.messageId;
				bufferRef.current.reset();
				seenLengthRef.current = 0;
			}

			const content = streaming.content;
			if (content.length > seenLengthRef.current) {
				const delta = content.slice(seenLengthRef.current);
				seenLengthRef.current = content.length;
				for (const sentence of bufferRef.current.push(delta)) {
					enqueueSentence(sentence, content);
				}
			}

			// Terminal event for this turn (stream went inactive): flush the trailing remainder exactly once.
			if (!streaming.isActive) {
				const remainder = bufferRef.current.flush();
				if (remainder) {
					enqueueSentence(remainder, content);
				}
				completedMessageIdRef.current = streaming.messageId;
			}
		},
		[runtime, enqueueSentence],
	);

	return { onTurnStart, onAnswerProgress };
}
