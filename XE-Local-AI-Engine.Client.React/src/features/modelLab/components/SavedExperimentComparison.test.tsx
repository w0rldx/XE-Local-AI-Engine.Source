// @vitest-environment jsdom

// Saved runs side by side against stubbed routes (MSW): the facts alert, the first divergence, each run's candidates,
// the block means, and steering arms paired across runs.

import { fireEvent, screen, within } from "@testing-library/react";
import { HttpResponse, http } from "msw";
import { describe, expect, it } from "vitest";

import { SavedExperimentComparison } from "@/features/modelLab/components/SavedExperimentComparison";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

const first = "11111111-1111-4111-8111-111111111111";
const second = "22222222-2222-4222-8222-222222222222";

const candidate = (id: number, text: string, logprob: number) => ({ id, text, logprob });
const token = (id: number, text: string) => ({
	id,
	text,
	logprob: Math.log(0.5),
	candidates: [candidate(id, text, Math.log(0.5))],
	processedLogprob: Math.log(0.5),
	processedCandidates: [candidate(id, text, Math.log(0.5))],
});

function row(id: string, kind: string, revision: string, result: unknown, extra: Record<string, unknown> = {}) {
	return {
		id,
		status: "Succeeded",
		kind,
		repoId: "Qwen/Qwen3.5-0.8B",
		revision,
		installedModelName: "org/Tiny-GGUF:Q4_K_M",
		prompt: "hello",
		errorMessage: null,
		traceByteCount: 10,
		result,
		notes: null,
		isPinned: false,
		appVersion: "1.0.0",
		createdAtUtc: "2026-11-03T00:00:00Z",
		updatedAtUtc: "2026-11-03T00:00:00Z",
		...extra,
	};
}

function stubRun(experiment: { id: string }, layers: number[] = []): void {
	server.use(
		http.get(localApiPath(`model-lab/experiments/${experiment.id}`), () => HttpResponse.json(experiment)),
		http.get(localApiPath(`model-lab/experiments/${experiment.id}/trace`), () =>
			HttpResponse.json({
				records: layers.flatMap((layer) =>
					[0, 1].map((step) => ({
						step,
						layer,
						kind: "F",
						norm: layer + step,
						percentiles: [],
						topIndices: [],
						topValues: [],
					})),
				),
				truncated: false,
				droppedRecords: 0,
				byteCap: 1000,
			}),
		),
	);
}

const traceFacts = { device: "cuda", torch: "2.9", transformers: "4.57", cuda: "12.8", seed: 1234, maxNewTokens: 128 };

describe("SavedExperimentComparison", () => {
	it("lists every differing fact first, marks the first divergence, and shows each run's candidates", async () => {
		stubRun(
			row(first, "Trace", "rev-a", {
				...traceFacts,
				layers: [3, 7],
				text: "Hi!",
				tokens: [token(1, "Hi"), token(2, "!")],
			}),
			[3, 7],
		);
		// An older run: no CUDA, layers or app version recorded.
		stubRun(
			row(
				second,
				"Trace",
				"rev-b",
				{ ...traceFacts, cuda: null, text: "Hi?", tokens: [token(1, "Hi"), token(3, "?")] },
				{ appVersion: null, notes: "Second run, after the update." },
			),
			[3],
		);

		renderWithProviders(<SavedExperimentComparison experimentIds={[first, second]} />);

		const alert = await screen.findByTestId("model-lab-compare-alert");
		expect(alert.textContent).toContain(
			"These runs differ in: Revision. A difference in their results may come from these facts, not from the experiment.",
		);
		expect(within(alert).getByTestId("model-lab-compare-unchecked").textContent).toBe(
			"Not recorded in every run, so not checked: CUDA, App version, Traced blocks.",
		);
		const revision = screen.getByTestId("model-lab-compare-fact-revision");
		expect(revision.getAttribute("data-state")).toBe("differs");
		expect(within(revision).getByText("Differs")).toBeTruthy();
		expect(within(screen.getByTestId("model-lab-compare-fact-cuda")).getByText("Not recorded")).toBeTruthy();
		// A shared prompt is printed once.
		expect(within(screen.getByTestId("model-lab-compare-fact-prompt")).getAllByText("hello")).toHaveLength(1);
		expect(screen.getByTestId("model-lab-compare-notes").textContent).toContain("Second run, after the update.");

		expect(screen.getByTestId("model-lab-compare-divergence").textContent).toBe("The chosen tokens first differ at step 2.");
		// The revisions differ, so ids are not comparable.
		expect(screen.getByTestId("model-lab-compare-by-text").textContent).toBe(
			"These runs come from different checkpoints, so tokens are compared by their text, not by id.",
		);
		const rows = screen.getAllByTestId("model-lab-compare-step-row");
		expect(rows.map((element) => element.hasAttribute("data-divergence"))).toEqual([false, true]);
		expect(within(rows[1] as HTMLElement).getByTestId("model-lab-compare-first-difference")).toBeTruthy();

		fireEvent.click(within(rows[1] as HTMLElement).getByRole("button", { name: "Step 2" }));
		expect(within(screen.getByTestId("model-lab-compare-candidates")).getAllByTestId("model-lab-next-token-step")).toHaveLength(
			2,
		);

		const means = await screen.findByTestId("model-lab-compare-means");
		expect(within(means).getAllByRole("row")).toHaveLength(2);
		expect(means.textContent).toContain("Block 3 (F)");
		expect(means.textContent).not.toContain("Block 7");
		expect(means.textContent).toContain("3.50");
	});

	it("says per run whether a step's candidates were dropped by a long run or never recorded by an older one", async () => {
		const dropped = { ...token(2, "!"), candidates: null, processedCandidates: null };
		stubRun(row(first, "Trace", "rev-a", { ...traceFacts, text: "Hi!", tokens: [token(1, "Hi"), dropped], candidateSteps: 1 }));
		stubRun(row(second, "Trace", "rev-a", { ...traceFacts, text: "Hi!", tokens: [token(1, "Hi"), { id: 2, text: "!" }] }));

		renderWithProviders(<SavedExperimentComparison experimentIds={[first, second]} />);

		const rows = await screen.findAllByTestId("model-lab-compare-step-row");
		fireEvent.click(within(rows[1] as HTMLElement).getByRole("button", { name: "Step 2" }));

		const panels = screen.getByTestId("model-lab-compare-candidates");
		expect(within(panels).getByTestId("model-lab-candidates-notKept")).toBeTruthy();
		expect(within(panels).getByTestId("model-lab-candidates-beforeVersion").textContent).toBe(
			"This trace recorded no candidates (made before this version).",
		);
		expect(within(panels).queryAllByTestId("model-lab-next-token-step")).toHaveLength(0);
	});

	it("keeps steps selectable when every run dropped all its candidates, each saying not kept", async () => {
		const dropped = (id: number, text: string) => ({ ...token(id, text), candidates: null, processedCandidates: null });
		for (const id of [first, second]) {
			stubRun(
				row(id, "Trace", "rev-a", { ...traceFacts, text: "Hi!", tokens: [dropped(1, "Hi"), dropped(2, "!")], candidateSteps: 0 }),
			);
		}

		renderWithProviders(<SavedExperimentComparison experimentIds={[first, second]} />);

		const rows = await screen.findAllByTestId("model-lab-compare-step-row");
		fireEvent.click(within(rows[0] as HTMLElement).getByRole("button", { name: "Step 1" }));

		const panels = screen.getByTestId("model-lab-compare-candidates");
		expect(within(panels).getAllByTestId("model-lab-candidates-notKept")).toHaveLength(2);
		expect(within(panels).queryByTestId("model-lab-candidates-beforeVersion")).toBeNull();
	});

	it("says no recorded fact differs, and pairs each steering arm's outputs across runs", async () => {
		const steering = (text: string) => ({
			...traceFacts,
			cuda: "12.8",
			steering: {
				layer: 15,
				scale: 1,
				vectorNorm: 1.98,
				seed: 7,
				arms: ["baseline", "vector", "random"].map((name) => ({
					name,
					build: "build 12014",
					outputs: [{ prompt: "Q1", text: `${name} ${text}`, tokenIds: [1] }],
				})),
			},
		});
		stubRun(row(first, "Steering", "rev", steering("one")));
		stubRun(row(second, "Steering", "rev", steering("two")));

		renderWithProviders(<SavedExperimentComparison experimentIds={[first, second]} />);

		expect((await screen.findByTestId("model-lab-compare-alert")).textContent).toContain(
			"No recorded fact differs between these runs.",
		);
		expect(screen.queryByTestId("model-lab-compare-steps")).toBeNull();
		const vector = screen.getByTestId("model-lab-compare-arm-vector");
		expect(within(vector).getAllByText("Q1")).toHaveLength(1);
		expect(within(vector).getByText("vector one")).toBeTruthy();
		expect(within(vector).getByText("vector two")).toBeTruthy();
	});
});
