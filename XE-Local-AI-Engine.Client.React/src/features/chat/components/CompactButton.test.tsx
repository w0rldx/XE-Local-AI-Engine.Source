// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { CompactButton } from "@/features/chat/components/CompactButton";
import { renderWithProviders } from "@/test/RenderWithProviders";

// vi.mock factories are hoisted above the module, so the spies/state they close over must be created with vi.hoisted.
const { compactSpy, confirmSpy, toastSpies, storeState } = vi.hoisted(() => ({
	compactSpy: vi.fn(),
	confirmSpy: vi.fn(),
	toastSpies: { success: vi.fn(), info: vi.fn(), warn: vi.fn(), error: vi.fn() },
	storeState: { selectedConversationId: "conv-1", selectedModel: "user-model" },
}));

// The button drives the adapter's compactConversation; stub it so the click wiring is asserted without a backend.
vi.mock("@/features/chat/api/NodeChatAdapter", () => ({
	nodeChatAdapter: { compactConversation: (id: string, model?: string) => compactSpy(id, model) },
}));

// The confirmation gate and toasts are collaborators, not the unit under test — stub them.
vi.mock("@/core/ui/hooks/useConfirm", () => ({ useConfirm: () => ({ confirm: confirmSpy }) }));
vi.mock("@/core/ui/notifications/Toast", () => ({ toast: toastSpies }));

// The active conversation + selected model come from the preferences store; drive them from mutable hoisted state so
// individual tests can vary the selection (e.g. the local-default sentinel).
vi.mock("@/features/chat/stores/NodeChatPreferencesStore", () => ({
	useNodeChatPreferencesStore: (selector: (state: { selectedConversationId: string; selectedModel: string }) => unknown) => selector(storeState),
}));

describe("CompactButton", () => {
	beforeEach(() => {
		compactSpy.mockReset();
		confirmSpy.mockReset();
		storeState.selectedConversationId = "conv-1";
		storeState.selectedModel = "user-model";
		for (const spy of Object.values(toastSpies)) {
			spy.mockReset();
		}
		Object.defineProperty(window, "matchMedia", {
			writable: true,
			value: vi.fn().mockImplementation((query: string) => ({
				matches: false,
				media: query,
				addEventListener: vi.fn(),
				removeEventListener: vi.fn(),
				addListener: vi.fn(),
				removeListener: vi.fn(),
				dispatchEvent: vi.fn(),
			})),
		});
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it("compacts the active conversation after the user confirms and shows a success toast", async () => {
		confirmSpy.mockResolvedValue(true);
		compactSpy.mockResolvedValue({ outcome: "Compacted", messagesFolded: 3 });

		renderWithProviders(<CompactButton percentUsed={95} />);
		fireEvent.click(screen.getByTestId("compact-conversation-button"));

		await waitFor(() => expect(compactSpy).toHaveBeenCalledWith("conv-1", "user-model"));
		await waitFor(() => expect(toastSpies.success).toHaveBeenCalledTimes(1));
	});

	it("maps the local-default sentinel to undefined so the backend uses the node default", async () => {
		storeState.selectedModel = "local-default";
		confirmSpy.mockResolvedValue(true);
		compactSpy.mockResolvedValue({ outcome: "Compacted", messagesFolded: 3, usedFallbackModel: false });

		renderWithProviders(<CompactButton />);
		fireEvent.click(screen.getByTestId("compact-conversation-button"));

		await waitFor(() => expect(compactSpy).toHaveBeenCalledWith("conv-1", undefined));
	});

	it("shows an on-device info toast (not success) when a cloud selection fell back to a local model", async () => {
		confirmSpy.mockResolvedValue(true);
		compactSpy.mockResolvedValue({ outcome: "Compacted", messagesFolded: 2, usedFallbackModel: true, modelUsed: "local-model" });

		renderWithProviders(<CompactButton />);
		fireEvent.click(screen.getByTestId("compact-conversation-button"));

		await waitFor(() => expect(toastSpies.info).toHaveBeenCalledTimes(1));
		expect(toastSpies.success).not.toHaveBeenCalled();
	});

	it("does nothing when the user cancels the confirmation", async () => {
		confirmSpy.mockResolvedValue(false);

		renderWithProviders(<CompactButton />);
		fireEvent.click(screen.getByTestId("compact-conversation-button"));

		await waitFor(() => expect(confirmSpy).toHaveBeenCalledTimes(1));
		expect(compactSpy).not.toHaveBeenCalled();
	});

	it("shows an info toast (not success) when there is nothing to compact", async () => {
		confirmSpy.mockResolvedValue(true);
		compactSpy.mockResolvedValue({ outcome: "NothingToCompact", messagesFolded: 0 });

		renderWithProviders(<CompactButton />);
		fireEvent.click(screen.getByTestId("compact-conversation-button"));

		await waitFor(() => expect(toastSpies.info).toHaveBeenCalledTimes(1));
		expect(toastSpies.success).not.toHaveBeenCalled();
	});
});
