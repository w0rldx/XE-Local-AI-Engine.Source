// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { ChatMessageModel } from "@/features/chat/models/ChatModels";
import { VoiceMessagePlayButton } from "@/features/voice/components/VoiceMessagePlayButton";
import { useVoicePreferencesStore } from "@/features/voice/VoicePreferencesStore";
import type { VoiceRuntimeContextValue } from "@/features/voice/VoiceRuntimeContext";

// Controllable runtime context: the component reads it via useVoiceRuntime, which the mock below returns.
let mockContext: VoiceRuntimeContextValue;
const playMessage = vi.fn(() => Promise.resolve());
const stopPlayback = vi.fn();

vi.mock("@/features/voice/VoiceRuntimeContext", () => ({
	useVoiceRuntime: (): VoiceRuntimeContextValue => mockContext,
}));

function baseContext(overrides: Partial<VoiceRuntimeContextValue> = {}): VoiceRuntimeContextValue {
	return {
		enabled: true,
		defaultVoiceProfile: "af_heart",
		runtime: undefined,
		playingMessageId: undefined,
		playMessage,
		previewVoice: () => Promise.resolve(),
		stopPlayback,
		...overrides,
	};
}

function makeMessage(overrides: Partial<ChatMessageModel> = {}): ChatMessageModel {
	return {
		id: "msg-1",
		conversationId: "conv-1",
		role: "assistant",
		content: "Hello there. This is the answer.",
		status: "completed",
		createdAt: new Date().toISOString(),
		sortOrder: 0,
		...overrides,
	};
}

function renderButton(ui: ReactElement) {
	return render(<MantineProvider>{ui}</MantineProvider>);
}

describe("VoiceMessagePlayButton", () => {
	beforeEach(() => {
		playMessage.mockClear();
		stopPlayback.mockClear();
		mockContext = baseContext();
		useVoicePreferencesStore.getState().actions.setVoiceEnabled(true);
		Object.defineProperty(window, "matchMedia", {
			writable: true,
			value: vi.fn().mockImplementation((query: string) => ({
				matches: false,
				media: query,
				onchange: null,
				addEventListener: vi.fn(),
				removeEventListener: vi.fn(),
				dispatchEvent: vi.fn(),
			})),
		});
	});

	afterEach(() => {
		cleanup();
		useVoicePreferencesStore.getState().actions.setVoiceEnabled(false);
	});

	it("renders nothing when the voice surface is disabled", () => {
		mockContext = baseContext({ enabled: false });
		renderButton(<VoiceMessagePlayButton message={makeMessage()} />);
		expect(screen.queryByTestId("voice-message-play-msg-1")).toBeNull();
	});

	it("renders nothing when the user has voice turned off", () => {
		useVoicePreferencesStore.getState().actions.setVoiceEnabled(false);
		renderButton(<VoiceMessagePlayButton message={makeMessage()} />);
		expect(screen.queryByTestId("voice-message-play-msg-1")).toBeNull();
	});

	it("renders nothing for a user message or empty content", () => {
		const { rerender } = renderButton(<VoiceMessagePlayButton message={makeMessage({ role: "user" })} />);
		expect(screen.queryByTestId("voice-message-play-msg-1")).toBeNull();
		rerender(
			<MantineProvider>
				<VoiceMessagePlayButton message={makeMessage({ content: "   " })} />
			</MantineProvider>,
		);
		expect(screen.queryByTestId("voice-message-play-msg-1")).toBeNull();
	});

	it("plays this message's answer on click (language is resolved by the runtime from the selected voice)", () => {
		renderButton(<VoiceMessagePlayButton message={makeMessage()} />);
		fireEvent.click(screen.getByTestId("voice-message-play-msg-1"));
		expect(playMessage).toHaveBeenCalledTimes(1);
		expect(playMessage).toHaveBeenCalledWith("msg-1", "Hello there. This is the answer.");
		expect(stopPlayback).not.toHaveBeenCalled();
	});

	it("halts playback when clicked while this message is the one playing", () => {
		mockContext = baseContext({ playingMessageId: "msg-1" });
		renderButton(<VoiceMessagePlayButton message={makeMessage()} />);
		fireEvent.click(screen.getByTestId("voice-message-play-msg-1"));
		expect(stopPlayback).toHaveBeenCalledTimes(1);
		expect(playMessage).not.toHaveBeenCalled();
	});
});
