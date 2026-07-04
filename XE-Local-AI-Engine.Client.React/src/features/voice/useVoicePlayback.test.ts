// @vitest-environment jsdom

import { renderHook } from "@testing-library/react";
import { createElement, type ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { VoiceRuntime } from "@/core/runtime/VoiceRuntime";
import type { ChatStreamingState } from "@/features/chat/models/ChatModels";
import { useVoicePlayback } from "@/features/voice/useVoicePlayback";
import { useVoicePreferencesStore } from "@/features/voice/VoicePreferencesStore";
import { INERT_VOICE_RUNTIME_CONTEXT, VoiceRuntimeContext } from "@/features/voice/VoiceRuntimeContext";

function baseStreaming(overrides: Partial<ChatStreamingState>): ChatStreamingState {
	return {
		conversationId: "conv-1",
		messageId: "msg-1",
		content: "",
		isActive: true,
		...overrides,
	};
}

describe("useVoicePlayback", () => {
	let enqueue: ReturnType<typeof vi.fn>;

	beforeEach(() => {
		enqueue = vi.fn().mockResolvedValue(undefined);
		useVoicePreferencesStore.setState({ voiceEnabled: true, autoPlayAssistant: true, voiceProfile: "" });
	});

	afterEach(() => {
		useVoicePreferencesStore.setState({ voiceEnabled: false, autoPlayAssistant: false, voiceProfile: "" });
	});

	function renderVoicePlayback() {
		const runtime = { enqueue } as unknown as VoiceRuntime;
		const wrapper = ({ children }: { children: ReactNode }) =>
			createElement(VoiceRuntimeContext.Provider, { value: { ...INERT_VOICE_RUNTIME_CONTEXT, runtime } }, children);
		return renderHook(() => useVoicePlayback(), { wrapper });
	}

	it("strips markdown from a completed sentence before enqueueing", () => {
		const { result } = renderVoicePlayback();

		result.current.onAnswerProgress(baseStreaming({ content: "**Hello** world. " }));

		expect(enqueue).toHaveBeenCalledTimes(1);
		expect(enqueue.mock.calls[0]?.[0]).toBe("Hello world.");
	});

	it("skips sentences produced while a fenced code block is still open", () => {
		const { result } = renderVoicePlayback();

		// First delta: plain prose, fence not yet opened — spoken normally.
		result.current.onAnswerProgress(baseStreaming({ content: "Before.\n" }));
		expect(enqueue).toHaveBeenCalledTimes(1);
		expect(enqueue.mock.calls[0]?.[0]).toBe("Before.");

		enqueue.mockClear();

		// Next delta opens the fence and streams a code line — the fence-opening delta itself must stay silent.
		result.current.onAnswerProgress(baseStreaming({ content: "Before.\n```js\nconst x = 1;\n" }));
		expect(enqueue).not.toHaveBeenCalled();

		// Still inside the fence: another code line arrives, must stay silent.
		result.current.onAnswerProgress(baseStreaming({ content: "Before.\n```js\nconst x = 1;\nconsole.log(x);\n" }));
		expect(enqueue).not.toHaveBeenCalled();

		// The closing delimiter arrives on its own delta — the batch that CLOSES the fence stays silent too (the
		// coarser, delta-level granularity documented in useVoicePlayback.ts), but it carries no prose to lose.
		result.current.onAnswerProgress(baseStreaming({ content: "Before.\n```js\nconst x = 1;\nconsole.log(x);\n```\n" }));
		expect(enqueue).not.toHaveBeenCalled();

		// Prose resuming in its OWN delta, now that the fence is fully closed, is spoken normally.
		result.current.onAnswerProgress(
			baseStreaming({ content: "Before.\n```js\nconst x = 1;\nconsole.log(x);\n```\nAfter.", isActive: false }),
		);

		const spoken = enqueue.mock.calls.map((call) => call[0]);
		expect(spoken.some((text) => typeof text === "string" && text.includes("const x"))).toBe(false);
		expect(spoken.some((text) => typeof text === "string" && text.includes("console.log"))).toBe(false);
		expect(spoken.some((text) => typeof text === "string" && text.includes("After"))).toBe(true);
	});

	it("skips an empty-after-strip sentence (e.g. pure formatting markers)", () => {
		const { result } = renderVoicePlayback();

		result.current.onAnswerProgress(baseStreaming({ content: "``` ", isActive: false }));

		expect(enqueue).not.toHaveBeenCalled();
	});

	it("suppresses a complete fenced block that arrives entirely within one delta", () => {
		const { result } = renderVoicePlayback();

		result.current.onAnswerProgress(baseStreaming({ content: "Before.\n" }));
		expect(enqueue).toHaveBeenCalledTimes(1);
		expect(enqueue.mock.calls[0]?.[0]).toBe("Before.");
		enqueue.mockClear();

		// The whole fenced block — open, code, close — lands in ONE delta (a coalesced/buffered chunk). Fence parity
		// on the accumulated content stays EVEN across this delta (open + close cancel out), so the
		// wasInsideFence/isInsideFenceNow parity check alone would miss it; the delta-level fence-delimiter check
		// (deltaHasFenceDelimiter) catches it instead.
		result.current.onAnswerProgress(baseStreaming({ content: "Before.\n```js\nconst x = 1;\nconsole.log(x);\n```\n" }));
		expect(enqueue).not.toHaveBeenCalled();

		result.current.onAnswerProgress(
			baseStreaming({ content: "Before.\n```js\nconst x = 1;\nconsole.log(x);\n```\nAfter.", isActive: false }),
		);

		const spoken = enqueue.mock.calls.map((call) => call[0]);
		expect(spoken.some((text) => typeof text === "string" && text.includes("const x"))).toBe(false);
		expect(spoken.some((text) => typeof text === "string" && text.includes("console.log"))).toBe(false);
		expect(spoken.some((text) => typeof text === "string" && text.includes("After"))).toBe(true);
	});

	it("skips code inside a tilde (~~~) fenced block just like a backtick fence", () => {
		const { result } = renderVoicePlayback();

		result.current.onAnswerProgress(baseStreaming({ content: "Before.\n" }));
		expect(enqueue).toHaveBeenCalledTimes(1);
		enqueue.mockClear();

		result.current.onAnswerProgress(baseStreaming({ content: "Before.\n~~~js\nconst x = 1;\n" }));
		expect(enqueue).not.toHaveBeenCalled();

		result.current.onAnswerProgress(baseStreaming({ content: "Before.\n~~~js\nconst x = 1;\nconsole.log(x);\n" }));
		expect(enqueue).not.toHaveBeenCalled();

		result.current.onAnswerProgress(baseStreaming({ content: "Before.\n~~~js\nconst x = 1;\nconsole.log(x);\n~~~\n" }));
		expect(enqueue).not.toHaveBeenCalled();

		result.current.onAnswerProgress(
			baseStreaming({ content: "Before.\n~~~js\nconst x = 1;\nconsole.log(x);\n~~~\nAfter.", isActive: false }),
		);

		const spoken = enqueue.mock.calls.map((call) => call[0]);
		expect(spoken.some((text) => typeof text === "string" && text.includes("const x"))).toBe(false);
		expect(spoken.some((text) => typeof text === "string" && text.includes("console.log"))).toBe(false);
		expect(spoken.some((text) => typeof text === "string" && text.includes("After"))).toBe(true);
	});

	it("keeps speaking normally across multiple prose-only deltas with no fence involved", () => {
		const { result } = renderVoicePlayback();

		result.current.onAnswerProgress(baseStreaming({ content: "First sentence. " }));
		result.current.onAnswerProgress(baseStreaming({ content: "First sentence. Second sentence.", isActive: false }));

		const spoken = enqueue.mock.calls.map((call) => call[0]);
		expect(spoken).toEqual(["First sentence.", "Second sentence."]);
	});
});
