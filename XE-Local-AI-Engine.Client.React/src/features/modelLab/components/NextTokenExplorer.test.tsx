// @vitest-environment jsdom

// The Next token tab against a stubbed models route (MSW) and a faked shared-hub seam: it offers llama.cpp models only,
// asks for the top candidates, lists the steps, opens one by keyboard and simulates a sampler over its candidates.

import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import i18next from "i18next";
import { HttpResponse, http } from "msw";
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

function stubModels(): void {
	server.use(
		http.get(localApiPath("models"), () =>
			HttpResponse.json({
				items: [
					["model-a", "llamacpp"],
					["gpt-6", "CodexOAuth"],
					["ext:lan/qwen3", "external"],
				].map(([modelName, provider]) => ({
					modelName,
					displayLabel: null,
					isSelected: false,
					kind: "Chat",
					detectedKind: "Chat",
					capabilities: [],
					isReasoningCapable: false,
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
