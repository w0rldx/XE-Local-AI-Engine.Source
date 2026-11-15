// @vitest-environment jsdom

// The Next token tab against a stubbed models route (MSW) and a faked shared-hub seam: it offers llama.cpp models only,
// asks for the top candidates, lists the steps, opens one by keyboard and simulates a sampler over its candidates.

import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import i18next from "i18next";
import { HttpResponse, http } from "msw";
import { useState } from "react";
import { afterEach, beforeAll, describe, expect, it, vi } from "vitest";

import type { DirectInferenceEvent, DirectInferenceRequest } from "@/core/directInference/DirectInferenceModels";
import { NextTokenExplorer } from "@/features/modelLab/components/NextTokenExplorer";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

const hub = vi.hoisted(() => {
	const state = { requests: [] as DirectInferenceRequest[], events: [] as unknown[], hangs: false };
	const dispose = vi.fn();
	const connection = {
		state: "Connected",
		stream: vi.fn((_method: string, request: DirectInferenceRequest) => {
			state.requests.push(request);
			return {
				subscribe: (observer: { next: (event: unknown) => void; complete: () => void }) => {
					for (const event of state.events) {
						observer.next(event);
					}
					if (!state.hangs) {
						observer.complete();
					}
					return { dispose };
				},
			};
		}),
	};
	return { state, dispose, handle: { connection, whenStarted: Promise.resolve(), release: vi.fn() } };
});

vi.mock("@/core/api/signalr/SharedHubConnection", () => ({
	acquireHubConnection: () => hub.handle,
}));

setupMswServer();

beforeAll(() => {
	i18next.addResourceBundle(
		"en",
		"translation",
		{ pages: { modelLab: { nextToken: { explain: { temperature: { overview: "Fixture: temperature." } } } } } },
		true,
		true,
	);
});

// The bodies the prompt-tokens route received, in order.
let tokenizeBodies: unknown[] = [];

const promptTokens = {
	renderedPrompt: "<|im_start|>user\nSay hello there.<|im_end|>\n",
	tokens: [
		{ id: 151644, piece: "<|im_start|>", special: true },
		{ id: 872, piece: "user", special: false },
		{ id: 198, piece: "\n", special: false },
		{ id: 1149, piece: " there", special: false },
	],
	tokenCount: 4,
	truncated: false,
};

function stubPromptTokens(respond: () => Response | Promise<Response> = () => HttpResponse.json(promptTokens)): void {
	server.use(
		http.post(localApiPath("model-lab/prompt-tokens"), async ({ request }) => {
			tokenizeBodies.push(await request.json());
			return respond();
		}),
	);
}

function stubModels(reasoningModels: readonly string[] = []): void {
	stubPromptTokens();
	server.use(
		http.get(localApiPath("models"), () =>
			HttpResponse.json({
				items: [
					["model-a", "llamacpp"],
					["model-b", "llamacpp"],
					["gpt-6", "CodexOAuth"],
					["ext:lan/qwen3", "external"],
				].map(([modelName, provider]) => ({
					modelName,
					displayLabel: null,
					isSelected: false,
					kind: "Chat",
					detectedKind: "Chat",
					capabilities: [],
					isReasoningCapable: modelName !== undefined && reasoningModels.includes(modelName),
					isToolCapable: true,
					isOverridden: false,
					provider,
				})),
			}),
		),
	);
}

const logprobs: DirectInferenceEvent = {
	type: "logprobs",
	logprobs: [
		{ text: "Hmm", logprob: Math.log(0.9), isReasoning: true, alternatives: [{ text: "Hmm", logprob: Math.log(0.9) }] },
		{
			text: "Hello",
			logprob: Math.log(0.3),
			isReasoning: false,
			alternatives: [
				{ text: "Hi", logprob: Math.log(0.6) },
				{ text: "Hello", logprob: Math.log(0.3) },
			],
		},
		{
			text: " there",
			logprob: Math.log(0.01),
			isReasoning: false,
			alternatives: [
				{ text: "!", logprob: Math.log(0.7) },
				{ text: ",", logprob: Math.log(0.2) },
			],
		},
	],
};

async function openDropdown(): Promise<ReturnType<typeof within>> {
	const input = screen.getByRole("combobox", { name: "Model" });
	fireEvent.click(input);
	return within(
		await waitFor(() => {
			const element = document.getElementById(input.getAttribute("aria-controls") ?? "");
			if (element === null) {
				throw new Error("the dropdown is not open yet");
			}
			return element;
		}),
	);
}

async function run(): Promise<void> {
	fireEvent.click(await (await openDropdown()).findByRole("option", { name: "model-a", hidden: true }));
	fireEvent.change(screen.getByLabelText("Prompt"), { target: { value: "Say hello." } });
	fireEvent.click(screen.getByRole("button", { name: "Run" }));
	await waitFor(() => expect(screen.getAllByTestId("model-lab-next-token-step-button").length).toBeGreaterThan(0));
}

function stepButtons(): string[] {
	return screen.getAllByTestId("model-lab-next-token-step-button").map((button) => button.textContent ?? "");
}

function bars(testId: string): string[] {
	return within(screen.getByTestId(testId))
		.getAllByTestId("model-lab-next-token-bar")
		.map((bar) => bar.textContent ?? "");
}

describe("NextTokenExplorer", () => {
	afterEach(() => {
		tokenizeBodies = [];
		hub.state.requests = [];
		hub.state.events = [];
		hub.state.hangs = false;
		hub.dispose.mockClear();
		hub.handle.release.mockClear();
	});

	it("states what the values are and are not before any run", () => {
		stubModels();
		renderWithProviders(<NextTokenExplorer />);

		const honesty = screen.getByTestId("model-lab-next-token-honesty").textContent;
		expect(honesty).toContain("before sampling");
		expect(honesty).toContain("not tokens");
		expect(honesty).toContain("leaves out the last step");
	});

	it("offers installed llama.cpp models only", async () => {
		stubModels();
		renderWithProviders(<NextTokenExplorer />);

		const dropdown = await openDropdown();

		await dropdown.findByRole("option", { name: "model-a", hidden: true });
		expect(dropdown.queryByRole("option", { name: "gpt-6", hidden: true })).toBeNull();
		expect(dropdown.queryByRole("option", { name: "ext:lan/qwen3", hidden: true })).toBeNull();
	});

	it("offers Thinking off for a reasoning model only, starts each model from its default, and sends it", async () => {
		stubModels(["model-a"]);
		hub.state.events = [logprobs, { type: "done" }];
		renderWithProviders(<NextTokenExplorer />);
		const pick = async (name: string): Promise<void> => {
			fireEvent.click(await (await openDropdown()).findByRole("option", { name, hidden: true }));
		};

		await pick("model-b");
		expect(screen.queryByTestId("direct-inference-thinking-off")).toBeNull();
		await pick("model-a");
		fireEvent.click(screen.getByTestId("direct-inference-thinking-off"));
		await pick("model-b");
		await pick("model-a");
		expect((screen.getByTestId("direct-inference-thinking-off") as HTMLInputElement).checked).toBe(false);

		fireEvent.change(screen.getByLabelText("Prompt"), { target: { value: "Say hello." } });
		fireEvent.click(screen.getByRole("button", { name: "Run" }));
		await waitFor(() => expect(hub.state.requests).toHaveLength(1));
		expect(hub.state.requests[0]?.disableThinking).toBeUndefined();
		await waitFor(() => expect((screen.getByTestId("direct-inference-thinking-off") as HTMLInputElement).disabled).toBe(false));
		fireEvent.click(screen.getByTestId("direct-inference-thinking-off"));
		fireEvent.click(screen.getByRole("button", { name: "Run" }));

		await waitFor(() => expect(hub.state.requests).toHaveLength(2));
		expect(hub.state.requests[1]?.disableThinking).toBe(true);
	});

	it("asks for the most candidates the server allows and lists answer steps, thinking steps only on request", async () => {
		stubModels();
		hub.state.events = [{ type: "delta", textDelta: "Hello there" }, logprobs, { type: "done" }];
		renderWithProviders(<NextTokenExplorer />);

		await run();

		expect(hub.state.requests[0]?.topLogprobs).toBe(20);
		expect(hub.state.requests[0]?.modelId).toBe("model-a");
		expect(stepButtons()).toEqual(['"Hello"', '" there"']);

		fireEvent.click(screen.getByRole("switch", { name: "Show thinking steps" }));

		expect(stepButtons()).toEqual(['"Hmm"', '"Hello"', '" there"']);
		fireEvent.click(within(screen.getByRole("group", { name: "Thinking" })).getByRole("button", { name: '"Hmm"' }));
		// Numbered within its kind, so the first answer step after thinking is answer step 1.
		expect(screen.getByTestId("model-lab-next-token-step").textContent).toContain('Thinking step 1: "Hmm"');
	});

	it("moves between steps with the arrow keys and opens one with a native button", async () => {
		stubModels();
		hub.state.events = [logprobs, { type: "done" }];
		renderWithProviders(<NextTokenExplorer />);
		await run();
		const [first, second] = within(screen.getByRole("group", { name: "Answer" })).getAllByRole("button");

		expect(first?.tagName).toBe("BUTTON");
		first?.focus();
		fireEvent.keyDown(first as HTMLElement, { key: "ArrowRight" });
		expect(document.activeElement).toBe(second);
		fireEvent.keyDown(second as HTMLElement, { key: "Home" });
		expect(document.activeElement).toBe(first);
		// Enter and Space activate a native button without a handler of ours; the click is what they dispatch.
		fireEvent.click(document.activeElement as HTMLElement);

		expect(first?.getAttribute("aria-pressed")).toBe("true");
		expect(screen.getByTestId("model-lab-next-token-step").textContent).toContain('Answer step 1: "Hello"');
		expect(bars("model-lab-next-token-measured")).toEqual(['"Hi"60.00%', '"Hello"Chosen30.00%']);
		expect(screen.getByTestId("model-lab-next-token-rest").textContent).toBe("The rest of the vocabulary: 10.00%");
		expect(screen.queryByTestId("model-lab-next-token-chosen-missing")).toBeNull();
	});

	it("labels the simulation as over the shown candidates and recomputes it without touching the measured bars", async () => {
		stubModels();
		hub.state.events = [logprobs, { type: "done" }];
		renderWithProviders(<NextTokenExplorer />);
		await run();
		fireEvent.click(screen.getByRole("button", { name: '"Hello"' }));

		expect(screen.getByTestId("model-lab-next-token-sim-label").textContent).toContain(
			"Simulated over the 2 shown candidates, renormalized; not what the server sampled.",
		);
		// Renormalized over the two shown: 0.6 / 0.9 and 0.3 / 0.9.
		expect(bars("model-lab-next-token-simulated")).toEqual(['"Hi"66.67%', '"Hello"Chosen33.33%']);

		const topK = within(screen.getByTestId("model-lab-next-token-top-k")).getByRole("slider", { name: "Simulated top-k" });
		topK.focus();
		fireEvent.keyDown(topK, { key: "ArrowRight" });

		expect(bars("model-lab-next-token-simulated")).toEqual(['"Hi"100.00%', '"Hello"ChosenCut']);
		expect(bars("model-lab-next-token-measured")).toEqual(['"Hi"60.00%', '"Hello"Chosen30.00%']);
		expect(screen.getByTestId("model-lab-next-token-sim-label")).toBeTruthy();
	});

	it("says when the chosen entry is not among the candidates", async () => {
		stubModels();
		hub.state.events = [logprobs, { type: "done" }];
		renderWithProviders(<NextTokenExplorer />);
		await run();

		fireEvent.click(screen.getByRole("button", { name: '" there"' }));

		expect(screen.getByTestId("model-lab-next-token-chosen-missing").textContent).toContain(
			"The chosen entry's text matches none of the 2 candidates",
		);
		expect(screen.queryByTestId("model-lab-next-token-chosen")).toBeNull();
	});

	it("simulates min-p, which at its top keeps only the most likely candidate", async () => {
		stubModels();
		hub.state.events = [logprobs, { type: "done" }];
		renderWithProviders(<NextTokenExplorer />);
		await run();
		fireEvent.click(screen.getByRole("button", { name: '"Hello"' }));
		expect(screen.getByTestId("model-lab-next-token-server-defaults").textContent).toContain("top-k 40");

		const minP = within(screen.getByTestId("model-lab-next-token-min-p")).getByRole("slider", { name: "Simulated min-p" });
		minP.focus();
		fireEvent.keyDown(minP, { key: "End" });

		expect(bars("model-lab-next-token-simulated")).toEqual(['"Hi"100.00%', '"Hello"ChosenCut']);
	});

	it("marks only the first candidate whose text matches the chosen entry", async () => {
		stubModels();
		hub.state.events = [
			{
				type: "logprobs",
				logprobs: [
					{
						text: "a",
						logprob: Math.log(0.5),
						isReasoning: false,
						alternatives: [
							{ text: "a", logprob: Math.log(0.5) },
							{ text: "a", logprob: Math.log(0.2) },
						],
					},
				],
			},
			{ type: "done" },
		];
		renderWithProviders(<NextTokenExplorer />);
		await run();

		fireEvent.click(screen.getByRole("button", { name: '"a"' }));

		expect(bars("model-lab-next-token-measured")).toEqual(['"a"Chosen50.00%', '"a"20.00%']);
	});

	it.each([
		["length", "the answer had not started when the output limit was reached"],
		["stop", "the answer, if any, was too short to get a step"],
	])(
		"says a run finished with %s produced only thinking, and offers no step to pick while none is shown",
		async (finishReason, note) => {
			stubModels();
			hub.state.events = [
				{ type: "logprobs", logprobs: [{ text: "Hmm", logprob: -0.1, isReasoning: true, alternatives: [] }] },
				{ type: "done", finishReason },
			];
			renderWithProviders(<NextTokenExplorer />);
			fireEvent.click(await (await openDropdown()).findByRole("option", { name: "model-a", hidden: true }));
			fireEvent.change(screen.getByLabelText("Prompt"), { target: { value: "Think." } });
			fireEvent.click(screen.getByRole("button", { name: "Run" }));

			await waitFor(() => expect(screen.getByTestId("model-lab-next-token-thinking-only").textContent).toContain(note));
			expect(screen.queryByText("Pick a step to see its candidates.")).toBeNull();

			fireEvent.click(screen.getByRole("switch", { name: "Show thinking steps" }));

			expect(screen.getByText("Pick a step to see its candidates.")).toBeTruthy();
			expect(screen.queryByTestId("model-lab-next-token-thinking-only")).toBeNull();
		},
	);

	it("says only thinking arrived so far while the run is still going", async () => {
		stubModels();
		hub.state.hangs = true;
		hub.state.events = [{ type: "logprobs", logprobs: [{ text: "Hmm", logprob: -0.1, isReasoning: true, alternatives: [] }] }];
		renderWithProviders(<NextTokenExplorer />);
		fireEvent.click(await (await openDropdown()).findByRole("option", { name: "model-a", hidden: true }));
		fireEvent.change(screen.getByLabelText("Prompt"), { target: { value: "Think." } });
		fireEvent.click(screen.getByRole("button", { name: "Run" }));

		expect((await screen.findByTestId("model-lab-next-token-thinking-only")).textContent).toContain(
			"So far only thinking steps arrived",
		);
	});

	it("states how many entries of each kind were left out", async () => {
		stubModels();
		const many = (isReasoning: boolean) =>
			Array.from({ length: 501 }, () => ({ text: "x", logprob: -1, isReasoning, alternatives: [] }));
		hub.state.events = [{ type: "logprobs", logprobs: [...many(true), ...many(false)] }, { type: "done" }];
		renderWithProviders(<NextTokenExplorer />);

		await run();

		expect(screen.getByTestId("model-lab-next-token-left-out-reasoning").textContent).toContain("1 more were left out");
		expect(screen.getByTestId("model-lab-next-token-left-out-answer").textContent).toContain("1 more were left out");
	});

	it("says when the server returned no probabilities", async () => {
		stubModels();
		hub.state.events = [
			{ type: "delta", textDelta: "Hi" },
			{ type: "done", elapsedMs: 5 },
		];
		renderWithProviders(<NextTokenExplorer />);
		fireEvent.click(await (await openDropdown()).findByRole("option", { name: "model-a", hidden: true }));
		fireEvent.change(screen.getByLabelText("Prompt"), { target: { value: "Say hello." } });
		fireEvent.click(screen.getByRole("button", { name: "Run" }));

		expect(await screen.findByTestId("model-lab-next-token-none")).toBeTruthy();
	});

	it("explains each topic with its basis", () => {
		stubModels();
		renderWithProviders(<NextTokenExplorer />);
		const explain = within(screen.getByTestId("model-lab-next-token-explain"));

		fireEvent.click(explain.getByRole("button", { name: "Temperature" }));

		expect(explain.getByText("Fixture: temperature.")).toBeTruthy();
		expect(explain.getByTestId("model-lab-explain-basis").textContent).toBe("Simulated on this page");
	});

	it("leaving mid-run cancels it", async () => {
		stubModels();
		hub.state.hangs = true;
		const { unmount } = renderWithProviders(<NextTokenExplorer />);
		fireEvent.click(await (await openDropdown()).findByRole("option", { name: "model-a", hidden: true }));
		fireEvent.change(screen.getByLabelText("Prompt"), { target: { value: "Say hello." } });
		fireEvent.click(screen.getByRole("button", { name: "Run" }));
		await waitFor(() => expect(hub.state.requests).toHaveLength(1));

		unmount();

		await waitFor(() => expect(hub.dispose).toHaveBeenCalledTimes(1));
		await waitFor(() => expect(hub.handle.release).toHaveBeenCalledTimes(1));
	});
});

describe("NextTokenExplorer prompt tokens", () => {
	afterEach(() => {
		tokenizeBodies = [];
		hub.state.requests = [];
		hub.state.events = [];
		hub.state.hangs = false;
	});

	async function chooseAndRun(prompt: string): Promise<void> {
		fireEvent.click(await (await openDropdown()).findByRole("option", { name: "model-a", hidden: true }));
		fireEvent.change(screen.getByLabelText("Prompt"), { target: { value: prompt } });
		fireEvent.click(screen.getByRole("button", { name: "Run" }));
	}

	it("reads them once per run with the exact inputs it sent, and shows each token's piece and id", async () => {
		stubModels(["model-a"]);
		hub.state.events = [{ type: "delta", textDelta: "Hello" }, logprobs, { type: "done" }];
		renderWithProviders(<NextTokenExplorer />);
		fireEvent.click(await (await openDropdown()).findByRole("option", { name: "model-a", hidden: true }));
		fireEvent.change(screen.getByLabelText("System prompt (optional)"), { target: { value: "Be brief." } });
		fireEvent.click(screen.getByTestId("direct-inference-thinking-off"));
		fireEvent.change(screen.getByLabelText("Prompt"), { target: { value: "Say hello there." } });
		fireEvent.click(screen.getByRole("button", { name: "Run" }));

		const panel = within(await screen.findByTestId("model-lab-prompt-tokens"));
		expect((await panel.findByTestId("model-lab-prompt-tokens-count")).textContent).toBe(
			"The model read 4 tokens: your prompt with the chat template applied.",
		);
		expect(tokenizeBodies).toEqual([
			{ modelName: "model-a", systemPrompt: "Be brief.", prompt: "Say hello there.", disableThinking: true },
		]);
		expect(panel.getByTestId("model-lab-explain-basis").textContent).toBe("Measured by llama.cpp");
		expect(panel.queryByTestId("model-lab-prompt-tokens-truncated")).toBeNull();
		const chips = panel.getAllByTestId("model-lab-prompt-token");
		// Whitespace is drawn, and the template's marker is told apart by more than colour.
		expect(chips.map((chip) => chip.textContent)).toEqual(["<|im_start|>", "user", "↵", "·there"]);
		expect(chips.map((chip) => chip.dataset["special"])).toEqual(["true", "false", "false", "false"]);
		expect(chips[0]?.getAttribute("aria-label")).toBe('"<|im_start|>", id 151644, control token');
		expect(chips[2]?.getAttribute("aria-label")).toBe('"\\n", id 198');
		expect(chips.map((chip) => chip.tabIndex)).toEqual([0, -1, -1, -1]);

		// Keyboard focus shows the id as pointing does.
		chips[0]?.focus();
		fireEvent.keyDown(chips[0] as HTMLElement, { key: "End" });
		expect(document.activeElement).toBe(chips[3]);
		const readout = panel.getByTestId("model-lab-prompt-tokens-readout");
		expect(readout.textContent).toBe('Token 4 of 4: " there", id 1149');
		fireEvent.mouseEnter(chips[0] as HTMLElement);
		expect(readout.textContent).toBe('Token 1 of 4: "<|im_start|>", id 151644, control token');

		// A render is not a run.
		fireEvent.click(screen.getByRole("switch", { name: "Show thinking steps" }));
		expect(tokenizeBodies).toHaveLength(1);
		fireEvent.click(screen.getByRole("button", { name: "Run" }));
		await waitFor(() => expect(tokenizeBodies).toHaveLength(2));
	});

	it("says to run again when the model is no longer running, and asks only once", async () => {
		stubModels();
		stubPromptTokens(() => HttpResponse.json({ reason: "ModelNotLoaded", message: "The model is not loaded." }, { status: 409 }));
		hub.state.events = [logprobs, { type: "done" }];
		renderWithProviders(<NextTokenExplorer />);

		await chooseAndRun("Say hello.");

		expect((await screen.findByTestId("model-lab-prompt-tokens-not-loaded")).textContent).toBe(
			"The model was unloaded, or its server stopped, before its prompt tokens could be read. Run again to see them.",
		);
		expect(screen.queryByTestId("model-lab-prompt-tokens-failed")).toBeNull();
		expect(tokenizeBodies).toEqual([{ modelName: "model-a", prompt: "Say hello.", disableThinking: false }]);
	});

	it("says it is reading while the request is still out", async () => {
		stubModels();
		let answer: (response: Response) => void = () => undefined;
		const reply = new Promise<Response>((resolve) => {
			answer = resolve;
		});
		stubPromptTokens(() => reply);
		hub.state.events = [logprobs, { type: "done" }];
		renderWithProviders(<NextTokenExplorer />);

		await chooseAndRun("Say hello.");

		expect((await screen.findByTestId("model-lab-prompt-tokens-loading")).textContent).toContain(
			"A long prompt can take a moment.",
		);
		answer(HttpResponse.json(promptTokens));
		await screen.findByTestId("model-lab-prompt-tokens-count");
		expect(screen.queryByTestId("model-lab-prompt-tokens-loading")).toBeNull();
	});

	it("shows any other refusal as an error", async () => {
		stubModels();
		stubPromptTokens(() => HttpResponse.json({ title: "Broken" }, { status: 500 }));
		hub.state.events = [logprobs, { type: "done" }];
		renderWithProviders(<NextTokenExplorer />);

		await chooseAndRun("Say hello.");

		expect(await screen.findByTestId("model-lab-prompt-tokens-failed")).toBeTruthy();
		expect(screen.queryByTestId("model-lab-prompt-tokens-not-loaded")).toBeNull();
	});

	it("says when only the first tokens are shown", async () => {
		stubModels();
		stubPromptTokens(() => HttpResponse.json({ ...promptTokens, tokenCount: 9000, truncated: true }));
		hub.state.events = [logprobs, { type: "done" }];
		renderWithProviders(<NextTokenExplorer />);

		await chooseAndRun("Say hello.");

		expect((await screen.findByTestId("model-lab-prompt-tokens-truncated")).textContent).toBe(
			"Only the first 4 tokens are shown.",
		);
		expect(screen.getByTestId("model-lab-prompt-tokens-count").textContent).toContain("9000 tokens");
	});

	it("reads none for a failed run or one stopped before any output, and reads them for one stopped with output", async () => {
		stubModels();
		hub.state.events = [{ type: "error", errorMessage: "No slot." }];
		renderWithProviders(<NextTokenExplorer />);
		await chooseAndRun("Say hello.");
		await waitFor(() => expect(screen.getByRole("button", { name: "Run" })).toHaveProperty("disabled", false));

		// Stopped only once the stream is open, or the stop would send nothing at all.
		hub.state.hangs = true;
		hub.state.events = [];
		fireEvent.click(screen.getByRole("button", { name: "Run" }));
		await waitFor(() => expect(hub.state.requests).toHaveLength(2));
		fireEvent.click(screen.getByRole("button", { name: "Stop" }));

		hub.state.events = [{ type: "delta", textDelta: "Hel" }];
		fireEvent.click(screen.getByRole("button", { name: "Run" }));
		await waitFor(() => expect(hub.state.requests).toHaveLength(3));
		fireEvent.click(screen.getByRole("button", { name: "Stop" }));

		await screen.findByTestId("model-lab-prompt-tokens-count");
		expect(tokenizeBodies).toHaveLength(1);
	});
});

describe("NextTokenExplorer prefilled from a trace", () => {
	afterEach(() => {
		tokenizeBodies = [];
	});

	// Stands in for the page: Jump again sends the same link once more under a new nonce.
	function Harness() {
		const [nonce, setNonce] = useState(1);
		return (
			<>
				<button type="button" onClick={() => setNonce((value) => value + 1)}>
					Jump again
				</button>
				<NextTokenExplorer
					intent={{ tab: "nextToken", nonce, modelName: "model-a", prompt: "From the trace.", disableThinking: true }}
				/>
			</>
		);
	}

	it("fills the paired model, the prompt and thinking off once, says it runs the GGUF, and keeps a later edit", async () => {
		stubModels(["model-a"]);
		renderWithProviders(<Harness />);

		const prompt = screen.getByLabelText("Prompt") as HTMLTextAreaElement;
		expect(prompt.value).toBe("From the trace.");
		expect(screen.getByTestId("model-lab-next-token-from-trace").textContent).toContain("runs the paired GGUF with llama.cpp");
		expect(((await screen.findByTestId("direct-inference-thinking-off")) as HTMLInputElement).checked).toBe(true);
		await waitFor(() => expect((screen.getByRole("combobox", { name: "Model" }) as HTMLInputElement).value).toBe("model-a"));

		fireEvent.change(prompt, { target: { value: "My own prompt." } });
		expect(prompt.value).toBe("My own prompt.");
		expect(screen.queryByTestId("model-lab-next-token-from-trace")).toBeNull();

		fireEvent.click(screen.getByRole("button", { name: "Jump again" }));
		expect(prompt.value).toBe("From the trace.");
		expect(screen.getByTestId("model-lab-next-token-from-trace")).not.toBeNull();
	});

	it("drops the trace note when the system prompt is edited", async () => {
		stubModels(["model-a"]);
		renderWithProviders(<Harness />);
		expect(screen.getByTestId("model-lab-next-token-from-trace")).not.toBeNull();

		fireEvent.change(screen.getByLabelText("System prompt (optional)"), { target: { value: "Be brief." } });

		expect(screen.queryByTestId("model-lab-next-token-from-trace")).toBeNull();
	});
});
