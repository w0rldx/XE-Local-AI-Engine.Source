// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";

import { BenchmarksPage } from "@/features/benchmarks/pages/BenchmarksPage";
import { jsonRoute, problemDetailsRoute } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

// Smoke coverage only — the page orchestrates seven query hooks, a dialog, a project selector and a live run pane,
// and unit-testing that orchestration would mostly re-test TanStack Query. What is pinned here is that the page
// mounts against the real query/i18n stack, auto-selects the first project, routes a load error to an inline Alert
// (not a toast), and offers the create editor.
//
// Every case serves projects with NO runs on purpose: a selected run mounts BenchmarkRunLivePane, which opens a
// SignalR hub — out of scope for a page smoke and covered by useBenchmarkRunHub's own suite.

const projectId = "aaaaaaaa-0000-4000-8000-000000000001";

function baseRoutes(projects: unknown[]) {
	server.use(
		jsonRoute("get", "benchmarks/projects", { items: projects }),
		jsonRoute("get", "benchmarks/eligible-models", { items: [] }),
		jsonRoute("get", "agents", { items: [] }),
		jsonRoute("get", `benchmarks/projects/${projectId}`, {
			id: projectId,
			name: "Summarisation",
			contextTokens: 4096,
			agentDefinitionId: "cccccccc-0000-4000-8000-000000000003",
			judgeEnabled: false,
			runCount: 0,
			isFrozen: false,
			version: 1,
			createdAtUtc: 1,
			updatedAtUtc: 2,
			coreTask: "Summarise the attached text.",
			judge: { enabled: false },
		}),
		jsonRoute("get", `benchmarks/projects/${projectId}/runs`, {
			items: [],
			rankCohort: { rankedCount: 0, totalScored: 0 },
		}),
	);
}

const projectRow = {
	id: projectId,
	name: "Summarisation",
	contextTokens: 4096,
	agentDefinitionId: "cccccccc-0000-4000-8000-000000000003",
	judgeEnabled: false,
	runCount: 0,
	isFrozen: false,
	version: 1,
	createdAtUtc: 1,
	updatedAtUtc: 2,
};

describe("BenchmarksPage", () => {
	afterEach(cleanup);

	it("guides the operator to create a project when the node has none", async () => {
		baseRoutes([]);

		renderWithProviders(<BenchmarksPage />);

		expect(await screen.findByText("Create a project to freeze one task and compare models.")).toBeTruthy();
		expect(screen.getByText("Local model benchmarks")).toBeTruthy();
	});

	// The page selects the first project itself, so the detail pane is populated without any operator action.
	it("auto-selects the first project and shows its core task", async () => {
		baseRoutes([projectRow]);

		renderWithProviders(<BenchmarksPage />);

		expect(await screen.findByText("Summarise the attached text.")).toBeTruthy();
		expect(screen.getByRole("heading", { name: "Summarisation" })).toBeTruthy();
		expect(screen.queryByText("Select a benchmark project.")).toBeNull();
		// An unfrozen project stays editable, so the frozen explanation must not appear.
		expect(screen.getByRole("button", { name: "Edit" })).toBeTruthy();
	});

	// A query load-error belongs in an inline Alert, not a toast (agent-knowledge §5 "Error surfacing").
	it("shows a projects load failure inline", async () => {
		server.use(
			problemDetailsRoute("get", "benchmarks/projects", 500, {
				title: "Server Error",
				detail: "The benchmark store is offline.",
			}),
			jsonRoute("get", "benchmarks/eligible-models", { items: [] }),
			jsonRoute("get", "agents", { items: [] }),
		);

		renderWithProviders(<BenchmarksPage />);

		expect(await screen.findByText("The benchmark store is offline.")).toBeTruthy();
	});

	it("opens the create-project editor from the page action", async () => {
		baseRoutes([]);

		renderWithProviders(<BenchmarksPage />);

		fireEvent.click(await screen.findByRole("button", { name: "New project" }));

		await waitFor(() => expect(screen.getByRole("button", { name: "Save" })).toBeTruthy());
	});
});
