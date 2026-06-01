// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("react-i18next", () => ({
	useTranslation: () => ({
		t: (_key: string, defaultValue?: string, options?: Record<string, unknown>) => {
			let text = defaultValue ?? _key;
			if (options) {
				for (const [name, value] of Object.entries(options)) {
					text = text.replace(`{{${name}}}`, String(value));
				}
			}
			return text;
		},
	}),
}));

const { hooksMock, confirmMock, toastMock } = vi.hoisted(() => ({
	hooksMock: {
		useGoldenConversations: vi.fn(),
		useCreateGoldenConversation: vi.fn(),
		useDeleteGoldenConversation: vi.fn(),
		useHarvestGolden: vi.fn(),
		useApproveGolden: vi.fn(),
	},
	confirmMock: vi.fn(),
	toastMock: { success: vi.fn(), error: vi.fn(), info: vi.fn(), warn: vi.fn(), warning: vi.fn() },
}));

vi.mock("@/features/agents/queries/useGoldenConversations", () => hooksMock);
vi.mock("@/core/ui/hooks/useConfirm", () => ({
	useConfirm: () => ({ confirm: confirmMock }),
}));
vi.mock("@/core/ui/notifications/Toast", () => ({ toast: toastMock }));

import { GoldenConversationPanel } from "@/features/agents/components/GoldenConversationPanel";
import type { GoldenConversation } from "@/features/agents/models/GoldenConversationModels";

function makeGoldenCase(overrides: Partial<GoldenConversation> = {}): GoldenConversation {
	return {
		id: "golden-1",
		agentDefinitionId: "agent-1",
		title: "Summarizes accurately",
		inputTurns: [{ role: "user", text: "Summarize the document" }],
		assertion: { requiredPhrases: ["summary"], forbiddenPhrases: [] },
		rubric: null,
		enabled: true,
		source: "manual",
		sourceMessageId: null,
		sourceConversationId: null,
		createdAtUtc: 1000,
		updatedAtUtc: 2000,
		...overrides,
	};
}

function makeMutation() {
	return { mutate: vi.fn(), isPending: false, error: null };
}

function installJsdomEnvironmentMocks(): void {
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
	Object.defineProperty(window, "ResizeObserver", {
		writable: true,
		value: class ResizeObserverMock {
			observe = vi.fn();

			unobserve = vi.fn();

			disconnect = vi.fn();
		},
	});
	Object.defineProperty(document, "fonts", {
		writable: true,
		value: { ready: Promise.resolve(), addEventListener: vi.fn(), removeEventListener: vi.fn() },
	});
}

function renderPanel(ui: ReactElement) {
	return render(<MantineProvider>{ui}</MantineProvider>);
}

describe("GoldenConversationPanel", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		hooksMock.useGoldenConversations.mockReturnValue({ data: [makeGoldenCase()], isLoading: false, error: null });
		hooksMock.useCreateGoldenConversation.mockReturnValue(makeMutation());
		hooksMock.useDeleteGoldenConversation.mockReturnValue(makeMutation());
		hooksMock.useHarvestGolden.mockReturnValue(makeMutation());
		hooksMock.useApproveGolden.mockReturnValue(makeMutation());
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it("renders nothing when the capability gate is off", () => {
		renderPanel(<GoldenConversationPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={false} />);

		expect(screen.queryByTestId("golden-panel-agent-1")).toBeNull();
		expect(hooksMock.useGoldenConversations).toHaveBeenCalledWith(null);
	});

	it("lists golden cases with the turn count + assertion presence badge", () => {
		renderPanel(<GoldenConversationPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		expect(screen.getByTestId("golden-case-golden-1")).toBeTruthy();
		expect(screen.getByText("Summarizes accurately")).toBeTruthy();
		expect(screen.getByTestId("golden-case-turns-golden-1").textContent).toContain("1");
		expect(screen.getByTestId("golden-case-assertion-golden-1")).toBeTruthy();
		// No rubric on this case → no rubric badge.
		expect(screen.queryByTestId("golden-case-rubric-golden-1")).toBeNull();
	});

	it("shows the empty state when there are no golden cases", () => {
		hooksMock.useGoldenConversations.mockReturnValue({ data: [], isLoading: false, error: null });

		renderPanel(<GoldenConversationPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		expect(screen.getByTestId("golden-empty")).toBeTruthy();
	});

	it("opens the add form and creates a golden case with parsed turns + an assertion", () => {
		const createMutation = makeMutation();
		hooksMock.useCreateGoldenConversation.mockReturnValue(createMutation);

		renderPanel(<GoldenConversationPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		fireEvent.click(screen.getByTestId("golden-add-button"));
		fireEvent.change(screen.getByTestId("golden-form-title"), { target: { value: "Cites sources" } });
		fireEvent.change(screen.getByTestId("golden-form-turns"), {
			target: { value: "user: What is the capital of France?\nassistant: Paris" },
		});
		fireEvent.change(screen.getByTestId("golden-form-required"), { target: { value: "Paris" } });
		fireEvent.click(screen.getByTestId("golden-form-submit"));

		expect(createMutation.mutate).toHaveBeenCalledTimes(1);
		const request = createMutation.mutate.mock.calls.at(0)?.at(0);
		expect(request).toMatchObject({
			title: "Cites sources",
			inputTurns: [
				{ role: "user", text: "What is the capital of France?" },
				{ role: "assistant", text: "Paris" },
			],
			assertion: { requiredPhrases: ["Paris"], forbiddenPhrases: [] },
		});
	});

	it("blocks create when neither an assertion phrase nor a rubric is provided", () => {
		const createMutation = makeMutation();
		hooksMock.useCreateGoldenConversation.mockReturnValue(createMutation);

		renderPanel(<GoldenConversationPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		fireEvent.click(screen.getByTestId("golden-add-button"));
		fireEvent.change(screen.getByTestId("golden-form-title"), { target: { value: "No signal" } });
		fireEvent.change(screen.getByTestId("golden-form-turns"), { target: { value: "Summarize this" } });
		fireEvent.click(screen.getByTestId("golden-form-submit"));

		expect(createMutation.mutate).not.toHaveBeenCalled();
		expect(screen.getByTestId("golden-form-validation-error")).toBeTruthy();
	});

	it("deletes a golden case through the delete mutation after confirmation", async () => {
		confirmMock.mockResolvedValue(true);
		const deleteMutation = makeMutation();
		hooksMock.useDeleteGoldenConversation.mockReturnValue(deleteMutation);

		renderPanel(<GoldenConversationPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		fireEvent.click(screen.getByTestId("golden-delete-golden-1"));
		await Promise.resolve();

		expect(confirmMock).toHaveBeenCalled();
		expect(deleteMutation.mutate).toHaveBeenCalledWith("golden-1");
	});

	it("surfaces a load error", () => {
		hooksMock.useGoldenConversations.mockReturnValue({ data: undefined, isLoading: false, error: new Error("boom") });

		renderPanel(<GoldenConversationPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		expect(screen.getByTestId("golden-list-error")).toBeTruthy();
	});

	it("renders the pending-review sub-section for a harvested+disabled case with Approve and Reject buttons", () => {
		const pendingCase = makeGoldenCase({
			id: "golden-pending-1",
			source: "harvested",
			enabled: false,
			sourceMessageId: "msg-1",
			sourceConversationId: "conv-1",
		});
		hooksMock.useGoldenConversations.mockReturnValue({ data: [pendingCase], isLoading: false, error: null });

		renderPanel(<GoldenConversationPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		expect(screen.getByTestId("golden-pending-section")).toBeTruthy();
		expect(screen.getByTestId("golden-pending-golden-pending-1")).toBeTruthy();
		expect(screen.getByTestId("golden-pending-approve-golden-pending-1")).toBeTruthy();
		expect(screen.getByTestId("golden-pending-reject-golden-pending-1")).toBeTruthy();
	});

	it("clicking Approve on a pending case invokes the approve mutation with the case id", () => {
		const approveMutation = makeMutation();
		hooksMock.useApproveGolden.mockReturnValue(approveMutation);

		const pendingCase = makeGoldenCase({
			id: "golden-pending-2",
			source: "harvested",
			enabled: false,
		});
		hooksMock.useGoldenConversations.mockReturnValue({ data: [pendingCase], isLoading: false, error: null });

		renderPanel(<GoldenConversationPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		fireEvent.click(screen.getByTestId("golden-pending-approve-golden-pending-2"));

		expect(approveMutation.mutate).toHaveBeenCalledTimes(1);
		expect(approveMutation.mutate).toHaveBeenCalledWith("golden-pending-2");
	});

	it("clicking Reject on a pending case invokes the delete flow with confirmation", async () => {
		confirmMock.mockResolvedValue(true);
		const deleteMutation = makeMutation();
		hooksMock.useDeleteGoldenConversation.mockReturnValue(deleteMutation);

		const pendingCase = makeGoldenCase({
			id: "golden-pending-3",
			source: "harvested",
			enabled: false,
		});
		hooksMock.useGoldenConversations.mockReturnValue({ data: [pendingCase], isLoading: false, error: null });

		renderPanel(<GoldenConversationPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		fireEvent.click(screen.getByTestId("golden-pending-reject-golden-pending-3"));
		await Promise.resolve();

		expect(confirmMock).toHaveBeenCalled();
		expect(deleteMutation.mutate).toHaveBeenCalledWith("golden-pending-3");
	});

	it("shows the 'harvested' badge in the active list for a harvested+enabled case", () => {
		const harvestedEnabled = makeGoldenCase({
			id: "golden-harvested-active",
			source: "harvested",
			enabled: true,
		});
		hooksMock.useGoldenConversations.mockReturnValue({ data: [harvestedEnabled], isLoading: false, error: null });

		renderPanel(<GoldenConversationPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		// The case renders in the active list (not the pending section).
		expect(screen.queryByTestId("golden-pending-section")).toBeNull();
		expect(screen.getByTestId("golden-case-golden-harvested-active")).toBeTruthy();
		expect(screen.getByTestId("golden-case-harvested-golden-harvested-active")).toBeTruthy();
	});

	it("clicking 'Harvest from 👍' invokes the harvest mutation", () => {
		const harvestMutation = makeMutation();
		hooksMock.useHarvestGolden.mockReturnValue(harvestMutation);

		renderPanel(<GoldenConversationPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		fireEvent.click(screen.getByTestId("golden-harvest-button"));

		expect(harvestMutation.mutate).toHaveBeenCalledTimes(1);
	});
});
