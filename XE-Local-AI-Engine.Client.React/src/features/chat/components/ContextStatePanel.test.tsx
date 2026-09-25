// @vitest-environment jsdom

import { cleanup, fireEvent, screen, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { XeLocalAiEngineClientEndpointsLocalChatV1NodeChatConversationContextStateResponse as ContextStateResponse } from "@/core/api/generated";
import { ContextStateButton } from "@/features/chat/components/ContextStatePanel";
import en from "@/locales/en.json";
import { jsonRoute, problemDetailsRoute } from "@/test/msw/Handlers";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

const strings = en.pages.chat.contextState;
// The generated request validator requires a GUID conversation id.
const conversationId = "0b7e8c2a-3f41-4d6e-9a5b-2c1d0e9f8a7b";
const route = `chat/conversations/${conversationId}/context-state`;

// A live goal, a decision that superseded an earlier one, a retired open question, and a synopsis.
const fixture: ContextStateResponse = {
	entries: [
		{
			id: "e1",
			category: "Decision",
			value: "Use SQLite",
			sourceSequences: [2],
			supersededById: "e3",
			createdAtSequence: 2,
			isLive: false,
		},
		{ id: "e2", category: "Goal", value: "Ship the drawer", sourceSequences: [1, 4], createdAtSequence: 1, isLive: true },
		{ id: "e3", category: "Decision", value: "Use Postgres", sourceSequences: [6], createdAtSequence: 6, isLive: true },
		{
			id: "e4",
			category: "OpenQuestion",
			value: "Which port?",
			sourceSequences: [3],
			retiredAtSequence: 7,
			createdAtSequence: 3,
			isLive: false,
		},
	],
	stateCoversToSequence: 8,
	stateUpdatedAtUtc: 1_790_000_000_000,
	synopsis: "Earlier the user set up the project.",
	synopsisCoversToSequence: 8,
	synopsisUpdatedAtUtc: 1_790_000_000_000,
	nextEntryNumber: 5,
};

const server = setupMswServer();

vi.mock("@/features/chat/stores/NodeChatPreferencesStore", () => ({
	useNodeChatPreferencesStore: (selector: (state: { selectedConversationId: string }) => unknown) =>
		selector({ selectedConversationId: conversationId }),
}));

async function openDrawer(): Promise<HTMLElement> {
	renderWithProviders(<ContextStateButton />);
	fireEvent.click(screen.getByRole("button", { name: strings.aria }));
	return screen.findByTestId("context-state-drawer");
}

describe("ContextStateButton", () => {
	beforeEach(() => {
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
	});

	it("opens the drawer and renders live entries grouped under translated category titles in order", async () => {
		server.use(jsonRoute("get", route, fixture));

		const drawer = await openDrawer();

		expect(await within(drawer).findByText("Ship the drawer")).toBeTruthy();
		expect(within(drawer).getByText(strings.title)).toBeTruthy();
		const goals = within(drawer).getByText(strings.categories.Goal);
		const decisions = within(drawer).getByText(strings.categories.Decision);
		// Goal is listed before Decision.
		expect(goals.compareDocumentPosition(decisions) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
		expect(within(drawer).getByText("Use Postgres")).toBeTruthy();
		expect(within(drawer).getByText("From sequences 1, 4")).toBeTruthy();
		expect(within(drawer).getByText("State covers up to sequence 8", { exact: false })).toBeTruthy();
		expect(within(drawer).getByTestId("context-state-synopsis").textContent).toBe("Earlier the user set up the project.");
	});

	it("lists superseded and retired entries only in the collapsed section", async () => {
		server.use(jsonRoute("get", route, fixture));

		const drawer = await openDrawer();
		const inactive = await within(drawer).findByTestId("context-state-inactive");

		expect(inactive.hasAttribute("open")).toBe(false);
		expect(within(inactive).getByText("Superseded and retired (2)")).toBeTruthy();
		expect(within(inactive).getByText("Use SQLite")).toBeTruthy();
		expect(within(inactive).getByText("replaced by e3", { exact: false })).toBeTruthy();
		expect(within(inactive).getByText("retired at sequence 7", { exact: false })).toBeTruthy();
		// The OpenQuestion group has no live entry, so its title is absent.
		expect(within(drawer).queryByText(strings.categories.OpenQuestion)).toBeNull();
	});

	it("shows the empty state when nothing was distilled yet", async () => {
		server.use(
			jsonRoute("get", route, { entries: [], synopsis: null, stateCoversToSequence: null, synopsisCoversToSequence: null }),
		);

		const drawer = await openDrawer();

		expect(await within(drawer).findByText(strings.empty)).toBeTruthy();
	});

	it("shows the empty state for an unknown conversation (404)", async () => {
		server.use(problemDetailsRoute("get", route, 404, { title: "Not Found" }));

		const drawer = await openDrawer();

		expect(await within(drawer).findByText(strings.empty)).toBeTruthy();
	});
});
