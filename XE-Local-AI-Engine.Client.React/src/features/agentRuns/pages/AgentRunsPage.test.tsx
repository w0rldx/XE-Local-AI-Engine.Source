// @vitest-environment jsdom

// The page over the real generated SDK: MSW answers the routes, so what is pinned here is the wiring nothing else
// holds — which page the footer asks the server for, that a row with a patch opens the EXISTING apply dialog for
// that run id, and that an empty or failing history still renders something the operator can read.

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";

import { AgentRunsPage } from "@/features/agentRuns/pages/AgentRunsPage";
import { jsonRoute, problemDetailsRoute } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

const runId = "run-1758300000000-1";

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

	it("renders an outcome token this build does not know as unknown rather than as raw server text", async () => {
		server.use(...listRoutes({ items: [runDto({ outcome: "SomethingThisBuildNeverHeardOf" })], totalCount: 1 }));

		renderWithProviders(<AgentRunsPage />);

		expect(await screen.findByText("Unknown")).toBeTruthy();
		expect(screen.queryByText("SomethingThisBuildNeverHeardOf")).toBeNull();
	});
});
