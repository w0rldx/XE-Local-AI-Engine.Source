// @vitest-environment jsdom

// The composer's per-conversation "Auto-accept web content" switch (ADR 0017, D8): shown only when the node allows web
// access, off for every conversation until chosen, and gated per user behind a one-time prompt-injection notice that is
// recorded through the tutorial state.

import { fireEvent, screen, waitFor } from "@testing-library/react";
import { HttpResponse, http } from "msw";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { WebContentAutoAcceptToggle } from "@/features/chat/components/ChatInputArea/WebContentAutoAcceptToggle";
import { isWebContentAutoAccepted } from "@/features/chat/stores/WebContentAutoAcceptStore";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { createTestQueryClient, renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

// Node settings are ambient here (the gate the switch reads, not its subject), so they are stubbed at the query seam;
// the tutorial state the notice records is the subject and goes through MSW.
const { webAccessEnabled } = vi.hoisted(() => ({ webAccessEnabled: { value: true } }));
vi.mock("@/core/api/generated/@tanstack/react-query.gen", async (importOriginal) => ({
	...(await importOriginal<typeof import("@/core/api/generated/@tanstack/react-query.gen")>()),
	getNodeSettingsOptions: () => ({
		queryKey: ["getNodeSettings", webAccessEnabled.value],
		queryFn: async () => ({ webAccessEnabled: webAccessEnabled.value }),
	}),
}));

setupMswServer();

// The wire key, spelled out: the server stores it per user, so a rename would re-show the notice to everyone.
const noticeKey = "web-content-auto-accept-notice";

function tutorialState(entries: readonly { key: string; status: string }[]) {
	return http.get(localApiPath("tutorial-state"), () =>
		HttpResponse.json({ entries: entries.map((entry) => ({ ...entry, atUtc: "2026-09-27T00:00:00Z" })) }),
	);
}

function recordTutorialSaves(status = 204) {
	const saved: unknown[] = [];
	server.use(
		http.put(localApiPath("tutorial-state"), async ({ request }) => {
			saved.push(await request.json());
			return status === 204 ? new HttpResponse(null, { status }) : HttpResponse.json({ title: "failed" }, { status });
		}),
	);
	return saved;
}

async function findToggle(): Promise<HTMLButtonElement> {
	return screen.findByTestId<HTMLButtonElement>("chat-web-auto-accept-toggle");
}

// The switch is held disabled until the user's acknowledgement has loaded; a test clicks only once it is armed.
async function findArmedToggle(): Promise<HTMLButtonElement> {
	const toggle = await findToggle();
	await waitFor(() => expect(toggle.disabled).toBe(false));
	return toggle;
}

describe("WebContentAutoAcceptToggle", () => {
	beforeEach(() => {
		webAccessEnabled.value = true;
		localStorage.clear();
	});

	afterEach(() => {
		localStorage.clear();
	});

	it("is not offered when the node does not allow web access", async () => {
		webAccessEnabled.value = false;
		const queryClient = createTestQueryClient();
		renderWithProviders(<WebContentAutoAcceptToggle conversationId="conv-off" disabled={false} />, { queryClient });

		// Settled on the node's answer first, so the absence below is the gate's verdict, not a query still in flight.
		await waitFor(() => expect(queryClient.getQueryState(["getNodeSettings", false])?.status).toBe("success"));
		expect(screen.queryByTestId("chat-web-auto-accept-toggle")).toBeNull();
	});

	it("starts off and stays disabled for a draft that has no conversation yet", async () => {
		server.use(tutorialState([]));
		renderWithProviders(<WebContentAutoAcceptToggle conversationId="" disabled={false} />);

		const toggle = await findToggle();
		expect(toggle.getAttribute("aria-label")).toBe("Auto-accept web content");
		expect(toggle.getAttribute("aria-pressed")).toBe("false");
		expect(toggle.disabled).toBe(true);
	});

	it("shows the risk notice on the first enable and records the acknowledgement", async () => {
		server.use(tutorialState([]));
		const saved = recordTutorialSaves();
		renderWithProviders(<WebContentAutoAcceptToggle conversationId="conv-first" disabled={false} />);

		fireEvent.click(await findArmedToggle());

		expect(await screen.findByText("Auto-accept web content?")).toBeTruthy();
		expect(
			screen.getByText(
				"Web pages and search results can contain hidden instructions (prompt injection) written to make the model leak your data or take actions you did not ask for.",
			),
		).toBeTruthy();
		expect(
			screen.getByText(
				"With auto-accept on, the model's web requests in this conversation are sent without asking you, and their results reach the model without being shown to you first.",
			),
		).toBeTruthy();
		expect(screen.getByText("You can turn it off again at any time.")).toBeTruthy();
		// Not enabled until the notice is confirmed.
		expect((await findToggle()).getAttribute("aria-pressed")).toBe("false");

		fireEvent.click(screen.getByRole("button", { name: "Enable auto-accept" }));

		await waitFor(() => expect(saved).toEqual([{ key: noticeKey, status: "completed" }]));
		expect((await findToggle()).getAttribute("aria-pressed")).toBe("true");
		expect(isWebContentAutoAccepted("conv-first")).toBe(true);
		expect(localStorage.getItem("xe-node-chat-web-auto:conv-first")).toBe("true");
	});

	it("leaves auto-accept off when the notice is cancelled", async () => {
		server.use(tutorialState([]));
		const saved = recordTutorialSaves();
		renderWithProviders(<WebContentAutoAcceptToggle conversationId="conv-cancel" disabled={false} />);

		fireEvent.click(await findArmedToggle());
		fireEvent.click(await screen.findByRole("button", { name: "Cancel" }));

		await waitFor(() => expect(screen.queryByText("Enable auto-accept")).toBeNull());
		expect((await findToggle()).getAttribute("aria-pressed")).toBe("false");
		expect(isWebContentAutoAccepted("conv-cancel")).toBe(false);
		expect(saved).toEqual([]);
	});

	it("enables straight away once the user has acknowledged the notice", async () => {
		server.use(tutorialState([{ key: noticeKey, status: "completed" }]));
		const saved = recordTutorialSaves();
		renderWithProviders(<WebContentAutoAcceptToggle conversationId="conv-known" disabled={false} />);
		const toggle = await findArmedToggle();

		fireEvent.click(toggle);

		expect(toggle.getAttribute("aria-pressed")).toBe("true");
		expect(screen.queryByText("Auto-accept web content?")).toBeNull();
		expect(saved).toEqual([]);

		fireEvent.click(toggle);
		expect(toggle.getAttribute("aria-pressed")).toBe("false");
		expect(localStorage.getItem("xe-node-chat-web-auto:conv-known")).toBeNull();
	});

	it("still enables this conversation when recording the acknowledgement fails", async () => {
		server.use(tutorialState([]));
		const saved = recordTutorialSaves(500);
		renderWithProviders(<WebContentAutoAcceptToggle conversationId="conv-failed-save" disabled={false} />);

		fireEvent.click(await findArmedToggle());
		fireEvent.click(await screen.findByRole("button", { name: "Enable auto-accept" }));

		await waitFor(() => expect(saved).toHaveLength(1));
		expect((await findToggle()).getAttribute("aria-pressed")).toBe("true");
	});

	it("keeps each conversation's choice to itself", async () => {
		localStorage.setItem("xe-node-chat-web-auto:conv-a", "true");
		server.use(tutorialState([{ key: noticeKey, status: "completed" }]));
		const first = renderWithProviders(<WebContentAutoAcceptToggle conversationId="conv-a" disabled={false} />);
		expect((await findToggle()).getAttribute("aria-pressed")).toBe("true");
		first.unmount();

		renderWithProviders(<WebContentAutoAcceptToggle conversationId="conv-b" disabled={false} />);
		expect((await findToggle()).getAttribute("aria-pressed")).toBe("false");
		expect(isWebContentAutoAccepted("conv-a")).toBe(true);
		expect(isWebContentAutoAccepted("conv-b")).toBe(false);
	});
});
