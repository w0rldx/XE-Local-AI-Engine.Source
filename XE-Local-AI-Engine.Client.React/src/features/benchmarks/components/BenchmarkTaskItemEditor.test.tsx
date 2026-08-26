// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// One query and four mutations over the generated SDK. Mocking the SDK functions keeps the test on the component's own
// rules — what it says an edit COSTS, and what it sends — with no network and no msw handler set.
const { listMock, createMock, updateMock, deleteMock, reorderMock, toastErrorMock } = vi.hoisted(() => ({
	listMock: vi.fn(),
	createMock: vi.fn(),
	updateMock: vi.fn(),
	deleteMock: vi.fn(),
	reorderMock: vi.fn(),
	toastErrorMock: vi.fn(),
}));

vi.mock("@/core/ui/notifications/Toast", () => ({
	toast: { success: vi.fn(), error: toastErrorMock, info: vi.fn(), warn: vi.fn(), warning: vi.fn(), progress: vi.fn() },
}));

vi.mock("@/core/api/generated", async (importOriginal) => ({
	...(await importOriginal<typeof import("@/core/api/generated")>()),
	listBenchmarkTaskItems: listMock,
	createBenchmarkTaskItem: createMock,
	updateBenchmarkTaskItem: updateMock,
	deleteBenchmarkTaskItem: deleteMock,
	reorderBenchmarkTaskItems: reorderMock,
}));

import { BenchmarkTaskItemEditor } from "@/features/benchmarks/components/BenchmarkTaskItemEditor";
import type { BenchmarkRubricCriterion } from "@/features/benchmarks/models/BenchmarkModels";
import { renderWithProviders } from "@/test/RenderWithProviders";

const item = (overrides: Record<string, unknown> = {}) => ({
	id: "item-1",
	projectId: "project-1",
	parentItemId: null,
	index: 0,
	kind: "prompt",
	revision: 1,
	inputHash: "v1:a",
	isLeaf: true,
	countsTowardScore: true,
	prompt: "Summarise the release notes.",
	version: 3,
	createdAtUtc: 1,
	updatedAtUtc: 1,
	...overrides,
});

const criteria: BenchmarkRubricCriterion[] = [
	{ id: "accuracy", title: "Accuracy", description: "d", weight: 1, kind: "exact", config: '{"expected":"policy"}' },
];

const render = (props: Partial<Parameters<typeof BenchmarkTaskItemEditor>[0]> = {}) =>
	renderWithProviders(
		<BenchmarkTaskItemEditor projectId="project-1" projectContextTokens={32_768} hasRuns={false} criteria={[]} {...props} />,
	);

describe("BenchmarkTaskItemEditor", () => {
	beforeEach(() => {
		vi.clearAllMocks();
		listMock.mockResolvedValue({ data: { items: [item(), item({ id: "item-2", index: 1, prompt: "Second" })], projectVersion: 9 } });
		createMock.mockResolvedValue({ data: item({ id: "item-3" }) });
		updateMock.mockResolvedValue({ data: item() });
		deleteMock.mockResolvedValue({ data: undefined });
		reorderMock.mockResolvedValue({ data: { items: [] } });
	});
	afterEach(cleanup);

	it("lists the project's items against the node's cap", async () => {
		render();

		await waitFor(() => expect(screen.getByTestId("benchmark-items-count").textContent).toContain("2 of 20"));
		expect(screen.getByTestId("benchmark-item-prompt-item-1").textContent).toBe("Summarise the release notes.");
	});

	// The whole point of the warning: an operator has to know an edit costs history BEFORE they open the form.
	it("says nothing about unranking on a project with no runs, and says it plainly on one with runs", async () => {
		render();
		await screen.findByTestId("benchmark-items-count");
		expect(screen.queryByTestId("benchmark-items-history-warning")).toBeNull();

		cleanup();
		render({ hasRuns: true });

		expect((await screen.findByTestId("benchmark-items-history-warning")).textContent).toContain("item-set-revised");
	});

	// A reorder is the ONE mutation here that costs nothing, because the item-set hash is over ids, not positions.
	it("says a reorder changes nothing", async () => {
		render({ hasRuns: true });

		expect((await screen.findByTestId("benchmark-items-history-warning")).textContent).toContain("Reordering changes nothing");
	});

	it("names the item's next revision in the edit warning", async () => {
		render({ hasRuns: true });
		fireEvent.click(await screen.findByTestId("benchmark-item-edit-item-1"));

		expect(screen.getByTestId("benchmark-item-revise-warning").textContent).toContain("r2");
	});

	it("adds an item against the project version the item list reported", async () => {
		render();
		fireEvent.click(await screen.findByTestId("benchmark-item-add"));
		fireEvent.change(screen.getByTestId("benchmark-item-prompt-input"), { target: { value: "A third question" } });
		fireEvent.click(screen.getByTestId("benchmark-item-save"));

		await waitFor(() =>
			expect(createMock).toHaveBeenCalledWith(
				expect.objectContaining({
					path: { projectId: "project-1" },
					body: expect.objectContaining({ prompt: "A third question", kind: "prompt", expectedProjectVersion: 9 }),
				}),
			),
		);
	});

	it("refuses to send an empty prompt", async () => {
		render();
		fireEvent.click(await screen.findByTestId("benchmark-item-add"));
		fireEvent.click(screen.getByTestId("benchmark-item-save"));

		expect(createMock).not.toHaveBeenCalled();
	});

	// The ITEM's version, not the project's — an edit is a write to one item.
	it("edits an item against its own version", async () => {
		render();
		fireEvent.click(await screen.findByTestId("benchmark-item-edit-item-1"));
		fireEvent.change(screen.getByTestId("benchmark-item-prompt-input"), { target: { value: "Reworded" } });
		fireEvent.click(screen.getByTestId("benchmark-item-save"));

		await waitFor(() =>
			expect(updateMock).toHaveBeenCalledWith(
				expect.objectContaining({
					path: { projectId: "project-1", itemId: "item-1" },
					body: expect.objectContaining({ prompt: "Reworded", expectedVersion: 3 }),
				}),
			),
		);
	});

	// Naming every current id IS the node's concurrency check, so the whole order goes over the wire.
	it("reorders by naming the whole new order", async () => {
		render();
		fireEvent.click(await screen.findByTestId("benchmark-item-down-item-1"));

		await waitFor(() =>
			expect(reorderMock).toHaveBeenCalledWith(expect.objectContaining({ body: { itemIds: ["item-2", "item-1"] } })),
		);
	});

	it("asks before a delete, and says what the deletion excludes", async () => {
		render();
		fireEvent.click(await screen.findByTestId("benchmark-item-delete-item-1"));

		const accept = await screen.findByTestId("benchmark-item-delete-accept");
		expect(screen.getByText(/item-set-revised/)).toBeTruthy();
		expect(deleteMock).not.toHaveBeenCalled();

		fireEvent.click(accept);

		await waitFor(() =>
			expect(deleteMock).toHaveBeenCalledWith(
				expect.objectContaining({ path: { projectId: "project-1", itemId: "item-1" }, body: { expectedVersion: 3 } }),
			),
		);
	});

	// A project always holds at least one item; the node refuses the last delete anyway.
	it("does not offer to delete the only item", async () => {
		listMock.mockResolvedValue({ data: { items: [item()], projectVersion: 9 } });
		render();

		expect(((await screen.findByTestId("benchmark-item-delete-item-1")) as HTMLButtonElement).disabled).toBe(true);
	});

	it("shows how many cases a long-context probe expands into", async () => {
		render();
		fireEvent.click(await screen.findByTestId("benchmark-item-add"));
		fireEvent.click(screen.getByTestId("benchmark-item-kind"));
		fireEvent.change(screen.getByTestId("benchmark-item-niah-context"), { target: { value: "8192, 16384" } });
		fireEvent.change(screen.getByTestId("benchmark-item-niah-depths"), { target: { value: "10, 50, 90" } });

		expect(screen.getByTestId("benchmark-item-niah-summary").textContent).toContain("6 cases");
	});

	// A probe silently truncated to the project window measures the window, not the model — so it is refused while the
	// operator is still looking at the form, naming both numbers.
	it("refuses a probe longer than the project's window and does not send it", async () => {
		render({ projectContextTokens: 8192 });
		fireEvent.click(await screen.findByTestId("benchmark-item-add"));
		fireEvent.click(screen.getByTestId("benchmark-item-kind"));
		fireEvent.change(screen.getByTestId("benchmark-item-prompt-input"), { target: { value: "Recall probe" } });
		fireEvent.change(screen.getByTestId("benchmark-item-niah-context"), { target: { value: "65536" } });
		fireEvent.click(screen.getByTestId("benchmark-item-save"));

		expect(screen.getByTestId("benchmark-item-niah-summary").textContent).toContain("8192");
		expect(createMock).not.toHaveBeenCalled();
	});

	// A recall probe is a capability measurement, not answer quality; the node's own default keeps it off the mean.
	it("starts a long-context probe off the ranked mean", async () => {
		render();
		fireEvent.click(await screen.findByTestId("benchmark-item-add"));
		fireEvent.click(screen.getByTestId("benchmark-item-kind"));

		expect((screen.getByTestId("benchmark-item-counts") as HTMLInputElement).checked).toBe(false);
	});

	// The policy owns HOW a criterion is decided; an item overrides only the configuration, so the kind is a fact here.
	it("offers a per-item override of a verifiable criterion without offering to change its kind", async () => {
		render({ criteria });
		fireEvent.click(await screen.findByTestId("benchmark-item-edit-item-1"));

		expect(screen.getByTestId("benchmark-item-override-accuracy-kind-locked")).toBeTruthy();
		expect(screen.queryByTestId("benchmark-item-override-accuracy-kind")).toBeNull();

		fireEvent.change(screen.getByTestId("benchmark-item-override-accuracy-expected"), { target: { value: "for this item" } });
		fireEvent.click(screen.getByTestId("benchmark-item-save"));

		await waitFor(() =>
			expect(updateMock).toHaveBeenCalledWith(
				expect.objectContaining({
					body: expect.objectContaining({ verifierConfig: { accuracy: { expected: "for this item" } } }),
				}),
			),
		);
	});
});
