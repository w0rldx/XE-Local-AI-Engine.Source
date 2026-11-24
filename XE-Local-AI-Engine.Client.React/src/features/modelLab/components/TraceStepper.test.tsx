// @vitest-environment jsdom

import { existsSync, readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

import { act, fireEvent, screen, waitFor, within } from "@testing-library/react";
import i18next from "i18next";
import { HttpResponse, http } from "msw";
import { beforeAll, describe, expect, it, onTestFinished, vi } from "vitest";

import { TraceExperimentView } from "@/features/modelLab/components/TraceExperimentView";
import { TraceStepper } from "@/features/modelLab/components/TraceStepper";
import { loadModelLabStrings } from "@/features/modelLab/models/ModelLabStrings";
import { toTraceTable } from "@/features/modelLab/models/ModelLabTrace";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

const experimentId = "22222222-2222-4222-8222-222222222222";
const candidate = (id: number, text: string, logprob: number) => ({ id, text, logprob });

// A contract-3 run: three tokens, a linear block 3 and a full block 7 traced, no attention.
const tokens = [
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
		id: 7,
		text: "\n",
		logprob: Math.log(0.3),
		candidates: [candidate(7, "\n", Math.log(0.3))],
		processedLogprob: Math.log(0.3),
		processedCandidates: [candidate(7, "\n", Math.log(0.3))],
	},
];

const records = [0, 1, 2].flatMap((step) => [
	{ step, layer: 3, kind: "L", norm: step + 1 },
	{ step, layer: 7, kind: "F", norm: 10 * (step + 1) },
]);

const result = { promptTokens: 3, tokens, text: "Hi!\n", seconds: 1, peakVramBytes: 10, transformers: "5.17.0" };

function stubTrace(run: Record<string, unknown> = result, traced = records): void {
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
				result: run,
				createdAtUtc: "2026-11-03T00:00:00Z",
				updatedAtUtc: "2026-11-03T00:00:00Z",
				isPinned: false,
				appVersion: null,
				notes: null,
			}),
		),
		http.get(localApiPath(`model-lab/experiments/${experimentId}/trace`), () =>
			HttpResponse.json({
				records: traced.map((record) => ({ ...record, percentiles: [], topIndices: [], topValues: [] })),
				truncated: false,
				droppedRecords: 0,
				byteCap: 1000,
			}),
		),
	);
}

async function openStepper(run?: Record<string, unknown>, traced?: typeof records): Promise<HTMLElement> {
	stubTrace(run, traced);
	renderWithProviders(<TraceExperimentView experimentId={experimentId} isLive={false} />);
	await screen.findByTestId("model-lab-trace-table");
	fireEvent.click(screen.getByRole("radio", { name: "Step through" }));
	return screen.getByTestId("model-lab-stepper");
}

function position(): string | null {
	return screen.getByTestId("model-lab-stepper-position").textContent;
}

function currentToken(): string | null | undefined {
	return screen
		.getAllByTestId("model-lab-stepper-token")
		.find((token) => token.getAttribute("aria-current") === "step")
		?.getAttribute("aria-label");
}

describe("TraceStepper", () => {
	it("keeps the table as the default view and replaces it on Step through", async () => {
		stubTrace();
		renderWithProviders(<TraceExperimentView experimentId={experimentId} isLive={false} />);

		await screen.findByTestId("model-lab-trace-table");
		expect((screen.getByRole("radio", { name: "Table" }) as HTMLInputElement).checked).toBe(true);
		expect(screen.queryByTestId("model-lab-stepper")).toBeNull();

		fireEvent.click(screen.getByRole("radio", { name: "Step through" }));

		expect(screen.queryByTestId("model-lab-trace-table")).toBeNull();
		expect(screen.getByTestId("model-lab-stepper-provenance").textContent).toBe(
			"Hugging Face checkpoint, bf16, transformers 5.17.0; not the GGUF. Every value below is from this one saved run.",
		);
	});

	it("scrubs with the step buttons, the token strip and the slider's keys", async () => {
		await openStepper();

		expect(position()).toBe('Step 1 of 3: "Hi"');
		expect(currentToken()).toBe('Step 1: "Hi"');
		expect(screen.getByRole("button", { name: "Previous step" }).hasAttribute("disabled")).toBe(true);

		fireEvent.click(screen.getByRole("button", { name: "Next step" }));
		expect(position()).toBe('Step 2 of 3: "!"');
		expect(currentToken()).toBe('Step 2: "!"');

		fireEvent.click(screen.getByRole("button", { name: 'Step 3: "\\n"' }));
		expect(position()).toBe('Step 3 of 3: "\\n"');
		expect(screen.getByRole("button", { name: "Next step" }).hasAttribute("disabled")).toBe(true);

		const slider = screen.getByRole("slider", { name: "Step" });
		fireEvent.keyDown(slider, { key: "Home" });
		expect(position()).toBe('Step 1 of 3: "Hi"');
		fireEvent.keyDown(slider, { key: "ArrowRight" });
		expect(position()).toBe('Step 2 of 3: "!"');
		fireEvent.keyDown(slider, { key: "End" });
		expect(position()).toBe('Step 3 of 3: "\\n"');
	});

	it("makes the token strip one Tab stop, on the current step, with the arrow keys between the tokens", async () => {
		await openStepper();
		fireEvent.click(screen.getByRole("button", { name: "Next step" }));

		const chips = screen.getAllByTestId("model-lab-stepper-token");
		expect(chips.map((chip) => chip.getAttribute("tabindex"))).toEqual(["-1", "0", "-1"]);
		const strip = screen.getByRole("group", { name: "Generated tokens" });

		chips[1]?.focus();
		fireEvent.keyDown(strip, { key: "ArrowRight" });
		expect(document.activeElement).toBe(chips[2]);
		fireEvent.keyDown(strip, { key: "Home" });
		expect(document.activeElement).toBe(chips[0]);
		fireEvent.keyDown(strip, { key: "End" });
		expect(document.activeElement).toBe(chips[2]);
		// Moving focus picks nothing; Enter or a click does.
		expect(position()).toBe('Step 2 of 3: "!"');
		expect(currentToken()).toBe('Step 2: "!"');
	});

	it("moves the strip's Tab stop with focus, so Tab leaves the strip from there, and back to the step when it changes", async () => {
		await openStepper();
		fireEvent.click(screen.getByRole("button", { name: "Next step" }));
		const chips = screen.getAllByTestId("model-lab-stepper-token");
		const strip = screen.getByRole("group", { name: "Generated tokens" });

		chips[1]?.focus();
		fireEvent.keyDown(strip, { key: "Home" });

		expect(chips.map((chip) => chip.getAttribute("tabindex"))).toEqual(["0", "-1", "-1"]);
		expect(nextTabStop(chips[0] as HTMLElement)?.getAttribute("aria-label")).toBe("Block 3, linear attention: residual norm 2");
		expect(currentToken()).toBe('Step 2: "!"');

		fireEvent.keyDown(strip, { key: "ArrowRight" });
		expect(chips.map((chip) => chip.getAttribute("tabindex"))).toEqual(["-1", "0", "-1"]);
		fireEvent.keyDown(strip, { key: "ArrowRight" });
		fireEvent.click(screen.getByRole("button", { name: "Previous step" }));
		// A step from the scrubber takes the Tab stop back to the selected token.
		expect(chips.map((chip) => chip.getAttribute("tabindex"))).toEqual(["0", "-1", "-1"]);
		expect(currentToken()).toBe('Step 1: "Hi"');
	});

	it("opens on the step picked in the table", async () => {
		stubTrace();
		renderWithProviders(<TraceExperimentView experimentId={experimentId} isLive={false} />);
		await screen.findByTestId("model-lab-trace-table");
		fireEvent.click(screen.getByRole("button", { name: 'Step 2: "!"' }));

		fireEvent.click(screen.getByRole("radio", { name: "Step through" }));

		expect(position()).toBe('Step 2 of 3: "!"');
	});

	it("shows each traced block's norm at the step and says why its attention is not available", async () => {
		await openStepper();

		const blocks = within(screen.getByTestId("model-lab-stepper-blocks")).getAllByRole("button");
		expect(blocks.map((block) => block.getAttribute("aria-label"))).toEqual([
			"Block 3, linear attention: residual norm 1",
			"Block 7, full attention: residual norm 10",
		]);
		// The first block is selected until another is picked: a linear one never has an attention matrix.
		expect(blocks[0]?.getAttribute("aria-pressed")).toBe("true");
		const attention = screen.getByTestId("model-lab-stepper-attention");
		expect(within(attention).getByTestId("model-lab-explain-basis").textContent).toBe("Not available");
		expect(within(attention).getByTestId("model-lab-stepper-attention-reason").textContent).toContain(
			"has no attention matrix to show",
		);

		fireEvent.click(screen.getByRole("button", { name: "Block 7, full attention: residual norm 10" }));

		expect(screen.getByRole("button", { name: "Block 7, full attention: residual norm 10" }).getAttribute("aria-pressed")).toBe(
			"true",
		);
		expect(screen.getByTestId("model-lab-stepper-selected").textContent).toBe("Block 7, full attention: residual norm 10");
		const full = screen.getByTestId("model-lab-stepper-attention");
		expect(within(full).getByTestId("model-lab-explain-basis").textContent).toBe("Not available");
		expect(within(full).getByTestId("model-lab-stepper-attention-reason").textContent).toBe("This run did not record attention.");

		fireEvent.click(screen.getByRole("button", { name: "Next step" }));
		expect(screen.getByTestId("model-lab-stepper-selected").textContent).toBe("Block 7, full attention: residual norm 20");
	});

	it("says a step past a long run's kept candidates has none kept, while an earlier step shows them", async () => {
		const [hi, bang, newline] = tokens;
		await openStepper({
			...result,
			tokens: [hi, bang, { ...newline, candidates: null, processedCandidates: null }],
			candidateSteps: 2,
		});

		fireEvent.click(screen.getByRole("button", { name: "Next step" }));
		expect(screen.getByTestId("model-lab-next-token-measured")).toBeTruthy();
		fireEvent.click(screen.getByRole("button", { name: "Next step" }));

		expect(screen.getByTestId("model-lab-candidates-notKept").textContent).toBe(
			"This step's candidates were not kept: a long run keeps them only for its first steps, so its saved result stays within its size limit. The token, its text and its probability are kept.",
		);
		expect(screen.queryByTestId("model-lab-next-token-measured")).toBeNull();
		expect(screen.queryByTestId("model-lab-candidates-beforeVersion")).toBeNull();
	});

	it("says an older run recorded no candidates, though it names no count of kept steps", async () => {
		await openStepper({ ...result, tokens: tokens.map(({ id, text }) => ({ id, text })) });

		expect(screen.getByTestId("model-lab-candidates-beforeVersion").textContent).toBe(
			"This trace recorded no candidates (made before this version).",
		);
		expect(screen.queryByTestId("model-lab-candidates-notKept")).toBeNull();
	});

	it("shows the step's raw candidates and its processed ones on the switch", async () => {
		await openStepper();

		const measured = screen.getByTestId("model-lab-next-token-measured");
		expect(measured.textContent).toContain('"Hello"');
		// The trace's own values, with their raw/processed provenance beside the badge; the rest is computed from them.
		expect(measured.textContent).toContain("Computed by PyTorch from the model's raw logits over the whole vocabulary");
		expect(
			within(measured)
				.getAllByTestId("model-lab-explain-basis")
				.map((badge) => badge.textContent),
		).toEqual(["Measured in the trace", "Derived"]);
		expect(within(screen.getByTestId("model-lab-next-token-simulated")).getByTestId("model-lab-explain-basis").textContent).toBe(
			"Simulated on this page",
		);

		fireEvent.click(screen.getByRole("radio", { name: "Processed scores" }));

		const processed = screen.getByTestId("model-lab-next-token-measured").textContent;
		expect(processed).toContain('"Hey"');
		expect(processed).not.toContain('"Hello"');
		expect(processed).toContain("Computed by PyTorch from the processed scores");
		expect(processed).toContain("Measured in the trace");
	});

	it("shows every block when the result names their kinds, marking the untraced ones", () => {
		const table = toTraceTable(records, tokens);

		renderWithProviders(
			<TraceStepper
				experimentId={experimentId}
				result={result}
				table={table}
				layerKinds={["L", "L", "L", "L", "L", "L", "L", "F"]}
				step={0}
				onStepChange={vi.fn()}
				basis="raw"
				onBasisChange={vi.fn()}
				modelName="org/Tiny-GGUF:Q4_K_M"
			/>,
		);

		const blocks = within(screen.getByTestId("model-lab-stepper-blocks")).getAllByRole("button");
		expect(blocks).toHaveLength(8);
		expect(blocks[0]?.getAttribute("aria-label")).toBe("Block 0, linear attention: not traced in this run");
		expect(blocks[7]?.getAttribute("aria-label")).toBe("Block 7, full attention: residual norm 10");
	});
});

// A contract-4 run with attention: prompt "<s>", "hel", "lo" (P = 3), eight blocks, full ones at 3 and 7, block 7 captured
// with 4 query heads on 2 key/value heads, top 2 keys. Step 2's row was cut at the cap.
const promptPieces = [
	{ id: 10, text: "<s>" },
	{ id: 11, text: "hel" },
	{ id: 12, text: "lo" },
];
const v4Result = {
	...result,
	promptPieces,
	layerKinds: "LLLFLLLF",
	attnImplementation: "sdpa",
	attention: { layers: [7], heads: 4, kvHeads: 2, headDim: 8, topKeys: 2, weightScale: 10_000, records: 2, truncated: true },
};
const v4Records = [0, 1, 2].flatMap((step) => [
	{ step, layer: 3, kind: "F", norm: step + 1 },
	{ step, layer: 7, kind: "F", norm: 10 * (step + 1) },
]);
const headRow = (keys: number[], weights: number[], rest: number) => ({ keys, weights, rest });
const attentionRows = [
	{
		step: 0,
		queryPosition: 2,
		heads: [headRow([0, 2], [0.7, 0.2], 0.1), headRow([1, 0], [0.5, 0], 0.5), headRow([0], [1], 0), headRow([0], [1], 0)],
	},
	{
		step: 1,
		queryPosition: 3,
		heads: [headRow([3, 0], [0.6, 0.3], 0.1), headRow([1, 3], [0.4, 0.4], 0.2), headRow([0], [1], 0), headRow([0], [1], 0)],
	},
];

function stubAttention(respond: () => Response = () => attentionResponse(attentionRows)): { requests: number } {
	const seen = { requests: 0 };
	server.use(
		http.get(localApiPath(`model-lab/experiments/${experimentId}/attention/7`), () => {
			seen.requests++;
			return respond();
		}),
	);
	return seen;
}

function attentionResponse(rows: unknown[]): Response {
	return HttpResponse.json({ layer: 7, heads: 4, kvHeads: 2, topKeys: 2, rows, truncated: true, droppedRecords: 1 });
}

async function chooseOption(label: string, option: string): Promise<void> {
	const select = screen.getByRole("combobox", { name: label });
	fireEvent.click(select);
	const listbox = await waitFor(() => {
		const element = document.getElementById(select.getAttribute("aria-controls") ?? "");
		if (element === null) {
			throw new Error("the dropdown is not open yet");
		}
		return element;
	});
	fireEvent.click(within(listbox).getByRole("option", { name: option, hidden: true }));
}

// Where Tab goes next: the following element in document order that takes focus from the keyboard.
function nextTabStop(from: HTMLElement): HTMLElement | undefined {
	const stops = [...document.querySelectorAll<HTMLElement>("button, input, select, textarea, a[href], [tabindex]")].filter(
		(element) => element.tabIndex >= 0 && !element.hasAttribute("disabled"),
	);
	return stops[stops.indexOf(from) + 1];
}

function rowKeyTexts(): (string | null)[] {
	return screen.getAllByTestId("model-lab-stepper-row-key").map((row) => row.textContent);
}

function mapCells(): SVGRectElement[] {
	return [...screen.getByTestId("model-lab-stepper-map").querySelectorAll<SVGRectElement>("[data-id='all-cells'] > rect")];
}

const fullBlock7 = "Block 7, full attention: residual norm 10, attention recorded";

describe("TraceStepper with recorded attention", () => {
	it("marks the blocks with recorded attention, by name and icon, and does not dim one without a residual trace", async () => {
		await openStepper(
			{ ...v4Result, attention: { ...v4Result.attention, layers: [3, 7] } },
			v4Records.filter((record) => record.layer === 7),
		);

		const blocks = within(screen.getByTestId("model-lab-stepper-blocks")).getAllByRole("button");
		expect(blocks[3]?.getAttribute("aria-label")).toBe("Block 3, full attention: not traced in this run, attention recorded");
		expect(blocks[3]?.style.opacity).toBe("");
		expect(within(blocks[3] as HTMLElement).getByTestId("model-lab-stepper-block-recorded")).toBeTruthy();
		expect(blocks[0]?.getAttribute("aria-label")).toBe("Block 0, linear attention: not traced in this run");
		expect(blocks[0]?.style.opacity).toBe("0.6");
		expect(within(blocks[0] as HTMLElement).queryByTestId("model-lab-stepper-block-recorded")).toBeNull();
		expect(blocks.filter((block) => within(block).queryByTestId("model-lab-stepper-block-recorded"))).toHaveLength(2);
		expect(screen.getByText("The eye marks the blocks whose attention this run recorded.")).toBeTruthy();
	});

	it("keeps 'not measured' apart from 'recorded' in German and writes the norm with a decimal comma", async () => {
		await loadModelLabStrings("de");
		await act(() => i18next.changeLanguage("de"));
		try {
			renderWithProviders(
				<TraceStepper
					experimentId={experimentId}
					result={{ ...v4Result, attention: { ...v4Result.attention, layers: [3] } }}
					table={toTraceTable([{ step: 0, layer: 7, kind: "F", norm: 0.725 }], tokens)}
					layerKinds={[..."LLLFLLLF"]}
					step={0}
					onStepChange={vi.fn()}
					basis="raw"
					onBasisChange={vi.fn()}
					modelName="org/Tiny-GGUF:Q4_K_M"
				/>,
			);

			const blocks = within(screen.getByTestId("model-lab-stepper-blocks")).getAllByRole("button");
			expect(blocks[0]?.getAttribute("aria-label")).toBe("Block 0, lineare Attention: in diesem Lauf nicht gemessen");
			expect(blocks[3]?.getAttribute("aria-label")).toBe(
				"Block 3, volle Attention: in diesem Lauf nicht gemessen, Attention aufgezeichnet",
			);
			expect(blocks[7]?.getAttribute("aria-label")).toBe("Block 7, volle Attention: Residual-Norm 0,725");
		} finally {
			await act(() => i18next.changeLanguage("en"));
		}
	});

	it("names the kernel, and fetches a captured block's rows only when it is selected, once per block", async () => {
		const attention = stubAttention();
		await openStepper(v4Result, v4Records);

		expect(screen.getByTestId("model-lab-stepper-provenance").textContent).toBe(
			"Hugging Face checkpoint, bf16, transformers 5.17.0, attention kernel sdpa; not the GGUF. Every value below is from this one saved run.",
		);
		const blocks = within(screen.getByTestId("model-lab-stepper-blocks")).getAllByRole("button");
		expect(blocks).toHaveLength(8);
		expect(attention.requests).toBe(0);

		fireEvent.click(screen.getByRole("button", { name: fullBlock7 }));
		await screen.findByTestId("model-lab-stepper-row");
		fireEvent.click(screen.getByRole("button", { name: "Block 0, linear attention: not traced in this run" }));
		fireEvent.click(screen.getByRole("button", { name: fullBlock7 }));
		await screen.findByTestId("model-lab-stepper-row");

		expect(attention.requests).toBe(1);
	});

	it("lists the step's stored keys with their neighbours and the rest, under a derived badge with its note", async () => {
		stubAttention();
		await openStepper(v4Result, v4Records);
		fireEvent.click(screen.getByRole("button", { name: fullBlock7 }));

		await screen.findByTestId("model-lab-stepper-row");
		const panel = screen.getByTestId("model-lab-stepper-attention");
		// Recomputed weights are derived; the head counts the selector lists are the model's own structure.
		expect(within(panel).getAllByTestId("model-lab-explain-basis")[0]?.textContent).toBe("Derived");
		expect(within(panel).getByTestId("model-lab-stepper-heads-basis").textContent).toBe("Read from the file");
		expect(screen.getByTestId("model-lab-stepper-attention-note").textContent).toBe(
			"Recomputed in fp32 from the kernel's own query/key tensors, top K kept, floored. This run kept the top 2 keys per row and head; the model has 4 query heads and 2 key/value heads.",
		);
		// Position order, never weight order; the key at the query position has no right neighbour.
		// Weight first, so it stays in view on a narrow screen.
		expect(rowKeyTexts()).toEqual(['70.00%0"<s>" "hel"', '20.00%2"hel" "lo"']);
		expect(screen.getByTestId("model-lab-stepper-row-rest").textContent).toBe("Rest of the row: 10.00%");

		fireEvent.click(screen.getByRole("button", { name: "Next step" }));
		// Position 3 is generated token 0.
		expect(rowKeyTexts()).toEqual(['30.00%0"<s>" "hel"', '60.00%3"lo" "Hi"']);
	});

	it("switches the head, naming its key/value head, and reads a floored weight as under 0.01 %", async () => {
		stubAttention();
		await openStepper(v4Result, v4Records);
		fireEvent.click(screen.getByRole("button", { name: fullBlock7 }));
		await screen.findByTestId("model-lab-stepper-row");

		await chooseOption("Head", "Head 1 (key/value head 0)");

		expect(rowKeyTexts()).toEqual(['<0.01%0"<s>" "hel"', '50.00%1"<s>" "hel" "lo"']);
		expect(screen.getByTestId("model-lab-stepper-row-rest").textContent).toBe("Rest of the row: 50.00%");
		expect(screen.getByRole("option", { name: "Head 2 (key/value head 1)", hidden: true })).toBeTruthy();
	});

	it("draws a decorative map with a distinct look per cell state, the current row outlined, and the cut rows as missing", async () => {
		stubAttention();
		await openStepper(v4Result, v4Records);
		fireEvent.click(screen.getByRole("button", { name: fullBlock7 }));
		await screen.findByTestId("model-lab-stepper-row");

		expect(screen.getByTestId("model-lab-stepper-map").getAttribute("aria-hidden")).toBe("true");
		// Head 0: columns 0, 2, 3; rows 0, 1 and the missing 2.
		const fills = mapCells().map((cell) => [cell.getAttribute("fill"), cell.getAttribute("stroke")]);
		expect(fills).toHaveLength(9);
		// Mantine's stylesheet paints any [data-empty] cell gray over its fill attribute, hiding every state below.
		expect(mapCells().some((cell) => cell.hasAttribute("data-empty"))).toBe(false);
		const [weight, , future, , notTopK, , missing] = fills;
		expect(weight?.[0]).toContain("color-mix");
		expect(future?.[0]).toBe("transparent");
		expect(notTopK?.[0]).toBe("var(--mantine-color-default-border)");
		// Hatched, outside the weight scale in both themes, and the legend swatch uses the same pattern.
		expect(missing?.[0]).toMatch(/^url\(#attention-hatch-[\w-]+\)$/);
		expect(screen.getByTestId("model-lab-stepper-legend-missing").getAttribute("fill")).toBe(missing?.[0]);
		expect(document.getElementById(missing?.[0]?.slice(5, -1) ?? "")?.tagName.toLowerCase()).toBe("pattern");
		expect(fills.slice(6).every((cell) => cell[0] === missing?.[0])).toBe(true);
		// Only this block's own missing rows, singular for one.
		expect(screen.getByTestId("model-lab-stepper-attention-truncated").textContent).toBe(
			"The attention file reached its cap: 1 row of this block was not kept, and the map marks it as missing.",
		);
		// A later position is blank with a dashed outline, as its legend swatch.
		expect(future?.[1]).toBe("var(--mantine-color-default-border)");
		expect(mapCells()[2]?.getAttribute("stroke-dasharray")).toBe("2 1");
		expect(screen.getByTestId("model-lab-stepper-map-legend").textContent).toContain("Weight, 0 to 70.00%");
		expect(screen.getByTestId("model-lab-stepper-map-current").style.top).toBe("-1px");

		await chooseOption("Head", "Head 1 (key/value head 0)");
		// Head 1, row 0, column 0 is stored but floored: an outline, no fill.
		expect(mapCells()[0]?.getAttribute("stroke")).toBe("var(--mantine-primary-color-filled)");

		fireEvent.click(screen.getByRole("button", { name: "Next step" }));
		fireEvent.click(screen.getByRole("button", { name: "Next step" }));
		expect(screen.getByTestId("model-lab-stepper-row-missing").textContent).toBe("This step's row was not kept.");
		expect(screen.getByTestId("model-lab-stepper-map-current").style.top).toBe("13px");
	});

	it("caps the map at 256 key positions and says how many it left out", async () => {
		const keys = Array.from({ length: 300 }, (_, position) => position);
		const wide = keys.map(() =>
			headRow(
				keys,
				keys.map(() => 0.001),
				0.7,
			),
		);
		stubAttention(() => attentionResponse([{ step: 0, queryPosition: 299, heads: wide }]));
		await openStepper(v4Result, v4Records);
		fireEvent.click(screen.getByRole("button", { name: fullBlock7 }));

		expect((await screen.findByTestId("model-lab-stepper-map-capped")).textContent).toBe(
			"The map shows 256 of 300 key positions; the 44 with the least summed weight are left out.",
		);
	});

	it("maps each query head to its own key/value head when the model has as many of each", async () => {
		stubAttention();
		await openStepper({ ...v4Result, attention: { ...v4Result.attention, kvHeads: 4 } }, v4Records);
		fireEvent.click(screen.getByRole("button", { name: fullBlock7 }));
		await screen.findByTestId("model-lab-stepper-row");

		expect(screen.getByTestId("model-lab-stepper-attention-note").textContent).toBe(
			"Recomputed in fp32 from the kernel's own query/key tensors, top K kept, floored. This run kept the top 2 keys per row and head; the model has 4 query heads and 4 key/value heads.",
		);
		await chooseOption("Head", "Head 3 (key/value head 3)");

		expect(screen.getAllByRole("option", { hidden: true }).map((option) => option.textContent)).toEqual(
			[0, 1, 2, 3].map((head) => `Head ${head} (key/value head ${head})`),
		);
		expect(rowKeyTexts()).toEqual(['100.00%0"<s>" "hel"']);
	});

	it("counts this block's missing rows in the plural", async () => {
		stubAttention(() => attentionResponse([attentionRows[0]]));
		await openStepper(v4Result, v4Records);
		fireEvent.click(screen.getByRole("button", { name: fullBlock7 }));

		expect((await screen.findByTestId("model-lab-stepper-attention-truncated")).textContent).toBe(
			"The attention file reached its cap: 2 rows of this block were not kept, and the map marks them as missing.",
		);
	});

	it("says every row of this block was kept when the cap cut only other blocks", async () => {
		stubAttention(() => attentionResponse([...attentionRows, { ...attentionRows[1], step: 2, queryPosition: 4 }]));
		await openStepper(v4Result, v4Records);
		fireEvent.click(screen.getByRole("button", { name: fullBlock7 }));

		expect((await screen.findByTestId("model-lab-stepper-attention-truncated")).textContent).toBe(
			"The attention file reached its cap, but every row of this block was kept.",
		);
	});

	it("says a block's rows are not kept on a 404", async () => {
		stubAttention(() => new HttpResponse(null, { status: 404 }));
		await openStepper(v4Result, v4Records);
		fireEvent.click(screen.getByRole("button", { name: fullBlock7 }));

		expect((await screen.findByTestId("model-lab-stepper-attention-failed")).textContent).toBe(
			"This run kept no attention rows for block 7.",
		);
		expect(screen.queryByTestId("model-lab-stepper-map")).toBeNull();
	});

	it("shows the server's reason when the stored rows do not read back (422)", async () => {
		stubAttention(() =>
			HttpResponse.json({ statusCode: 422, message: "The stored attention rows failed their check." }, { status: 422 }),
		);
		await openStepper(v4Result, v4Records);
		fireEvent.click(screen.getByRole("button", { name: fullBlock7 }));

		expect((await screen.findByTestId("model-lab-stepper-attention-failed")).textContent).toBe(
			"The stored attention rows failed their check.",
		);
	});

	it("explains a linear block and a full block this run did not capture as not available, without a request", async () => {
		const attention = stubAttention();
		await openStepper(v4Result, v4Records);

		const linear = screen.getByTestId("model-lab-stepper-attention");
		expect(within(linear).getAllByTestId("model-lab-explain-basis")[0]?.textContent).toBe("Not available");
		expect(within(linear).getByTestId("model-lab-stepper-attention-reason").textContent).toContain("has no attention matrix");

		fireEvent.click(screen.getByRole("button", { name: "Block 3, full attention: residual norm 1" }));

		const uncaptured = screen.getByTestId("model-lab-stepper-attention");
		expect(within(uncaptured).getAllByTestId("model-lab-explain-basis")[0]?.textContent).toBe("Not available");
		expect(within(uncaptured).getByTestId("model-lab-stepper-attention-reason").textContent).toBe(
			"This run recorded attention for blocks 7 only; this block was not among them.",
		);
		expect(attention.requests).toBe(0);
	});
});

// A contract-5 run with a logit lens over four blocks (L L L F), the top 3 kept, tied embeddings. The lens file reached
// its cap after two steps, so step 3 has no readout. Step 1's last block is one storage quantum below the raw output;
// step 2's is two.
const lensTokens = [{ ...tokens[0], logprob: -0.5108 }, { ...tokens[1], logprob: Math.log(0.5) }, tokens[2]];
const v5Result = {
	...result,
	tokens: lensTokens,
	layerKinds: "LLLF",
	attnImplementation: "sdpa",
	lens: { layers: [0, 1, 2, 3], topTokens: 3, records: 2, truncated: true, tiedEmbeddings: true, vocabSize: 248_320 },
};
const v5Records = [0, 1, 2].map((step) => ({ step, layer: 3, kind: "F", norm: step + 1 }));
const lensBlock = (
	layer: number,
	ids: number[],
	logprobs: (number | null)[],
	chosenLogprob: number | null,
	chosenRank: number | null,
	entropy: number | null,
) => ({
	layer,
	ids,
	logprobs,
	chosenLogprob,
	chosenRank,
	entropy,
});
const lensSteps = [
	{
		step: 0,
		blocks: [
			lensBlock(0, [20, 21, 22], [Math.log(0.5), Math.log(0.2), Math.log(0.1)], Math.log(0.01), 40, 9.876),
			// The chosen token shares the top value with " Hi", which is listed first.
			lensBlock(1, [21, 1, 22], [Math.log(0.4), Math.log(0.4), Math.log(0.1)], Math.log(0.4), 1, null),
			lensBlock(2, [1, 20, 21], [Math.log(0.55), Math.log(0.2), Math.log(0.1)], Math.log(0.55), 1, 1.234),
			lensBlock(3, [1, 2, 20], [-0.5109, Math.log(0.3), Math.log(0.05)], -0.5109, 1, 0.693),
		],
	},
	{
		step: 1,
		blocks: [0, 1, 2, 3].map((layer) =>
			lensBlock(
				layer,
				[4, 20, 22],
				[Math.log(0.5), Math.log(0.2), Math.log(0.1)],
				Math.log(0.5) - (layer === 3 ? 0.0002 : 0),
				1,
				2,
			),
		),
	},
];
const lensTexts = [
	{ id: 1, text: "Hi" },
	{ id: 2, text: "Hello" },
	{ id: 4, text: "!" },
	{ id: 20, text: "the" },
	{ id: 21, text: " Hi" },
	{ id: 22, text: "x" },
];

function lensResponse(overrides: Record<string, unknown> = {}): Response {
	return HttpResponse.json({
		layers: [0, 1, 2, 3],
		topTokens: 3,
		tiedEmbeddings: true,
		vocabSize: 248_320,
		steps: lensSteps,
		texts: lensTexts,
		truncated: true,
		droppedSteps: 1,
		...overrides,
	});
}

function stubLens(respond: () => Response = () => lensResponse()): { requests: number } {
	const seen = { requests: 0 };
	server.use(
		http.get(localApiPath(`model-lab/experiments/${experimentId}/lens`), () => {
			seen.requests++;
			return respond();
		}),
	);
	return seen;
}

function lensRows(): HTMLElement[] {
	return screen.getAllByTestId("model-lab-stepper-lens-row");
}

// The row header (the block), then its cells.
function cellTexts(row: HTMLElement | undefined): (string | null)[] {
	const header = within(row as HTMLElement).getByRole("rowheader");
	return [header, ...within(row as HTMLElement).getAllByRole("cell")].map((cell) => cell.textContent);
}

describe("TraceStepper with a logit lens", () => {
	// The explanation texts ship in the route's own bundle, which the page loads.
	beforeAll(() => loadModelLabStrings("en"));

	it("lists every block's readout at the step in a table, under a derived badge and its note, fetched once per run", async () => {
		const lens = stubLens();
		await openStepper(v5Result, v5Records);

		const table = await screen.findByTestId("model-lab-stepper-lens-table");
		const panel = screen.getByTestId("model-lab-stepper-lens");
		expect(within(panel).getAllByTestId("model-lab-explain-basis")[0]?.textContent).toBe("Derived");
		expect(screen.getByTestId("model-lab-stepper-lens-note").textContent).toBe(
			"The model's final norm and output layer applied to each block's output at this step. A derived readout, not what the model computed: the output layer read only the last block's output.",
		);
		expect(
			within(table)
				.getAllByRole("columnheader")
				.map((header) => header.textContent),
		).toEqual(["Block", "Lens top token", "Chosen token", "Spread (nats)"]);
		expect(lensRows().map(cellTexts)).toEqual([
			["L 0", '"the" 50.00%', "1.00%, rank 40", "9.88"],
			// Rank 1 behind another token is a tie at the top; a value that was not finite is announced, not blank.
			["L 1", '" Hi" 40.00%', "40.00%, tied for first", "–not available"],
			["L 2", '"Hi" 55.00%', "55.00%, rank 1", "1.23"],
			["F 3", '"Hi" 60.00%', "60.00%, rank 1", "0.69"],
		]);
		// One bar per block for the chosen token, hidden from assistive technology: the text beside it carries the value.
		const bars = table.querySelectorAll(".mantine-Progress-root");
		expect(bars).toHaveLength(4);
		expect([...bars].every((bar) => bar.getAttribute("aria-hidden") === "true")).toBe(true);
		// The rows add no Tab stop: the block strip stays the selector.
		expect(table.querySelectorAll("button, a[href], input, [tabindex]")).toHaveLength(0);
		expect(lensRows()[0]?.getAttribute("aria-current")).toBe("true");
		expect(within(screen.getByTestId("model-lab-stepper-lens-explain")).getAllByRole("button")).toHaveLength(5);

		fireEvent.click(screen.getByRole("button", { name: "Next step" }));
		expect(cellTexts(lensRows()[0])[1]).toBe('"!" 50.00%');
		expect(lens.requests).toBe(1);
	});

	it("shows the selected block's top tokens, the chosen token's rank and the spread as equally likely tokens", async () => {
		stubLens();
		await openStepper(v5Result, v5Records);

		const detail = await screen.findByTestId("model-lab-stepper-lens-detail");
		expect(
			within(detail)
				.getAllByTestId("model-lab-next-token-bar")
				.map((bar) => bar.textContent),
		).toEqual(['"the"50.00%', '" Hi"20.00%', '"x"10.00%']);
		// Outside the top 3: its rank and probability come from their own stored values.
		expect(screen.getByTestId("model-lab-stepper-lens-chosen").textContent).toBe('Chosen token "Hi": rank 40, 1.00%');
		expect(screen.getByTestId("model-lab-stepper-lens-spread").textContent).toBe(
			"Spread of this block's readout: 9.88 nats, about as spread as e^H ≈ 19,500 equally likely tokens (at most ln V = 12.42 nats for this vocabulary).",
		);

		fireEvent.click(screen.getByRole("button", { name: "Block 1, linear attention: not traced in this run" }));

		expect(lensRows()[1]?.getAttribute("aria-current")).toBe("true");
		expect(lensRows()[0]?.hasAttribute("aria-current")).toBe(false);
		const tied = screen.getByTestId("model-lab-stepper-lens-detail");
		expect(
			within(tied).getByTestId("model-lab-next-token-chosen").closest("[data-testid='model-lab-next-token-bar']")?.textContent,
		).toBe('"Hi"Chosen40.00%');
		expect(screen.getByTestId("model-lab-stepper-lens-chosen").textContent).toBe('Chosen token "Hi": tied for first, 40.00%');
		expect(screen.getByTestId("model-lab-stepper-lens-spread").textContent).toBe(
			"Spread of this block's readout: not available (not a finite number).",
		);
	});

	it("puts the table in a scroll region named by its caption", async () => {
		stubLens();
		await openStepper(v5Result, v5Records);
		await screen.findByTestId("model-lab-stepper-lens-table");

		const region = screen.getByRole("region", { name: 'Each block\'s lens readout at step 1; the chosen token is "Hi"' });
		expect(region.getAttribute("tabindex")).toBe("0");
		expect(within(region).getByTestId("model-lab-stepper-lens-table")).toBeTruthy();
	});

	it("scrolls a newly selected block's row into the table's scroller, and leaves it on a step change", async () => {
		stubLens();
		await openStepper(v5Result, v5Records);
		await screen.findByTestId("model-lab-stepper-lens-table");
		const region = screen.getByRole("region", { name: /lens readout at step 1/ });
		// The scroller shows 100–460 px; the selected row sits below it, at 900–930 px.
		const rect = (top: number, bottom: number) => ({ top, bottom }) as DOMRect;
		onTestFinished(() => {
			vi.restoreAllMocks();
		});
		vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(function (this: HTMLElement) {
			return this === region ? rect(100, 460) : this.getAttribute("aria-current") === "true" ? rect(900, 930) : rect(0, 0);
		});
		const scrolled = vi.spyOn(region, "scrollTop", "set");

		fireEvent.click(screen.getByRole("button", { name: "Block 3, full attention: residual norm 1" }));

		expect(scrolled).toHaveBeenCalledTimes(1);
		expect(scrolled).toHaveBeenCalledWith(470);

		fireEvent.click(screen.getByRole("button", { name: "Next step" }));
		expect(scrolled).toHaveBeenCalledTimes(1);
	});

	it("keeps a newly selected row clear of the horizontal scrollbar at the scroller's bottom edge", async () => {
		stubLens();
		await openStepper(v5Result, v5Records);
		await screen.findByTestId("model-lab-stepper-lens-table");
		const region = screen.getByRole("region", { name: /lens readout at step 1/ });
		// What ScrollArea sets while its horizontal bar shows: the bar's height as the viewport's bottom padding.
		region.style.paddingBottom = "8px";
		const rect = (top: number, bottom: number) => ({ top, bottom }) as DOMRect;
		onTestFinished(() => {
			vi.restoreAllMocks();
		});
		vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(function (this: HTMLElement) {
			return this === region ? rect(100, 460) : this.getAttribute("aria-current") === "true" ? rect(900, 930) : rect(0, 0);
		});
		const scrolled = vi.spyOn(region, "scrollTop", "set");

		fireEvent.click(screen.getByRole("button", { name: "Block 3, full attention: residual norm 1" }));

		expect(scrolled).toHaveBeenCalledWith(478);
	});

	it("shows a readout that was not a finite number as not available, never as a rank or a tie", async () => {
		const [first, ...rest] = lensSteps;
		const nan = lensBlock(0, [20, 21, 22], [null, null, null], null, null, null);
		stubLens(() => lensResponse({ steps: [{ step: 0, blocks: [nan, ...(first?.blocks.slice(1) ?? [])] }, ...rest] }));
		await openStepper(v5Result, v5Records);

		await screen.findByTestId("model-lab-stepper-lens-table");
		expect(cellTexts(lensRows()[0])).toEqual(["L 0", '"the" –not available', "–not available", "–not available"]);
		expect(screen.getByTestId("model-lab-stepper-lens-chosen").textContent).toBe(
			'Chosen token "Hi": not available (not a finite number).',
		);
		// No bar would be honest for a value that was not finite.
		expect(within(screen.getByTestId("model-lab-stepper-lens-detail")).queryAllByTestId("model-lab-next-token-bar")).toEqual([]);
		expect(screen.getByTestId("model-lab-stepper-lens-no-top").textContent).toBe(
			"This block's readout is not a finite number, so it has no top tokens to show.",
		);
		expect(screen.getByTestId("model-lab-stepper-lens").textContent).not.toContain("rank null");
		// Only block 1's real tie at the top is named.
		expect(screen.getAllByText(/tied for first/)).toHaveLength(1);
	});

	it("claims no tie when the run does not name the chosen token's id", async () => {
		stubLens();
		const [first, ...rest] = lensTokens;
		await openStepper({ ...v5Result, tokens: [{ ...first, id: undefined }, ...rest] }, v5Records);

		await screen.findByTestId("model-lab-stepper-lens-table");
		expect(cellTexts(lensRows()[1])[2]).toBe("40.00%, rank 1");
		expect(screen.queryByText(/tied for first/)).toBeNull();
	});

	it("shows the last block's lens beside the raw output with the tolerance, and warns only above it", async () => {
		stubLens();
		await openStepper(v5Result, v5Records);

		const last = await screen.findByTestId("model-lab-stepper-lens-last");
		// One storage quantum apart: within the tolerance, so both numbers and no verdict.
		expect(last.textContent).toContain(
			"Chosen token: 60.00% from the last block's lens (block 3), 60.00% in the raw model output.",
		);
		expect(screen.getByTestId("model-lab-stepper-lens-tolerance").textContent).toBe(
			"The view warns when their log-probabilities differ by more than 0.0001 nats, one step of the stored rounding.",
		);
		expect(screen.queryByTestId("model-lab-stepper-lens-mismatch")).toBeNull();

		fireEvent.click(screen.getByRole("button", { name: "Next step" }));

		expect(screen.getByTestId("model-lab-stepper-lens-last").textContent).toContain(
			"Chosen token: 49.99% from the last block's lens (block 3), 50.00% in the raw model output.",
		);
		expect(screen.getByTestId("model-lab-stepper-lens-mismatch").textContent).toBe(
			"Their log-probabilities differ by 0.0002 nats, more than the tolerance: the lens did not reproduce this step's raw output.",
		);
	});

	it("says a step past the lens cap was not kept", async () => {
		stubLens();
		await openStepper(v5Result, v5Records);
		await screen.findByTestId("model-lab-stepper-lens-table");

		expect(screen.getByTestId("model-lab-stepper-lens-truncated").textContent).toBe(
			"The lens reached its size limit: the first steps kept their readouts, later steps were not kept. The generated tokens are not affected.",
		);
		fireEvent.click(screen.getByRole("button", { name: 'Step 3: "\\n"' }));

		expect(screen.getByTestId("model-lab-stepper-lens-missing").textContent).toBe("This step's lens readout was not kept.");
		expect(screen.queryByTestId("model-lab-stepper-lens-table")).toBeNull();
		expect(screen.queryByTestId("model-lab-stepper-lens-last")).toBeNull();
	});

	it("leaves the tied-embeddings caveat out for a checkpoint with its own output layer", async () => {
		stubLens(() => lensResponse({ tiedEmbeddings: false, truncated: false, droppedSteps: 0 }));
		await openStepper({ ...v5Result, lens: { ...v5Result.lens, tiedEmbeddings: false } }, v5Records);
		await screen.findByTestId("model-lab-stepper-lens-table");

		expect(screen.queryByTestId("model-lab-stepper-lens-tied")).toBeNull();
		expect(screen.queryByTestId("model-lab-stepper-lens-truncated")).toBeNull();
	});

	it("names the tied-embeddings caveat for a checkpoint that ties them", async () => {
		stubLens();
		await openStepper(v5Result, v5Records);
		await screen.findByTestId("model-lab-stepper-lens-table");

		expect(screen.getByTestId("model-lab-stepper-lens-tied").textContent).toBe(
			"This checkpoint shares its output layer with the token embeddings, so the first blocks tend to read back the token at the current position.",
		);
	});

	it("shows a run without a lens as not available, with the reason, and never asks for one", async () => {
		const lens = stubLens();
		await openStepper(v4Result, v4Records);

		const panel = screen.getByTestId("model-lab-stepper-lens");
		expect(within(panel).getAllByTestId("model-lab-explain-basis")[0]?.textContent).toBe("Not available");
		expect(screen.getByTestId("model-lab-stepper-lens-unavailable").textContent).toBe(
			"This run has no logit lens: it was saved before lens recording, or its checkpoint has more blocks than the lens limit of 64.",
		);
		const entry = within(panel).getByTestId("model-lab-explain-entry");
		expect(entry.textContent).toContain("This run has no logit lens.");
		expect(within(entry).getByTestId("model-lab-explain-basis").textContent).toBe("Not available");
		expect(lens.requests).toBe(0);
	});

	it("says the run kept no lens on a 404", async () => {
		stubLens(() => new HttpResponse(null, { status: 404 }));
		await openStepper(v5Result, v5Records);

		expect((await screen.findByTestId("model-lab-stepper-lens-failed")).textContent).toBe("This run kept no logit lens.");
		expect(screen.queryByTestId("model-lab-stepper-lens-table")).toBeNull();
	});

	it("shows the server's reason when the stored lens does not read back (422)", async () => {
		stubLens(() => HttpResponse.json({ statusCode: 422, message: "The stored logit lens failed its check." }, { status: 422 }));
		await openStepper(v5Result, v5Records);

		expect((await screen.findByTestId("model-lab-stepper-lens-failed")).textContent).toBe(
			"The stored logit lens failed its check.",
		);
	});
});

// A contract-8 run of a mixture-of-experts checkpoint: four full blocks, each with a router choosing 3 of 64 experts, the
// weights not renormalized. The routing file reached its cap after two steps, so step 3 has no row. Weights are exact
// bf16 values, so the chips' rounding is the view's alone.
const v8Result = {
	...result,
	layerKinds: "FFFF",
	attnImplementation: "sdpa",
	routing: {
		layers: [0, 1, 2, 3],
		experts: 64,
		expertsPerToken: 3,
		normalizedTopK: false,
		weightEncoding: "bfloat16-bits",
		records: 2,
		truncated: true,
	},
};
const v8Records = [0, 1, 2].map((step) => ({ step, layer: 3, kind: "F", norm: step + 1 }));
const routed = (layer: number, experts: number[], weights: number[]) => ({ layer, experts, weights });
const routingSteps = [
	{
		step: 0,
		blocks: [
			routed(0, [5, 12, 40], [0.0625, 0.3125, 0.1875]),
			routed(1, [7, 3, 9], [0.5, 0.25, 0.125]),
			routed(2, [1, 2, 3], [0.25, 0.25, 0.25]),
			routed(3, [60, 61, 62], [0.125, 0.5, 0.25]),
		],
	},
	{
		step: 1,
		blocks: [
			routed(0, [12, 5, 33], [0.375, 0.25, 0.125]),
			routed(1, [7, 3, 9], [0.5, 0.25, 0.125]),
			routed(2, [1, 2, 3], [0.25, 0.25, 0.25]),
			routed(3, [60, 61, 62], [0.125, 0.5, 0.25]),
		],
	},
];

function routingResponse(overrides: Record<string, unknown> = {}): Response {
	return HttpResponse.json({
		layers: [0, 1, 2, 3],
		experts: 64,
		expertsPerToken: 3,
		normalizedTopK: false,
		steps: routingSteps,
		truncated: true,
		droppedSteps: 1,
		...overrides,
	});
}

function stubRouting(respond: () => Response = () => routingResponse()): { requests: number } {
	const seen = { requests: 0 };
	server.use(
		http.get(localApiPath(`model-lab/experiments/${experimentId}/routing`), () => {
			seen.requests++;
			return respond();
		}),
	);
	return seen;
}

function routingRows(): (string | null)[][] {
	return screen.getAllByTestId("model-lab-stepper-routing-row").map(cellTexts);
}

describe("TraceStepper with recorded routing", () => {
	// The explanation texts ship in the route's own bundle, which the page loads.
	beforeAll(() => loadModelLabStrings("en"));

	it("lists each block's experts by weight with their share, under a measured badge and its note, fetched once per run", async () => {
		const routing = stubRouting();
		await openStepper(v8Result, v8Records);

		const table = await screen.findByTestId("model-lab-stepper-routing-table");
		const panel = screen.getByTestId("model-lab-stepper-routing");
		expect(within(panel).getByText("Routing used to predict this token")).toBeTruthy();
		expect(within(panel).getAllByTestId("model-lab-explain-basis")[0]?.textContent).toBe("Measured in the trace");
		expect(screen.getByTestId("model-lab-stepper-routing-note").textContent).toBe(
			"Read from each block's router as the model ran in PyTorch on the Hugging Face checkpoint. The GGUF in llama.cpp may route differently.",
		);
		expect(
			within(table)
				.getAllByRole("columnheader")
				.map((header) => header.textContent),
		).toEqual(["Block", "Chosen experts and weights", "Sum of weights"]);
		// A chip's label never ellipsizes, so a narrow column cannot cut its weight off.
		for (const chip of screen.getAllByTestId("model-lab-stepper-routing-chip")) {
			expect(chip.querySelector<HTMLElement>(".mantine-Badge-label")?.style.overflow).toBe("visible");
		}
		// Heaviest first; equal weights by expert id; rounded for display only.
		expect(routingRows()).toEqual([
			["F 0", "E12 0.31E40 0.19E5 0.06", "0.56"],
			["F 1", "E7 0.50E3 0.25E9 0.13", "0.88"],
			["F 2", "E1 0.25E2 0.25E3 0.25", "0.75"],
			["F 3", "E61 0.50E62 0.25E60 0.13", "0.88"],
		]);
		expect(screen.getAllByTestId("model-lab-stepper-routing-row")[0]?.getAttribute("aria-current")).toBe("true");
		// The table adds no Tab stop: the block strip stays the selector.
		expect(table.querySelectorAll("button, a[href], input, [tabindex]")).toHaveLength(0);
		expect(within(screen.getByTestId("model-lab-stepper-routing-explain")).getAllByRole("button")).toHaveLength(4);

		fireEvent.click(screen.getByRole("button", { name: "Next step" }));
		expect(routingRows()[0]).toEqual(["F 0", "E12 0.38E5 0.25E33 0.13", "0.75"]);
		expect(routing.requests).toBe(1);
	});

	it("shows the selected block's weights as bars and its most picked experts over the saved steps, as derived", async () => {
		stubRouting();
		await openStepper(v8Result, v8Records);

		const detail = await screen.findByTestId("model-lab-stepper-routing-detail");
		expect(within(detail).getByText("Router weights of block 0")).toBeTruthy();
		const bars = within(detail).getAllByTestId("model-lab-stepper-routing-bar");
		expect(bars.map((bar) => bar.textContent)).toEqual(["E12 0.31", "E40 0.19", "E5 0.06"]);
		expect(detail.querySelectorAll(".mantine-Progress-root[aria-hidden='true']")).toHaveLength(3);
		const usage = screen.getByTestId("model-lab-stepper-routing-usage");
		expect(within(usage).getByText("Most picked in the 2 saved steps")).toBeTruthy();
		expect(within(usage).getByTestId("model-lab-explain-basis").textContent).toBe("Derived");
		expect(screen.getAllByTestId("model-lab-stepper-routing-usage-row").map((row) => row.textContent)).toEqual([
			"E5: picked in 2 of 2 steps",
			"E12: picked in 2 of 2 steps",
			"E33: picked in 1 of 2 steps",
		]);

		fireEvent.click(screen.getByRole("button", { name: "Block 3, full attention: residual norm 1" }));

		expect(screen.getAllByTestId("model-lab-stepper-routing-row")[3]?.getAttribute("aria-current")).toBe("true");
		expect(within(screen.getByTestId("model-lab-stepper-routing-detail")).getByText("Router weights of block 3")).toBeTruthy();
		expect(screen.getAllByTestId("model-lab-stepper-routing-usage-row").map((row) => row.textContent)).toEqual([
			"E60: picked in 2 of 2 steps",
			"E61: picked in 2 of 2 steps",
			"E62: picked in 2 of 2 steps",
		]);
	});

	it("leaves the share out when the weights are renormalized, and counts every step of an uncut run", async () => {
		stubRouting(() => routingResponse({ normalizedTopK: true, truncated: false, droppedSteps: 0 }));
		await openStepper({ ...v8Result, routing: { ...v8Result.routing, normalizedTopK: true, truncated: false } }, v8Records);

		const table = await screen.findByTestId("model-lab-stepper-routing-table");
		expect(
			within(table)
				.getAllByRole("columnheader")
				.map((header) => header.textContent),
		).toEqual(["Block", "Chosen experts and weights"]);
		expect(routingRows()[0]).toEqual(["F 0", "E12 0.31E40 0.19E5 0.06"]);
		expect(
			within(screen.getByTestId("model-lab-stepper-routing-usage")).getByText("Most picked in this run (2 steps)"),
		).toBeTruthy();
		expect(screen.queryByTestId("model-lab-stepper-routing-truncated")).toBeNull();
	});

	it("counts a single saved step in the singular", async () => {
		stubRouting(() => routingResponse({ steps: routingSteps.slice(0, 1) }));
		await openStepper(v8Result, v8Records);

		const usage = await screen.findByTestId("model-lab-stepper-routing-usage");
		expect(within(usage).getByText("Most picked in the 1 saved step")).toBeTruthy();
		expect(screen.getAllByTestId("model-lab-stepper-routing-usage-row")[0]?.textContent).toBe("E5: picked in 1 of 1 step");
	});

	it("says a step past the routing cap was not kept", async () => {
		stubRouting();
		await openStepper(v8Result, v8Records);
		await screen.findByTestId("model-lab-stepper-routing-table");

		expect(screen.getByTestId("model-lab-stepper-routing-truncated").textContent).toBe(
			"The routing reached its size limit: the first steps kept their rows, later steps were not kept. The generated tokens are not affected.",
		);
		fireEvent.click(screen.getByRole("button", { name: 'Step 3: "\\n"' }));

		expect(screen.getByTestId("model-lab-stepper-routing-missing").textContent).toBe("This step's routing was not kept.");
		expect(screen.queryByTestId("model-lab-stepper-routing-table")).toBeNull();
		expect(screen.queryByTestId("model-lab-stepper-routing-detail")).toBeNull();
		// The usage still counts the saved steps.
		expect(screen.getAllByTestId("model-lab-stepper-routing-usage-row")).toHaveLength(3);
	});

	it("shows a run without routing as not available, with the reason, and never asks for it", async () => {
		const routing = stubRouting();
		stubLens();
		await openStepper(v5Result, v5Records);

		const panel = screen.getByTestId("model-lab-stepper-routing");
		expect(within(panel).getAllByTestId("model-lab-explain-basis")[0]?.textContent).toBe("Not available");
		expect(screen.getByTestId("model-lab-stepper-routing-unavailable").textContent).toBe(
			"This run has no routing: it was saved before routing recording, it is a steering run, its checkpoint has no mixture-of-experts router, or its routing is past what a trace records (more than 64 blocks, more than 1024 experts or more than 16 experts per token).",
		);
		const entry = within(panel).getByTestId("model-lab-explain-entry");
		expect(entry.textContent).toContain("This run has no routing.");
		expect(within(entry).getByTestId("model-lab-explain-basis").textContent).toBe("Not available");
		expect(routing.requests).toBe(0);
	});

	it("jumps to the selected block in Architecture with this run laid over it", async () => {
		stubRouting();
		stubTrace(v8Result, v8Records);
		const onJump = vi.fn();
		renderWithProviders(<TraceExperimentView experimentId={experimentId} isLive={false} onJump={onJump} />);
		await screen.findByTestId("model-lab-trace-table");
		fireEvent.click(screen.getByRole("radio", { name: "Step through" }));
		await screen.findByTestId("model-lab-stepper-routing-table");
		fireEvent.click(screen.getByRole("button", { name: "Block 3, full attention: residual norm 1" }));

		const routing = within(screen.getByTestId("model-lab-stepper-routing"));
		fireEvent.click(routing.getByRole("button", { name: "Show block 3 in Architecture" }));

		expect(onJump).toHaveBeenCalledExactlyOnceWith({
			tab: "architecture",
			modelName: "org/Tiny-GGUF:Q4_K_M",
			traceId: experimentId,
			blockIndex: 3,
		});
	});

	it("offers no jump to Architecture for a block the run did not route", async () => {
		// Block 3 has residual records but no router rows.
		stubRouting(() =>
			routingResponse({
				layers: [0, 1, 2],
				steps: routingSteps.map((entry) => ({ ...entry, blocks: entry.blocks.filter((block) => block.layer !== 3) })),
			}),
		);
		stubTrace(v8Result, v8Records);
		renderWithProviders(<TraceExperimentView experimentId={experimentId} isLive={false} onJump={vi.fn()} />);
		await screen.findByTestId("model-lab-trace-table");
		fireEvent.click(screen.getByRole("radio", { name: "Step through" }));
		await screen.findByTestId("model-lab-stepper-routing-table");
		const block = screen.getByRole("button", { name: "Block 3, full attention: residual norm 1" });
		fireEvent.click(block);

		expect(block.getAttribute("aria-pressed")).toBe("true");
		expect(screen.queryByTestId("model-lab-stepper-routing-show-block")).toBeNull();
	});

	it("offers no jump to Architecture without a page to jump in", async () => {
		stubRouting();
		await openStepper(v8Result, v8Records);
		await screen.findByTestId("model-lab-stepper-routing-table");

		expect(screen.queryByTestId("model-lab-stepper-routing-show-block")).toBeNull();
	});

	it("says the run kept no routing on a 404", async () => {
		stubRouting(() => new HttpResponse(null, { status: 404 }));
		await openStepper(v8Result, v8Records);

		expect((await screen.findByTestId("model-lab-stepper-routing-failed")).textContent).toBe("This run kept no routing.");
		expect(screen.queryByTestId("model-lab-stepper-routing-table")).toBeNull();
		// A failed read claims no measurement.
		expect(within(screen.getByTestId("model-lab-stepper-routing")).queryByText("Measured in the trace")).toBeNull();
	});

	it("shows the server's reason when the stored routing does not read back (422)", async () => {
		stubRouting(() => HttpResponse.json({ statusCode: 422, message: "The stored routing failed its check." }, { status: 422 }));
		await openStepper(v8Result, v8Records);

		expect((await screen.findByTestId("model-lab-stepper-routing-failed")).textContent).toBe(
			"The stored routing failed its check.",
		);
	});
});

// The stepper replays one saved checkpoint run. Its module graph must not reach Next token, Same-prompt, direct
// inference's server calls or any live hub, so no GGUF result can leak into it. DirectInferenceModels stays allowed:
// it only formats token text and percentages.
describe("TraceStepper's imports", () => {
	const srcDir = resolve(dirname(fileURLToPath(import.meta.url)), "..", "..", "..");
	const forbidden = /NextTokenExplorer|SamePrompt|DirectInferenceApi|useDirectInferenceModels|Hub|signalr/i;

	function resolveSource(specifier: string, from: string): string | undefined {
		const base = specifier.startsWith("@/") ? join(srcDir, specifier.slice(2)) : resolve(dirname(from), specifier);
		return [`${base}.tsx`, `${base}.ts`, join(base, "index.ts"), join(base, "index.tsx")].find((path) => existsSync(path));
	}

	it("reaches no Next token, Same-prompt, direct-inference API or hub module", () => {
		const seen = new Set<string>();
		const queue = [join(srcDir, "features", "modelLab", "components", "TraceStepper.tsx")];
		while (queue.length > 0) {
			const file = queue.pop() as string;
			if (seen.has(file)) {
				continue;
			}
			seen.add(file);
			const source = readFileSync(file, "utf8");
			for (const [, specifier] of source.matchAll(/from\s+"((?:@\/|\.)[^"]+)"/g)) {
				const target = specifier && resolveSource(specifier, file);
				if (target) {
					queue.push(target);
				}
			}
		}

		const reached = [...seen].map((file) => file.slice(srcDir.length));
		expect(reached).toContain("/features/modelLab/components/NextTokenStepView.tsx");
		expect(reached.filter((file) => forbidden.test(file))).toEqual([]);
	});
});
