// @vitest-environment jsdom

// The page over the real generated SDK: MSW answers the routes, so what is pinned here is the wiring nothing else
// holds — which page the footer asks the server for, that a row with a patch opens the EXISTING apply dialog for
// that run id, where the conversation deep link hands its id over, and that an empty or failing history still
// renders something the operator can read.

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { usePendingChatConversationStore } from "@/core/ui/stores/PendingChatConversationStore";
import { AgentRunsPage } from "@/features/agentRuns/pages/AgentRunsPage";
import { jsonRoute, problemDetailsRoute } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

const navigate = vi.hoisted(() => vi.fn());

// Monaco owns a canvas and a worker neither jsdom nor this test needs: what the viewer owes is the right text with
// the right language, which the stub below is enough to assert.
vi.mock("@/core/ui/components/CodeEditor/CodeEditor", () => ({
	CodeEditor: ({ value, language, "data-testid": testId }: { value: string; language?: string; "data-testid"?: string }) => (
		<pre data-testid={testId} data-language={language}>
			{value}
		</pre>
	),
}));

// The app router is built from routeTree.gen.ts; a unit test only needs the navigate CALL, not a real route match.
vi.mock("@tanstack/react-router", async (importOriginal) => ({
	...(await importOriginal<typeof import("@tanstack/react-router")>()),
	useNavigate: () => navigate,
}));

const runId = "run-1758300000000-1";
const conversationId = "33333333-0000-4000-8000-000000000001";

function runDto(overrides: Record<string, unknown> = {}) {
	return {
		runId,
		startedAtUtc: "2026-09-20T10:00:00+00:00",
		outcome: "Completed",
		patchExported: true,
		changedFileCount: 3,
		applyState: "none",
		conversationId: null,
		sizeBytes: 4096,
		...overrides,
	};
}

/** The list route plus the preview route the apply dialog reaches for once a row opens it. */
function listRoutes(body: { readonly items: readonly unknown[]; readonly totalCount: number }) {
	return [
		jsonRoute("get", "agent-home/runs", body),
		jsonRoute("post", `agent-home/runs/${runId}/patch/preview`, {
			canApply: true,
			files: [{ alias: "repo-01", relativePath: "src/App.cs", changeType: "modified", added: 3, removed: 1 }],
			rejections: [],
			containsBinary: false,
			dirtyTargets: [],
			dirtyCheckUnavailable: false,
			patchSha256: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
		}),
	];
}

describe("AgentRunsPage", () => {
	beforeEach(() => {
		navigate.mockClear();
		usePendingChatConversationStore.setState({ pendingConversationId: "" });
	});

	afterEach(() => {
		cleanup();
	});

	it("renders one row per run with its outcome and change summary", async () => {
		server.use(...listRoutes({ items: [runDto()], totalCount: 1 }));

		renderWithProviders(<AgentRunsPage />);

		expect(await screen.findByTestId(`agent-run-row-${runId}`)).toBeTruthy();
		expect(screen.getByText(runId)).toBeTruthy();
		expect(screen.getByText("Completed")).toBeTruthy();
		expect(screen.getByText("3 file(s)")).toBeTruthy();
	});

	it("shows an empty state when the node has kept no runs", async () => {
		server.use(...listRoutes({ items: [], totalCount: 0 }));

		renderWithProviders(<AgentRunsPage />);

		expect(await screen.findByTestId("agent-runs-empty")).toBeTruthy();
		expect(screen.queryByTestId("agent-runs-table")).toBeNull();
	});

	it("shows an inline error when the history cannot be read", async () => {
		server.use(problemDetailsRoute("get", "agent-home/runs", 500, { detail: "The run history is unavailable." }));

		renderWithProviders(<AgentRunsPage />);

		expect(await screen.findByTestId("agent-runs-error")).toBeTruthy();
	});

	it("asks the server for the next page rather than slicing the one it has", async () => {
		const requested: string[] = [];
		server.use(
			...listRoutes({ items: [runDto()], totalCount: 60 }),
			// Overrides the list route above (runtime handlers are prepended) so the query string can be recorded.
			jsonRoute("get", "agent-home/runs", { items: [runDto()], totalCount: 60 }),
		);
		server.events.on("request:start", ({ request }) => {
			if (request.url.includes("agent-home/runs?")) {
				requested.push(new URL(request.url).search);
			}
		});

		renderWithProviders(<AgentRunsPage />);
		expect(await screen.findByTestId("agent-runs-pagination")).toBeTruthy();

		fireEvent.click(screen.getByRole("button", { name: "2" }));

		await waitFor(() => expect(requested.some((search) => search.includes("offset=25"))).toBe(true));
		expect(requested[0]).toContain("offset=0");
		server.events.removeAllListeners();
	});

	it("opens the existing apply dialog for the run whose row was clicked", async () => {
		server.use(...listRoutes({ items: [runDto()], totalCount: 1 }));

		renderWithProviders(<AgentRunsPage />);
		fireEvent.click(await screen.findByTestId(`agent-run-review-${runId}`));

		// The dialog's own content is its own file's subject; what this asserts is that it mounted for THIS run.
		expect(await screen.findByText("src/App.cs")).toBeTruthy();
	});

	it("offers no review action for a run that exported nothing", async () => {
		server.use(...listRoutes({ items: [runDto({ patchExported: false, changedFileCount: null })], totalCount: 1 }));

		renderWithProviders(<AgentRunsPage />);

		expect(await screen.findByText("No changes exported")).toBeTruthy();
		expect(screen.queryByTestId(`agent-run-review-${runId}`)).toBeNull();
	});

	// The id travels through the core hand-off store rather than the URL: `/chat` has no search schema, and the chat
	// page reads its thread from the preferences that store writes into on mount.
	it("hands the run's conversation to chat and navigates there", async () => {
		server.use(...listRoutes({ items: [runDto({ conversationId })], totalCount: 1 }));

		renderWithProviders(<AgentRunsPage />);
		fireEvent.click(await screen.findByTestId(`agent-run-conversation-${runId}`));

		expect(usePendingChatConversationStore.getState().pendingConversationId).toBe(conversationId);
		expect(navigate).toHaveBeenCalledWith({ to: "/chat" });
	});

	// A run older than the `started` event carrying the id, or one whose log could not be parsed, has no conversation
	// to open: the action is absent rather than a disabled control whose explanation no keyboard could reach.
	it("offers no conversation action for a run with no conversation id", async () => {
		server.use(...listRoutes({ items: [runDto()], totalCount: 1 }));

		renderWithProviders(<AgentRunsPage />);

		expect(await screen.findByTestId(`agent-run-row-${runId}`)).toBeTruthy();
		expect(screen.queryByTestId(`agent-run-conversation-${runId}`)).toBeNull();
	});

	it("asks before deleting, then removes the run and re-reads the list", async () => {
		const requested: string[] = [];
		server.events.on("request:start", ({ request }) => {
			requested.push(`${request.method} ${new URL(request.url).pathname}`);
		});
		server.use(...listRoutes({ items: [runDto()], totalCount: 1 }), jsonRoute("delete", `agent-home/runs/${runId}`, null));

		renderWithProviders(<AgentRunsPage />);
		fireEvent.click(await screen.findByTestId(`agent-run-delete-${runId}`));

		// Nothing is removed by opening the dialog: the confirmation is the act, not the button that opened it.
		expect(await screen.findByTestId("agent-run-delete-dialog")).toBeTruthy();
		expect(requested.some((entry) => entry.startsWith("DELETE"))).toBe(false);

		fireEvent.click(screen.getByTestId("agent-run-delete-confirm"));

		await waitFor(() => expect(requested.filter((entry) => entry.startsWith("DELETE")).length).toBe(1));
		await waitFor(() => expect(screen.queryByTestId("agent-run-delete-dialog")).toBeNull());
		// A removed run renumbers every page after it, so the list is re-read rather than patched in place.
		await waitFor(() =>
			expect(requested.filter((entry) => entry === "GET /api/local/v1/agent-home/runs").length).toBeGreaterThan(1),
		);
		server.events.removeAllListeners();
	});

	it("shows the node's refusal when a run is in flight rather than closing as though it worked", async () => {
		server.use(
			...listRoutes({ items: [runDto()], totalCount: 1 }),
			problemDetailsRoute("delete", `agent-home/runs/${runId}`, 409, {
				detail: "This run cannot be deleted right now.",
			}),
		);

		renderWithProviders(<AgentRunsPage />);
		fireEvent.click(await screen.findByTestId(`agent-run-delete-${runId}`));
		fireEvent.click(await screen.findByTestId("agent-run-delete-confirm"));

		expect(await screen.findByTestId("agent-run-delete-error")).toBeTruthy();
		expect(screen.getByTestId("agent-run-delete-dialog")).toBeTruthy();
	});

	it("reads neither the log nor the patch until the viewer is opened", async () => {
		const requested: string[] = [];
		server.events.on("request:start", ({ request }) => {
			requested.push(new URL(request.url).pathname);
		});
		server.use(
			...listRoutes({ items: [runDto()], totalCount: 1 }),
			jsonRoute("get", `agent-home/runs/${runId}/log`, { text: '{"eventName":"started"}', truncated: false }),
			jsonRoute("get", `agent-home/runs/${runId}/patch`, { text: "diff --git a/x b/x", truncated: false }),
		);

		renderWithProviders(<AgentRunsPage />);
		expect(await screen.findByTestId(`agent-run-row-${runId}`)).toBeTruthy();

		expect(requested.some((path) => path.endsWith("/log"))).toBe(false);
		expect(requested.some((path) => path.endsWith("/patch"))).toBe(false);

		fireEvent.click(screen.getByTestId(`agent-run-view-${runId}`));

		await waitFor(() => expect(requested.some((path) => path.endsWith("/log"))).toBe(true));
		server.events.removeAllListeners();
	});

	it("shows the run's log and, on the other tab, its patch as a diff", async () => {
		server.use(
			...listRoutes({ items: [runDto()], totalCount: 1 }),
			jsonRoute("get", `agent-home/runs/${runId}/log`, { text: '{"eventName":"started"}', truncated: false }),
			jsonRoute("get", `agent-home/runs/${runId}/patch`, { text: "diff --git a/x b/x", truncated: false }),
		);

		renderWithProviders(<AgentRunsPage />);
		fireEvent.click(await screen.findByTestId(`agent-run-view-${runId}`));

		const log = await screen.findByTestId("agent-run-viewer-log");
		expect(log.textContent).toContain("started");
		expect(log.getAttribute("data-language")).toBe("plaintext");

		fireEvent.click(screen.getByTestId("agent-run-viewer-tab-patch"));

		const patch = await screen.findByTestId("agent-run-viewer-patch");
		expect(patch.textContent).toContain("diff --git");
		expect(patch.getAttribute("data-language")).toBe("diff");
	});

	// Text with a gap in it and no note would read as the whole story.
	it("says so when the file it shows was cut short", async () => {
		server.use(
			...listRoutes({ items: [runDto()], totalCount: 1 }),
			jsonRoute("get", `agent-home/runs/${runId}/log`, { text: '{"eventName":"started"}', truncated: true }),
			jsonRoute("get", `agent-home/runs/${runId}/patch`, { text: "", truncated: false }),
		);

		renderWithProviders(<AgentRunsPage />);
		fireEvent.click(await screen.findByTestId(`agent-run-view-${runId}`));

		expect(await screen.findByTestId("agent-run-viewer-log-truncated")).toBeTruthy();
	});

	it("says a run exported nothing rather than showing an empty editor", async () => {
		server.use(
			...listRoutes({ items: [runDto()], totalCount: 1 }),
			jsonRoute("get", `agent-home/runs/${runId}/log`, { text: "x", truncated: false }),
			jsonRoute("get", `agent-home/runs/${runId}/patch`, { text: "", truncated: false }),
		);

		renderWithProviders(<AgentRunsPage />);
		fireEvent.click(await screen.findByTestId(`agent-run-view-${runId}`));
		fireEvent.click(await screen.findByTestId("agent-run-viewer-tab-patch"));

		expect(await screen.findByTestId("agent-run-viewer-patch-empty")).toBeTruthy();
	});

	it("renders an outcome token this build does not know as unknown rather than as raw server text", async () => {
		server.use(...listRoutes({ items: [runDto({ outcome: "SomethingThisBuildNeverHeardOf" })], totalCount: 1 }));

		renderWithProviders(<AgentRunsPage />);

		expect(await screen.findByText("Unknown")).toBeTruthy();
		expect(screen.queryByText("SomethingThisBuildNeverHeardOf")).toBeNull();
	});
});
