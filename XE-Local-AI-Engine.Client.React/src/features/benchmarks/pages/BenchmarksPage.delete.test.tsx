// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { delay, http, HttpResponse } from "msw";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// Deleting a project is a page behaviour, not a component one: the node refuses the delete while any run exists, and
// the only thing that moves the operator OUT of the deleted project's workspace is the page controller noticing it has
// left the refreshed list. Both need the real list + detail + controller stack, so this drives the whole page over MSW.
const { toastErrorMock, toastSuccessMock, hubMock } = vi.hoisted(() => ({
	toastErrorMock: vi.fn(),
	toastSuccessMock: vi.fn(),
	hubMock: vi.fn(),
}));

vi.mock("@/core/ui/notifications/Toast", () => ({
	toast: { error: toastErrorMock, success: toastSuccessMock, info: vi.fn() },
}));
// A selected run mounts the live pane, which opens a SignalR hub; it has its own suite.
vi.mock("@/features/benchmarks/hooks/useBenchmarkRunHub", () => ({ useBenchmarkRunHub: hubMock }));

import { BenchmarksPage } from "@/features/benchmarks/pages/BenchmarksPage";
import { jsonRoute, localApiPath, problemDetailsRoute } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

const projectId = "aaaaaaaa-0000-4000-8000-000000000001";
const otherProjectId = "aaaaaaaa-0000-4000-8000-000000000002";

function projectRow(id: string, name: string, overrides: Record<string, unknown> = {}) {
	return {
		id,
		name,
		contextTokens: 4096,
		agentDefinitionId: "cccccccc-0000-4000-8000-000000000003",
		judgeEnabled: false,
		runCount: 0,
		isFrozen: false,
		version: 3,
		createdAtUtc: 1,
		updatedAtUtc: 2,
		...overrides,
	};
}

function projectDetail(id: string, name: string, overrides: Record<string, unknown> = {}) {
	return {
		...projectRow(id, name, overrides),
		coreTask: `Task of ${name}.`,
		judge: { enabled: false },
	};
}

function runRow(id: string, projectOf: string, primaryStatus: string) {
	return {
		id,
		projectId: projectOf,
		primaryModelName: "model.gguf",
		modelContentFingerprint: "v1:test",
		modelGroupKey: "v1:test",
		agentName: "Summariser",
		agentVersion: 1,
		requestedContextTokens: 4096,
		primaryStatus,
		judge: { state: "disabled" },
		qualityScoreSource: "none",
		version: 3,
		createdAtUtc: 1,
		updatedAtUtc: 2,
		outputParts: [],
	};
}

/**
 * Serves the whole page for a list the test can mutate between refetches, which is what makes "the row disappears"
 * observable: the DELETE handler removes the row and the list GET is re-read afterwards.
 */
function pageRoutes(
	rows: Record<string, unknown>[],
	details: (Record<string, unknown> & { id: string })[],
	taskItemCount = 2,
	runs: Record<string, unknown>[] = [],
) {
	server.use(
		http.get(localApiPath("benchmarks/projects"), () => HttpResponse.json({ items: rows })),
		jsonRoute("get", "benchmarks/eligible-models", { items: [] }),
		jsonRoute("get", "agents", { items: [] }),
		...details.flatMap((detail) => [
			jsonRoute("get", `benchmarks/projects/${detail.id}`, detail),
			jsonRoute("get", `benchmarks/projects/${detail.id}/runs`, {
				items: runs.filter((run) => run["projectId"] === detail.id),
				rankCohort: { rankedCount: 0, totalScored: 0 },
			}),
			jsonRoute("get", `benchmarks/projects/${detail.id}/items`, {
				items: Array.from({ length: taskItemCount }, (_, index) => ({
					id: `dddddddd-0000-4000-8000-00000000000${index + 1}`,
					index,
					kind: "prompt",
					prompt: `Question ${index + 1}.`,
					inputHash: `v1:${index}`,
				})),
				taskItemSetHash: "v1:hash",
				projectVersion: 3,
			}),
		]),
	);
}

describe("BenchmarksPage project delete", () => {
	beforeEach(() => {
		toastErrorMock.mockClear();
		toastSuccessMock.mockClear();
	});
	afterEach(cleanup);

	it("names the project and what goes with it in the confirmation", async () => {
		pageRoutes([projectRow(projectId, "Summarisation")], [projectDetail(projectId, "Summarisation")]);

		renderWithProviders(<BenchmarksPage />);

		fireEvent.click(await screen.findByTestId("benchmark-project-delete"));

		expect(await screen.findByText("Delete this benchmark project?")).toBeTruthy();
		// The item count is the page's own already-loaded list, not a count fetched for the dialog.
		expect(
			screen.getByText(
				"“Summarisation” is deleted with its 2 task items — their prompts, reference answers and verifier settings — and its whole judge history. This cannot be undone.",
			),
		).toBeTruthy();
	});

	// A live round read "with its 1 task items". The sentence is pluralised through the bundle's `_one`/`_other`
	// siblings, and the singular form drops the possessive plural in its middle clause too.
	it("uses the singular sentence for a project with one task item", async () => {
		pageRoutes([projectRow(projectId, "Summarisation")], [projectDetail(projectId, "Summarisation")], 1);

		renderWithProviders(<BenchmarksPage />);

		fireEvent.click(await screen.findByTestId("benchmark-project-delete"));

		expect(
			await screen.findByText(
				"“Summarisation” is deleted with its 1 task item — its prompt, reference answer and verifier settings — and its whole judge history. This cannot be undone.",
			),
		).toBeTruthy();
	});

	// A destructive confirmation that under-reports is worse than one that says less: the item query has no placeholder
	// data, so between selecting a project and its items arriving `data` is undefined — and "0 task items" about a
	// project that has several is a sentence the operator would act on.
	it("never claims a count while the task items are still loading", async () => {
		pageRoutes([projectRow(projectId, "Summarisation")], [projectDetail(projectId, "Summarisation")]);
		// Held open for the whole test: "still loading" is the state under test, and the query aborts on unmount.
		server.use(
			http.get(localApiPath(`benchmarks/projects/${projectId}/items`), async () => {
				await delay("infinite");
				return new HttpResponse(null, { status: 204 });
			}),
		);

		renderWithProviders(<BenchmarksPage />);

		fireEvent.click(await screen.findByTestId("benchmark-project-delete"));

		expect(
			await screen.findByText(
				"“Summarisation” is deleted with its task items — their prompts, reference answers and verifier settings — and its whole judge history. This cannot be undone.",
			),
		).toBeTruthy();
		expect(screen.queryByText(/0 task items/)).toBeNull();
	});

	// A failed item read must not take the delete with it: the operator still has to be able to remove the project, and
	// the sentence must not invent a count it could not read.
	it("still deletes, without a count, when the task items could not be read", async () => {
		const rows = [projectRow(projectId, "Summarisation")];
		let deleteCalls = 0;
		pageRoutes(rows, [projectDetail(projectId, "Summarisation")]);
		server.use(
			problemDetailsRoute("get", `benchmarks/projects/${projectId}/items`, 500, { detail: "The item store is offline." }),
			http.delete(localApiPath(`benchmarks/projects/${projectId}`), () => {
				deleteCalls += 1;
				rows.length = 0;
				return new HttpResponse(null, { status: 204 });
			}),
		);

		renderWithProviders(<BenchmarksPage />);

		fireEvent.click(await screen.findByTestId("benchmark-project-delete"));

		expect(
			await screen.findByText(
				"“Summarisation” is deleted with its task items — their prompts, reference answers and verifier settings — and its whole judge history. This cannot be undone.",
			),
		).toBeTruthy();
		expect(screen.queryByText(/0 task items/)).toBeNull();

		fireEvent.click(screen.getByTestId("benchmark-project-delete-accept"));

		await waitFor(() => expect(deleteCalls).toBe(1));
		expect(toastSuccessMock).toHaveBeenCalledWith("Benchmark project deleted.");
	});

	it("sends nothing when the confirmation is cancelled", async () => {
		let deleteCalls = 0;
		pageRoutes([projectRow(projectId, "Summarisation")], [projectDetail(projectId, "Summarisation")]);
		server.use(
			http.delete(localApiPath(`benchmarks/projects/${projectId}`), () => {
				deleteCalls += 1;
				return new HttpResponse(null, { status: 204 });
			}),
		);

		renderWithProviders(<BenchmarksPage />);

		fireEvent.click(await screen.findByTestId("benchmark-project-delete"));
		fireEvent.click(await screen.findByRole("button", { name: "Cancel" }));

		await waitFor(() => expect(screen.queryByText("Delete this benchmark project?")).toBeNull());
		expect(deleteCalls).toBe(0);
		expect(screen.getByRole("heading", { name: "Summarisation" })).toBeTruthy();
	});

	// The delete carries the project's version (the node's optimistic-concurrency contract) and the row is gone from the
	// list once it is re-read — which is also what moves the operator off the deleted project's workspace, because the
	// controller re-selects when the id it holds is no longer in the list.
	it("deletes the project, drops its row and leaves the deleted workspace", async () => {
		const rows = [projectRow(projectId, "Summarisation"), projectRow(otherProjectId, "Translation")];
		let observedBody: unknown;
		pageRoutes(rows, [projectDetail(projectId, "Summarisation"), projectDetail(otherProjectId, "Translation")]);
		server.use(
			http.delete(localApiPath(`benchmarks/projects/${projectId}`), async ({ request }) => {
				observedBody = await request.json();
				rows.splice(
					rows.findIndex((row) => row.id === projectId),
					1,
				);
				return new HttpResponse(null, { status: 204 });
			}),
		);

		renderWithProviders(<BenchmarksPage />);

		fireEvent.click(await screen.findByTestId("benchmark-project-delete"));
		fireEvent.click(await screen.findByTestId("benchmark-project-delete-accept"));

		await waitFor(() => expect(screen.getByRole("heading", { name: "Translation" })).toBeTruthy());
		expect(observedBody).toEqual({ expectedVersion: 3 });
		expect(screen.queryByText("Summarise the attached text.")).toBeNull();
		expect(toastSuccessMock).toHaveBeenCalledWith("Benchmark project deleted.");
		await waitFor(() => expect(screen.queryByText("Delete this benchmark project?")).toBeNull());
	});

	// A refusal is a 409 the operator can act on (refresh and retry), so the dialog must survive it.
	it("keeps the dialog open and reports the node's own sentence when the delete is refused", async () => {
		pageRoutes([projectRow(projectId, "Summarisation")], [projectDetail(projectId, "Summarisation")]);
		server.use(
			http.delete(localApiPath(`benchmarks/projects/${projectId}`), () =>
				HttpResponse.json(
					{
						type: "about:blank",
						title: "Conflict",
						status: 409,
						detail: "The resource version changed. Refresh and retry.",
						code: "VersionConflict",
					},
					{ status: 409, headers: { "content-type": "application/problem+json" } },
				),
			),
		);

		renderWithProviders(<BenchmarksPage />);

		fireEvent.click(await screen.findByTestId("benchmark-project-delete"));
		fireEvent.click(await screen.findByTestId("benchmark-project-delete-accept"));

		await waitFor(() => expect(toastErrorMock).toHaveBeenCalledWith("The resource version changed. Refresh and retry."));
		expect(screen.getByText("Delete this benchmark project?")).toBeTruthy();
		expect(screen.getByTestId("benchmark-project-delete-accept")).toBeTruthy();
	});

	// A project whose runs are all finished IS deletable — that is the whole point of the cascade — and the run count
	// is the project's own server-side figure, stated on its own line because it is the half of the loss an operator
	// is most likely to regret.
	it("offers the delete on a frozen project with finished runs and names the run count", async () => {
		const frozen = { runCount: 3, isFrozen: true };
		pageRoutes([projectRow(projectId, "Summarisation", frozen)], [projectDetail(projectId, "Summarisation", frozen)], 2, [
			runRow("11111111-0000-4000-8000-000000000001", projectId, "Succeeded"),
			runRow("11111111-0000-4000-8000-000000000002", projectId, "Failed"),
			runRow("11111111-0000-4000-8000-000000000003", projectId, "Cancelled"),
		]);

		renderWithProviders(<BenchmarksPage />);

		const button = await screen.findByTestId("benchmark-project-delete");
		await waitFor(() => expect(button.hasAttribute("disabled")).toBe(false));
		fireEvent.click(button);

		expect(await screen.findByTestId("benchmark-project-delete-runs")).toBeTruthy();
		expect(screen.getByText("Its 3 runs go with it, including every result, transcript and judge verdict.")).toBeTruthy();
	});

	it("uses the singular run sentence for a project with one run", async () => {
		const frozen = { runCount: 1, isFrozen: true };
		pageRoutes([projectRow(projectId, "Summarisation", frozen)], [projectDetail(projectId, "Summarisation", frozen)], 2, [
			runRow("11111111-0000-4000-8000-000000000001", projectId, "Succeeded"),
		]);

		renderWithProviders(<BenchmarksPage />);

		fireEvent.click(await screen.findByTestId("benchmark-project-delete"));

		expect(await screen.findByText("Its 1 run goes with it, including every result, transcript and judge verdict.")).toBeTruthy();
	});

	// The node refuses only while a run is still in play, so that is the one state the control is disabled in.
	it("disables the delete while one of the project's runs is still going", async () => {
		const frozen = { runCount: 2, isFrozen: true };
		pageRoutes([projectRow(projectId, "Summarisation", frozen)], [projectDetail(projectId, "Summarisation", frozen)], 2, [
			runRow("11111111-0000-4000-8000-000000000001", projectId, "Succeeded"),
			runRow("11111111-0000-4000-8000-000000000002", projectId, "Running"),
		]);

		renderWithProviders(<BenchmarksPage />);

		const button = await screen.findByTestId("benchmark-project-delete");
		await waitFor(() => expect(button.hasAttribute("disabled")).toBe(true));
		// Hovered on the SPAN the tooltip wraps the button in: a disabled button fires no pointer events of its own,
		// which is exactly why the explanation would otherwise be unreachable.
		fireEvent.mouseEnter(button.parentElement as HTMLElement);

		expect(
			await screen.findByText(
				"A run of this project is still going. Wait for it to finish or cancel it, then delete the project.",
			),
		).toBeTruthy();
	});

	// The confirmation must not claim a count the page has not got, and the delete must still work without one — but
	// the RUN count comes from the project detail, which is always loaded, so only the task items can be unknown.
	it("names the runs even when the task-item count is unknown", async () => {
		const frozen = { runCount: 2, isFrozen: true };
		pageRoutes([projectRow(projectId, "Summarisation", frozen)], [projectDetail(projectId, "Summarisation", frozen)], 2, [
			runRow("11111111-0000-4000-8000-000000000001", projectId, "Succeeded"),
			runRow("11111111-0000-4000-8000-000000000002", projectId, "Succeeded"),
		]);
		server.use(
			problemDetailsRoute("get", `benchmarks/projects/${projectId}/items`, 500, { detail: "The item store is offline." }),
		);

		renderWithProviders(<BenchmarksPage />);

		fireEvent.click(await screen.findByTestId("benchmark-project-delete"));

		expect(
			await screen.findByText(
				"“Summarisation” is deleted with its task items — their prompts, reference answers and verifier settings — and its whole judge history. This cannot be undone.",
			),
		).toBeTruthy();
		expect(screen.getByText("Its 2 runs go with it, including every result, transcript and judge verdict.")).toBeTruthy();
		expect(screen.queryByText(/0 task items/)).toBeNull();
	});
});
