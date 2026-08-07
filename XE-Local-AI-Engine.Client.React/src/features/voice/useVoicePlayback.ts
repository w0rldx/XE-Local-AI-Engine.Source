import { useCallback, useRef } from "react";

import { SentenceBuffer } from "@/core/runtime/SentenceBuffer";
import { findVoiceById } from "@/core/runtime/VoiceManifest";
import type { ChatStreamingState } from "@/features/chat/models/ChatModels";
import { detectAnswerLanguage } from "@/features/voice/DetectAnswerLanguage";
import { toSpeakableText } from "@/features/voice/SpeakableText";
import { useVoicePreferencesStore } from "@/features/voice/VoicePreferencesStore";
import { useVoiceRuntime } from "@/features/voice/VoiceRuntimeContext";

// A fence delimiter LINE per CommonMark: up to 3 leading spaces/tabs, then 3+ backticks OR 3+ tildes. Line-anchored
// (not a bare ```/~~~ substring match) so an inline code span or a run of backticks mid-sentence is never mistaken
// for a fence; a run of 4+ delimiter characters still counts as exactly ONE fence event, matching how CommonMark
// treats it (a single open/close marker), not one event per extra character.
const FENCE_DELIMITER_LINE = /^[ \t]{0,3}(?:`{3,}|~{3,})/gm;

function countFenceDelimiters(text: string): number {
	return text.match(FENCE_DELIMITER_LINE)?.length ?? 0;
}

function isOddFenceCount(text: string): boolean {
	return countFenceDelimiters(text) % 2 === 1;
}

// The chat-stream voice tap. It is intentionally DECOUPLED from the stream reducer: Chat.tsx
// calls `onAnswerProgress` with the SAME `ChatStreamingState` it just handed to `setStreamingMessage`, and this hook
// diffs the ANSWER text (`streamingMessage.content` — never reasoning/tool parts) to feed a
// SentenceBuffer. Whole sentences (never tokens) are enqueued to the runtime. Each sentence is converted from
// markdown to speakable prose (`toSpeakableText`) right before enqueueing — never earlier, since an in-progress
// sentence's markdown isn't syntactically closed yet, and fenced code blocks are skipped wholesale via
// `insideFenceRef` (see its declaration below) so a code block spanning several sentences is never read aloud line by
// line. Barge-in (`onTurnStart`) stops playback + resets all per-turn state on every new send / regenerate / cancel.
// Engages only when the user has voice + autoplay on and a runtime exists; otherwise every call is a cheap no-op.

export interface VoicePlaybackTap {
	/** Barge-in: stop any current playback and reset the per-turn sentence buffer. Call on send/regenerate/cancel. */
	readonly onTurnStart: () => void;
	/** Feed the latest streaming state; diffs + flushes answer sentences, and flushes the remainder on terminal. */
	readonly onAnswerProgress: (streaming: ChatStreamingState | undefined) => void;
}

export function useVoicePlayback(): VoicePlaybackTap {
	const { runtime, manifest, stopPlayback } = useVoiceRuntime();

	// Lazy-initialized once at mount: useRef ignores all but its first argument, so passing `new SentenceBuffer()`
	// directly would rebuild it on every render and throw the result away. The cast keeps `.current` typed as
	// non-nullable so downstream reads don't need optional chaining.
	const bufferRef = useRef<SentenceBuffer>(undefined as unknown as SentenceBuffer);
	if (!bufferRef.current) {
		bufferRef.current = new SentenceBuffer();
	}
	const seenLengthRef = useRef(0);
	const messageIdRef = useRef<string | undefined>(undefined);
	// The message id whose remainder has already been flushed; guards the terminal flush against re-running (Chat.tsx
	// emits the terminal state both in-loop and once more after the loop), which would otherwise re-speak the answer.
	const completedMessageIdRef = useRef<string | undefined>(undefined);
	// Whether the accumulated answer text is CURRENTLY mid an unclosed fence (``` or ~~~). A fenced code block
	// routinely spans several SentenceBuffer segments (code has many newlines, each a sentence boundary), so a single
	// segment rarely contains a balanced fence pair for SentenceBuffer's own per-segment code stripping to catch.
	// Tracking parity on the full accumulated text (which only ever grows, so re-scanning it never changes an earlier
	// verdict) lets every segment produced while a fence is open be skipped wholesale, instead of reading source code
	// aloud line by line.
	const insideFenceRef = useRef(false);

	const enqueueSentence = useCallback(
		(sentence: string, fullText: string): void => {
			if (!runtime) {
				return;
			}

			// Markdown is stripped PER SENTENCE, right before speaking it — never on the accumulated stream text before
			// diffing. Stripping the accumulated text isn't monotonic: an unterminated `**word` that a later delta closes
			// changes what earlier characters mean, which would retroactively invalidate the length-based diff cursor in
			// `onAnswerProgress`. A finished sentence's markdown is already syntactically closed, so stripping it in
			// isolation is stable.
			const speakable = toSpeakableText(sentence);
			if (speakable.length === 0) {
				return;
			}

			const prefs = useVoicePreferencesStore.getState();
			const voiceId = prefs.voiceProfile || manifest?.defaultVoiceId || undefined;
			// "Selected voice always wins": drive the engine/ladder from the SELECTED voice's OWN language so
			// auto-play matches the node-settings preview exactly — never re-route an English answer to Kokoro when the
			// user picked a German voice. detectAnswerLanguage stays only as the fallback when no voice resolves (no
			// selection AND no manifest default), where there is no chosen language to honor.
			const selectedVoice = manifest ? findVoiceById(manifest, voiceId) : undefined;
			const language = selectedVoice?.language ?? detectAnswerLanguage(fullText);
			// Fire-and-forget: synthesis must not block the hot stream loop (decoupling). Errors degrade via the
			// runtime's own fallback ladder + onError; swallow here so a TTS hiccup never breaks chat rendering.
			runtime.enqueue(speakable, { voiceId, rate: prefs.speakingRate, language }).catch(() => undefined);
		},
		[runtime, manifest],
	);

	const onTurnStart = useCallback((): void => {
		stopPlayback();
		bufferRef.current.reset();
		seenLengthRef.current = 0;
		messageIdRef.current = undefined;
		completedMessageIdRef.current = undefined;
		insideFenceRef.current = false;
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
				insideFenceRef.current = false;
			}

			const content = streaming.content;
			if (content.length > seenLengthRef.current) {
				const delta = content.slice(seenLengthRef.current);
				seenLengthRef.current = content.length;

				// Fence parity BEFORE vs. AFTER this delta. `content` only ever grows, so both counts are stable once
				// computed — re-scanning it as later deltas arrive never changes what an earlier count meant.
				const wasInsideFence = insideFenceRef.current;
				const isInsideFenceNow = isOddFenceCount(content);
				insideFenceRef.current = isInsideFenceNow;
				// A COMPLETE, balanced fence (open + close) can arrive within a single coalesced delta — parity alone
				// stays even the whole time, so it would slip past the wasInsideFence/isInsideFenceNow check above and
				// speak the code inside. Catch that case by also suppressing whenever the delta itself contains any
				// fence delimiter line, regardless of parity.
				const deltaHasFenceDelimiter = countFenceDelimiters(delta) > 0;

				const sentences = bufferRef.current.push(delta);
				// A delta that starts inside an open fence, leaves one open, or contains a fence delimiter at all may
				// contain code; skip every sentence it produced rather than risk reading a source line aloud. This is
				// batch-level, not per-sentence, granularity (a single delta rarely mixes fence markers with unrelated
				// prose) — see file header comment.
				if (!wasInsideFence && !isInsideFenceNow && !deltaHasFenceDelimiter) {
					for (const sentence of sentences) {
						enqueueSentence(sentence, content);
					}
				}
			}

			// Terminal event for this turn (stream went inactive): flush the trailing remainder exactly once.
			if (!streaming.isActive) {
				const remainder = bufferRef.current.flush();
				// An unterminated fence at end-of-stream is malformed markdown; never speak it.
				if (remainder && !insideFenceRef.current) {
					enqueueSentence(remainder, content);
				}
				completedMessageIdRef.current = streaming.messageId;
			}
		},
		[runtime, enqueueSentence],
	);

	return { onTurnStart, onAnswerProgress };
}
