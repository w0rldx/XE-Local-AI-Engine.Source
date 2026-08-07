// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { VoicePreviewButton } from "@/features/voice/components/VoicePreviewButton";
import type { VoiceRuntimeContextValue } from "@/features/voice/VoiceRuntimeContext";

// Controllable runtime context (component reads it via useVoiceRuntime).
let mockContext: VoiceRuntimeContextValue;
const previewVoice = vi.fn(() => Promise.resolve());

vi.mock("@/features/voice/VoiceRuntimeContext", () => ({
	useVoiceRuntime: (): VoiceRuntimeContextValue => mockContext,
}));

vi.mock("react-i18next", () => ({
	useTranslation: () => ({ t: (_key: string, fallback?: string) => fallback ?? _key }),
}));

// A non-null stand-in so `Boolean(runtime)` gates true; the component never calls into it directly.
const fakeRuntime = {} as VoiceRuntimeContextValue["runtime"];

function baseContext(overrides: Partial<VoiceRuntimeContextValue> = {}): VoiceRuntimeContextValue {
	return {
		enabled: true,
		defaultVoiceProfile: "af_heart",
		runtime: fakeRuntime,
		playingMessageId: undefined,
		playMessage: () => Promise.resolve(),
		previewVoice,
		stopPlayback: () => undefined,
		...overrides,
	};
}

function renderButton(ui: ReactElement) {
	return render(<MantineProvider>{ui}</MantineProvider>);
}

describe("VoicePreviewButton", () => {
	beforeEach(() => {
		previewVoice.mockClear();
		mockContext = baseContext();
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
	});

	it("auditions the given voice on click", () => {
		renderButton(<VoicePreviewButton voiceId="am_michael" />);
		fireEvent.click(screen.getByTestId("voice-preview-button"));
		expect(previewVoice).toHaveBeenCalledTimes(1);
		expect(previewVoice).toHaveBeenCalledWith("am_michael");
	});

	it("is disabled when no voice is selected", () => {
		renderButton(<VoicePreviewButton voiceId={null} />);
		const button = screen.getByTestId("voice-preview-button") as HTMLButtonElement;
		expect(button.disabled).toBe(true);
		fireEvent.click(button);
		expect(previewVoice).not.toHaveBeenCalled();
	});

	it("is disabled when the runtime is not available", () => {
		mockContext = baseContext({ runtime: undefined });
		renderButton(<VoicePreviewButton voiceId="af_heart" />);
		const button = screen.getByTestId("voice-preview-button") as HTMLButtonElement;
		expect(button.disabled).toBe(true);
	});

	it("shows a busy state while synthesis is in flight, then clears it", async () => {
		let resolvePreview: () => void = () => undefined;
		previewVoice.mockImplementationOnce(
			() =>
				new Promise<void>((resolve) => {
					resolvePreview = resolve;
				}),
		);
		renderButton(<VoicePreviewButton voiceId="af_heart" />);
		const button = screen.getByTestId("voice-preview-button") as HTMLButtonElement;
		fireEvent.click(button);
		// Mantine renders the loading spinner as a data-loading attribute on the disabled button.
		await waitFor(() => expect(button.disabled).toBe(true));
		resolvePreview();
		await waitFor(() => expect(button.disabled).toBe(false));
	});
});
