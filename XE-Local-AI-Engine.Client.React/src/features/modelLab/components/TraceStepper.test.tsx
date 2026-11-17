// @vitest-environment jsdom

import { existsSync, readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import { HttpResponse, http } from "msw";
import { describe, expect, it, vi } from "vitest";

import { TraceExperimentView } from "@/features/modelLab/components/TraceExperimentView";
import { TraceStepper } from "@/features/modelLab/components/TraceStepper";
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
		expect(within(attention).getByTestId("model-lab-explain-basis").textContent).toBe("Not available here");
		expect(within(attention).getByTestId("model-lab-stepper-attention-reason").textContent).toContain(
			"has no attention matrix to show",
		);

		fireEvent.click(screen.getByRole("button", { name: "Block 7, full attention: residual norm 10" }));

		expect(screen.getByRole("button", { name: "Block 7, full attention: residual norm 10" }).getAttribute("aria-pressed")).toBe(
			"true",
		);
		expect(screen.getByTestId("model-lab-stepper-selected").textContent).toBe("Block 7, full attention: residual norm 10");
		const full = screen.getByTestId("model-lab-stepper-attention");
		expect(within(full).getByTestId("model-lab-explain-basis").textContent).toBe("Not available here");
		expect(within(full).getByTestId("model-lab-stepper-attention-reason").textContent).toBe("This run did not record attention.");

		fireEvent.click(screen.getByRole("button", { name: "Next step" }));
		expect(screen.getByTestId("model-lab-stepper-selected").textContent).toBe("Block 7, full attention: residual norm 20");
	});

	it("shows the step's raw candidates and its processed ones on the switch", async () => {
		await openStepper();

		expect(screen.getByTestId("model-lab-next-token-measured").textContent).toContain('"Hello"');

		fireEvent.click(screen.getByRole("radio", { name: "Processed scores" }));

		const processed = screen.getByTestId("model-lab-next-token-measured").textContent;
		expect(processed).toContain('"Hey"');
		expect(processed).not.toContain('"Hello"');
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

	it("lists the step's stored keys with their neighbours and the rest, under a measurement badge with its note", async () => {
		stubAttention();
		await openStepper(v4Result, v4Records);
		fireEvent.click(screen.getByRole("button", { name: fullBlock7 }));

		await screen.findByTestId("model-lab-stepper-row");
		const panel = screen.getByTestId("model-lab-stepper-attention");
		expect(within(panel).getAllByTestId("model-lab-explain-basis")[0]?.textContent).toBe("What this measurement means");
		expect(screen.getByTestId("model-lab-stepper-attention-note").textContent).toBe(
			"Recomputed in fp32 from the kernel's own query/key tensors, top K kept, floored. This run kept the top 2 keys per row and head; its 4 query heads share 2 key/value heads.",
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
		expect(within(linear).getByTestId("model-lab-explain-basis").textContent).toBe("Not available here");
		expect(within(linear).getByTestId("model-lab-stepper-attention-reason").textContent).toContain("has no attention matrix");

		fireEvent.click(screen.getByRole("button", { name: "Block 3, full attention: residual norm 1" }));

		const uncaptured = screen.getByTestId("model-lab-stepper-attention");
		expect(within(uncaptured).getByTestId("model-lab-explain-basis").textContent).toBe("Not available here");
		expect(within(uncaptured).getByTestId("model-lab-stepper-attention-reason").textContent).toBe(
			"This run recorded attention for blocks 7 only; this block was not among them.",
		);
		expect(attention.requests).toBe(0);
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
