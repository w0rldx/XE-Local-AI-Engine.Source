// @vitest-environment jsdom

// A row opened while it ran, with no hub at all: the view reads its saved row until it ends.

import { fireEvent, screen, within } from "@testing-library/react";
import { HttpResponse, http } from "msw";
import { describe, expect, it, vi } from "vitest";

import { TraceExperimentView } from "@/features/modelLab/components/TraceExperimentView";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

const pollMs = vi.hoisted(() => vi.fn((isLive: boolean) => (isLive ? 60_000 : 20)));

vi.mock("@/features/modelLab/models/ModelLabTrace", async (original) => ({
	...(await original<typeof import("@/features/modelLab/models/ModelLabTrace")>()),
	activeJobPollMs: pollMs,
}));

setupMswServer();

const experimentId = "11111111-1111-4111-8111-111111111111";

function steeringResult(arms: string[]) {
	return {
		seconds: 16.4,
		peakVramBytes: 10,
		device: "GPU",
		steering: {
			layer: 15,
			layerKind: "F",
			scale: 1,
			vectorNorm: 1.98,
			vectorSha256: "fd36ff9688a0c71574f4ace8795c0cd954ba302e3d1f55eb3bbc983c350295da",
			arms: arms.map((name) => ({
				name,
				runtime: "Cuda",
				build: "build 12014",
				outputs: ["Q1", "Q2"].map((prompt) => ({ prompt, text: `${name} says ${prompt}`, tokenIds: [1, 2] })),
			})),
		},
	};
}

function stubSteeringRow(result: unknown, status: string, errorMessage: string | null = null): void {
	server.use(
		http.get(localApiPath(`model-lab/experiments/${experimentId}`), () =>
			HttpResponse.json({
				id: experimentId,
				status,
				kind: "Steering",
				repoId: "Qwen/Qwen3.5-0.8B",
				revision: "rev",
				installedModelName: "org/Tiny-GGUF:Q4_K_M",
				prompt: "hello",
				errorMessage,
				traceByteCount: null,
				result,
				createdAtUtc: "2026-11-03T00:00:00Z",
				updatedAtUtc: "2026-11-03T00:00:00Z",
			}),
		),
	);
}

const candidate = (id: number, text: string, logprob: number) => ({ id, text, logprob });

// Step 0 differs after the processors; step 1 does not; step 2's choice is outside its raw candidates, and a processor
// masked them all; step 3 is a newline two ids decode to, and only the id tells the chosen one. The trace's records
// stop after step 1, as when its byte cap was reached.
const tokensWithCandidates = [
	{
		id: 1,
		text: "Hi",
		logprob: Math.log(0.6),
		candidates: [candidate(1, "Hi", Math.log(0.6)), candidate(2, "Hello", Math.log(0.3))],
		processedLogprob: Math.log(0.9),
		processedCandidates: [candidate(1, "Hi", Math.log(0.9)), candidate(3, "Hey", Math.log(0.05))],
	},
	{
		id: 4,
		text: "!",
		logprob: Math.log(0.5),
		candidates: [candidate(4, "!", Math.log(0.5))],
		processedLogprob: Math.log(0.5),
		processedCandidates: [candidate(4, "!", Math.log(0.5))],
	},
	{
		id: 5,
		text: "?",
		logprob: Math.log(0.1),
		candidates: [candidate(6, "x", Math.log(0.4))],
		processedLogprob: null,
		processedCandidates: [],
	},
	{
		id: 7,
		text: "\n",
		logprob: Math.log(0.3),
		candidates: [candidate(8, "\n", Math.log(0.5)), candidate(7, "\n", Math.log(0.3))],
		processedLogprob: Math.log(0.3),
		processedCandidates: [candidate(8, "\n", Math.log(0.5)), candidate(7, "\n", Math.log(0.3))],
	},
];

function stubSucceededTrace(tokens: unknown[]): void {
	server.use(
		http.get(localApiPath(`model-lab/experiments/${experimentId}`), () =>
			HttpResponse.json({
				id: experimentId,
				status: "Succeeded",
				kind: "Trace",
				repoId: "Qwen/Qwen3.5-0.8B",
				revision: "rev",
				installedModelName: "org/Tiny-GGUF:Q4_K_M",
				prompt: "hello",
				errorMessage: null,
				traceByteCount: 10,
				result: { promptTokens: 3, tokens, text: "Hi!", seconds: 1, peakVramBytes: 10 },
				createdAtUtc: "2026-11-03T00:00:00Z",
				updatedAtUtc: "2026-11-03T00:00:00Z",
			}),
		),
		http.get(localApiPath(`model-lab/experiments/${experimentId}/trace`), () =>
			HttpResponse.json({
				records: [0, 1].map((step) => ({
					step,
					layer: 6,
					kind: "F",
					norm: 1,
					percentiles: [],
					topIndices: [],
					topValues: [],
				})),
				truncated: false,
				droppedRecords: 0,
				byteCap: 1000,
			}),
		),
	);
}

// The button shows the token quoted with its whitespace escaped, as the step view does.
function stepButton(text: string): HTMLElement {
	const button = screen.getAllByTestId("model-lab-trace-step").find((element) => element.textContent === JSON.stringify(text));
	if (!button) {
		throw new Error(`No step button ${text}`);
	}
	return button;
}

describe("TraceExperimentView", () => {
	it("shows a selected step's raw candidates by default and its processed ones on the switch", async () => {
		stubSucceededTrace(tokensWithCandidates);

		renderWithProviders(<TraceExperimentView experimentId={experimentId} isLive={false} />);

		await screen.findByTestId("model-lab-trace-table");
		expect(screen.queryByTestId("model-lab-trace-candidates")).toBeNull();
		const first = stepButton("Hi");
		expect(first.tagName).toBe("BUTTON");
		fireEvent.click(first);

		expect(first.getAttribute("aria-pressed")).toBe("true");
		const measured = screen.getByTestId("model-lab-next-token-measured");
		expect(measured.textContent).toContain("Computed by PyTorch from the model's raw logits over the whole vocabulary");
		expect(measured.textContent).not.toContain("processors");
		const step = screen.getByTestId("model-lab-next-token-step").textContent;
		expect(step).toContain("not what generation picked");
		expect(step).not.toContain("server");
		expect(measured.textContent).toContain('"Hello"');
		expect(screen.queryByTestId("model-lab-next-token-server-defaults")).toBeNull();
		expect(screen.queryByTestId("model-lab-trace-same-candidates")).toBeNull();

		fireEvent.click(screen.getByRole("radio", { name: "Processed scores" }));

		const processed = screen.getByTestId("model-lab-next-token-measured");
		expect(processed.textContent).toContain(
			"Computed by PyTorch from the processed scores, after the checkpoint's generation config processors",
		);
		expect(processed.textContent).toContain('"Hey"');
		expect(processed.textContent).not.toContain('"Hello"');
	});

	it("says so when the processors left a step's candidates unchanged", async () => {
		stubSucceededTrace(tokensWithCandidates);

		renderWithProviders(<TraceExperimentView experimentId={experimentId} isLive={false} />);

		await screen.findByTestId("model-lab-trace-table");
		fireEvent.click(stepButton("!"));

		expect(screen.getByTestId("model-lab-trace-same-candidates")).toBeTruthy();
		expect(stepButton("Hi").getAttribute("aria-pressed")).toBe("false");
	});

	it("words a trace step's missing choice and empty candidates for the trace, not the server", async () => {
		stubSucceededTrace(tokensWithCandidates);

		renderWithProviders(<TraceExperimentView experimentId={experimentId} isLive={false} />);

		await screen.findByTestId("model-lab-trace-table");
		fireEvent.click(stepButton("?"));

		expect(screen.getByTestId("model-lab-next-token-chosen-missing").textContent).toBe(
			"The chosen token is not among these 1 candidates: generation picked it from the processed scores, where it ranked higher.",
		);

		fireEvent.click(screen.getByRole("radio", { name: "Processed scores" }));

		const step = screen.getByTestId("model-lab-next-token-step").textContent;
		expect(step).toContain("This trace recorded no candidates for this step.");
		expect(screen.getByTestId("model-lab-next-token-chosen-missing").textContent).toBe(
			"The chosen token is not among these 0 candidates.",
		);
		expect(step).not.toContain("server");
		expect(screen.queryByTestId("model-lab-next-token-server-defaults")).toBeNull();
	});

	it("keeps a row, selectable, for a step past the trace's byte cap", async () => {
		stubSucceededTrace(tokensWithCandidates);

		renderWithProviders(<TraceExperimentView experimentId={experimentId} isLive={false} />);

		await screen.findByTestId("model-lab-trace-table");
		const pastCap = stepButton("?").closest("tr");
		expect(pastCap?.textContent).toContain("–");
		fireEvent.click(stepButton("?"));
		expect(screen.getByTestId("model-lab-trace-candidates")).toBeTruthy();
	});

	it("names a newline step visibly and marks the chosen candidate by id, not by its repeated text", async () => {
		stubSucceededTrace(tokensWithCandidates);

		renderWithProviders(<TraceExperimentView experimentId={experimentId} isLive={false} />);

		await screen.findByTestId("model-lab-trace-table");
		const newline = screen.getByRole("button", { name: 'Step 4: "\\n"' });
		expect(newline.textContent).toBe('"\\n"');
		fireEvent.click(newline);

		expect(screen.queryByTestId("model-lab-next-token-chosen-missing")).toBeNull();
		const bars = within(screen.getByTestId("model-lab-next-token-measured")).getAllByTestId("model-lab-next-token-bar");
		expect(bars).toHaveLength(2);
		expect(within(bars[0] as HTMLElement).queryByTestId("model-lab-next-token-chosen")).toBeNull();
		expect(within(bars[1] as HTMLElement).getByTestId("model-lab-next-token-chosen")).toBeTruthy();
	});

	it("says an old trace recorded no candidates and keeps its rows plain", async () => {
		stubSucceededTrace([
			{ id: 1, text: "Hi" },
			{ id: 4, text: "!" },
		]);

		renderWithProviders(<TraceExperimentView experimentId={experimentId} isLive={false} />);

		expect((await screen.findByTestId("model-lab-trace-no-candidates")).textContent).toBe(
			"This trace recorded no candidates (made before this version).",
		);
		expect(screen.queryAllByTestId("model-lab-trace-step")).toHaveLength(0);
		expect(screen.getByTestId("model-lab-trace-table").textContent).toContain("Hi");
	});

	it("reaches a running row's failure with no hub event", async () => {
		let reads = 0;
		server.use(
			http.get(localApiPath(`model-lab/experiments/${experimentId}`), () => {
				reads += 1;
				const failed = reads > 1;
				return HttpResponse.json({
					id: experimentId,
					status: failed ? "Failed" : "Running",
					kind: "Trace",
					repoId: "Qwen/Qwen3.5-0.8B",
					revision: "rev",
					installedModelName: "org/Tiny-GGUF:Q4_K_M",
					prompt: "hello",
					errorMessage: failed ? "The script failed." : null,
					traceByteCount: null,
					result: null,
					createdAtUtc: "2026-11-03T00:00:00Z",
					updatedAtUtc: "2026-11-03T00:00:00Z",
				});
			}),
		);

		renderWithProviders(<TraceExperimentView experimentId={experimentId} isLive={false} />);

		expect(await screen.findByText("The script failed.")).toBeTruthy();
	});

	it.each(["Failed", "Cancelled", "Running"])("does not call a %s row with no trace measured", async (status) => {
		server.use(
			http.get(localApiPath(`model-lab/experiments/${experimentId}`), () =>
				HttpResponse.json({
					id: experimentId,
					status,
					kind: "Trace",
					repoId: "Qwen/Qwen3.5-0.8B",
					revision: "rev",
					installedModelName: "org/Tiny-GGUF:Q4_K_M",
					prompt: "hello",
					errorMessage: null,
					traceByteCount: null,
					result: null,
					createdAtUtc: "2026-11-03T00:00:00Z",
					updatedAtUtc: "2026-11-03T00:00:00Z",
				}),
			),
		);

		renderWithProviders(<TraceExperimentView experimentId={experimentId} isLive={true} />);

		expect((await screen.findByTestId("model-lab-trace-view")).textContent).not.toContain("Read by hooks");
		expect(screen.queryByTestId("model-lab-trace-measured")).toBeNull();
	});

	it("does not call a trace it cannot read measured", async () => {
		server.use(
			http.get(localApiPath(`model-lab/experiments/${experimentId}`), () =>
				HttpResponse.json({
					id: experimentId,
					status: "Succeeded",
					kind: "Trace",
					repoId: "Qwen/Qwen3.5-0.8B",
					revision: "rev",
					installedModelName: "org/Tiny-GGUF:Q4_K_M",
					prompt: "hello",
					errorMessage: null,
					traceByteCount: 10,
					result: null,
					createdAtUtc: "2026-11-03T00:00:00Z",
					updatedAtUtc: "2026-11-03T00:00:00Z",
				}),
			),
			http.get(localApiPath(`model-lab/experiments/${experimentId}/trace`), () =>
				HttpResponse.json({ title: "gone" }, { status: 404 }),
			),
		);

		renderWithProviders(<TraceExperimentView experimentId={experimentId} isLive={false} />);

		expect(await screen.findByTestId("model-lab-trace-unreadable")).toBeTruthy();
		expect(screen.queryByTestId("model-lab-trace-measured")).toBeNull();
		expect(screen.getByTestId("model-lab-trace-view").textContent).not.toContain("Read by hooks");
	});

	it("shows a steering row's arms side by side, with what they cannot say, and no trace", async () => {
		let traceReads = 0;
		stubSteeringRow(steeringResult(["baseline", "vector", "random"]), "Succeeded");
		server.use(
			http.get(localApiPath(`model-lab/experiments/${experimentId}/trace`), () => {
				traceReads += 1;
				return HttpResponse.json({ title: "gone" }, { status: 404 });
			}),
		);

		renderWithProviders(<TraceExperimentView experimentId={experimentId} isLive={false} />);

		const facts = await screen.findByTestId("model-lab-steering-facts");
		expect(facts.textContent).toContain("Layer 15 (full attention), scale 1");
		expect(facts.textContent).toContain("fd36ff9688a0");
		expect(facts.textContent).toContain("Runtime Cuda, build 12014.");
		const caveats = screen.getByTestId("model-lab-steering-caveats").textContent;
		for (const line of [
			"A changed output is not a finding on its own.",
			"A random vector also changes output",
			"BF16 checkpoint",
			"compare only with each other",
			"is an assumption",
			"by shape only",
		]) {
			expect(caveats).toContain(line);
		}
		expect(screen.getAllByTestId("model-lab-arm-baseline")[0]?.textContent).toContain("baseline says Q1");
		expect(screen.getAllByTestId("model-lab-arm-vector")[0]?.textContent).toContain("vector says Q1");
		expect(screen.getAllByTestId("model-lab-arm-random")[1]?.textContent).toContain("random says Q2");
		expect(screen.getAllByTestId("model-lab-arm-random")[1]?.textContent).toContain("Random control");
		expect(screen.queryByTestId("model-lab-trace-measured")).toBeNull();
		expect(screen.queryByTestId("model-lab-trace-facts")).toBeNull();
		expect(screen.queryByTestId("model-lab-trace-unreadable")).toBeNull();
		expect(traceReads).toBe(0);
	});

	it("keeps the finished arms of a failed run and says which did not finish", async () => {
		stubSteeringRow(steeringResult(["baseline"]), "Failed", "An arm failed.");

		renderWithProviders(<TraceExperimentView experimentId={experimentId} isLive={false} />);

		expect((await screen.findAllByTestId("model-lab-arm-baseline"))[0]?.textContent).toContain("baseline says Q1");
		expect(screen.getAllByTestId("model-lab-arm-vector")[0]?.textContent).toContain("did not finish");
		expect(screen.getByText("An arm failed.")).toBeTruthy();
	});

	it("calls an arm still to come pending while the row runs", async () => {
		stubSteeringRow(steeringResult(["baseline"]), "Running");

		renderWithProviders(<TraceExperimentView experimentId={experimentId} isLive={false} />);

		const vector = (await screen.findAllByTestId("model-lab-arm-vector"))[0];
		expect(vector?.textContent).toContain("not finished yet");
		expect(vector?.textContent).not.toContain("did not finish");
	});

	it("renders every cell of a repeated test prompt", async () => {
		const result = steeringResult(["baseline", "vector", "random"]);
		for (const arm of result.steering.arms) {
			for (const output of arm.outputs) {
				output.prompt = "the same";
			}
		}
		stubSteeringRow(result, "Succeeded");
		const consoleError = vi.spyOn(console, "error").mockImplementation(() => undefined);

		renderWithProviders(<TraceExperimentView experimentId={experimentId} isLive={false} />);

		expect(await screen.findAllByTestId("model-lab-arm-vector")).toHaveLength(2);
		expect(consoleError.mock.calls.filter(([message]) => String(message).includes("same key"))).toEqual([]);
		consoleError.mockRestore();
	});

	it("still states the limits before any arm has finished", async () => {
		stubSteeringRow(null, "Running");

		renderWithProviders(<TraceExperimentView experimentId={experimentId} isLive={true} />);

		expect(await screen.findByTestId("model-lab-steering-caveats")).toBeTruthy();
		expect(screen.queryByTestId("model-lab-arm-baseline")).toBeNull();
	});

	it("keeps only the floor while the hub is live", async () => {
		server.use(
			http.get(localApiPath(`model-lab/experiments/${experimentId}`), () =>
				HttpResponse.json({
					id: experimentId,
					status: "Running",
					kind: "Trace",
					repoId: "Qwen/Qwen3.5-0.8B",
					revision: "rev",
					installedModelName: "org/Tiny-GGUF:Q4_K_M",
					prompt: "hello",
					errorMessage: null,
					traceByteCount: null,
					result: null,
					createdAtUtc: "2026-11-03T00:00:00Z",
					updatedAtUtc: "2026-11-03T00:00:00Z",
				}),
			),
		);
		pollMs.mockClear();

		renderWithProviders(<TraceExperimentView experimentId={experimentId} isLive={true} />);

		await screen.findByTestId("model-lab-trace-view");
		expect(pollMs).toHaveBeenCalledWith(true);
		expect(pollMs).not.toHaveBeenCalledWith(false);
	});
});
