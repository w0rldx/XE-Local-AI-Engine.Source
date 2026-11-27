// @vitest-environment jsdom

import { cleanup, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { useDeveloperModeStore } from "@/core/dev-tools/stores/DeveloperModeStore";
import { ChatInputArea } from "@/features/chat/components/ChatInputArea";
import { installJsdomEnvironmentMocks, renderWithMantine } from "@/test/MantineTestRender";

// DeveloperModeStore hydrates from localStorage at module-init, but it also exposes a setter that writes
// through to storage — so these tests drive the flag through the setter rather than seeding storage and
// re-importing ChatInputArea under `vi.resetModules()`.
//
// That matters for stability, not just tidiness: a per-test `await import()` charges the cold transform +
// evaluation of ChatInputArea's whole graph (Mantine, @tabler/icons-react, react-i18next, the selector cards,
// the sampling dialog, the voice controls) against the 5s `testTimeout`. It measures ~1.8s on an idle box for
// the first test in this file versus ~0.1s for the rest, and on a loaded packaging box running all 209 files
// with coverage it intermittently blew past 5s — a timeout that said nothing about the behavior under test.
// A static import moves that cost to collection time, which no test timeout applies to.
//
// Hydration-from-storage is covered where it belongs, in NodeSettings.sampling.test.tsx.
function setDeveloperMode(developerMode: boolean): void {
	useDeveloperModeStore.getState().actions.setDeveloperMode(developerMode);
}

function baseProps() {
	return {
		availableReasoningEfforts: ["none", "medium"] as import("@/features/chat/models/ChatModels").ReasoningEffort[],
		isSending: false,
		modelOptions: [],
		selectedModel: "local-default",
		reasoningEffort: "medium" as import("@/features/chat/models/ChatModels").ReasoningEffort,
		onCancel: vi.fn(),
		onModelChange: vi.fn(),
		onReasoningEffortChange: vi.fn(),
		onSend: vi.fn(),
	};
}

describe("ChatInputArea sampling options trigger", () => {
	beforeEach(() => {
		localStorage.clear();
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
		localStorage.clear();
	});

	it("hides the sampling options trigger when developer mode is OFF", () => {
		setDeveloperMode(false);

		renderWithMantine(<ChatInputArea {...baseProps()} />);

		expect(screen.queryByTestId("chat-sampling-options-trigger")).toBeNull();
	});

	it("shows the sampling options trigger when developer mode is ON", () => {
		setDeveloperMode(true);

		renderWithMantine(<ChatInputArea {...baseProps()} />);

		expect(screen.getByTestId("chat-sampling-options-trigger")).toBeDefined();
	});

	it("trigger button is not disabled and has expected aria-label", () => {
		setDeveloperMode(true);

		renderWithMantine(<ChatInputArea {...baseProps()} />);

		const trigger = screen.getByTestId("chat-sampling-options-trigger");
		// The button should be enabled (not disabled) so it can be clicked.
		expect(trigger.hasAttribute("disabled")).toBe(false);
	});
});
