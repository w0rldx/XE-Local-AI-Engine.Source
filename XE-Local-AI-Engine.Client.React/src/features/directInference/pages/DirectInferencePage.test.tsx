// @vitest-environment jsdom

// The Direct Inference page against a stubbed models route (MSW) and a faked shared-hub seam: it sends the request the
// form describes, folds the streamed events into output and metrics, and shows an error event.

import { act, cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { HttpResponse, http } from "msw";
import { afterEach, describe, expect, it, vi } from "vitest";

import {
	type DirectInferenceEvent,
	directInferenceDefaultMaxOutputTokens,
	directInferenceMaxKeptLogprobEntries,
} from "@/core/directInference/DirectInferenceModels";
import { DirectInferencePage } from "@/features/directInference/pages/DirectInferencePage";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

const hub = vi.hoisted(() => {
	// `hang` keeps the stream open after its events, like a model still generating.
	const state = { events: [] as unknown[], requests: [] as unknown[], hang: false };
	const dispose = vi.fn();
	const connection = {
		state: "Connected",
		stream: vi.fn((_method: string, request: unknown) => {
			state.requests.push(request);
			return {
				subscribe: (observer: { next: (event: unknown) => void; complete: () => void }) => {
					for (const event of state.events) {
						observer.next(event);
					}
					if (!state.hang) {
						observer.complete();
					}
					return { dispose };
				},
			};
		}),
	};
	const handle = { connection, whenStarted: Promise.resolve(), release: vi.fn() };
	let finishStart: () => void = () => undefined;
	let rejectStart: (error: Error) => void = () => undefined;
	return {
		state,
		connection,
		handle,
		dispose,
		/** Holds every acquire's `whenStarted` open, like a hub still negotiating. */
		pendingStart(): void {
			handle.whenStarted = new Promise<void>((resolve, reject) => {
				finishStart = resolve;
				rejectStart = reject;
			});
		},
		failStart(error: Error): void {
			rejectStart(error);
		},
		async finishStart(): Promise<void> {
			finishStart();
			await handle.whenStarted;
		},
	};
});

vi.mock("@/core/api/signalr/SharedHubConnection", () => ({
	acquireHubConnection: () => hub.handle,
}));

setupMswServer();

function stubModels(): void {
	server.use(
		http.get(localApiPath("models"), () =>
			HttpResponse.json({
				isAvailable: true,
				items: [
					["qwen3", "llamacpp"],
					["gpt-6", "CodexOAuth"],
					["ext:lan/qwen3", "external"],
					["ollama-model", "ollama"],
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

async function fillAndSend(): Promise<void> {
	fireEvent.click(screen.getByTestId("direct-inference-model"));
	fireEvent.click(await screen.findByRole("option", { name: "qwen3", hidden: true }));
	fireEvent.change(screen.getByLabelText("System prompt (optional)"), { target: { value: "Be terse." } });
	fireEvent.change(screen.getByLabelText("Prompt"), { target: { value: "Say hello." } });
	fireEvent.click(screen.getByRole("button", { name: "Send" }));
}

describe("DirectInferencePage", () => {
	afterEach(() => {
		cleanup();
		hub.state.events = [];
		hub.state.requests = [];
		hub.state.hang = false;
		hub.dispose.mockClear();
		hub.handle.release.mockClear();
		hub.handle.whenStarted = Promise.resolve();
	});

	it.each(["gpt-6", "ext:lan/qwen3", "ollama-model"])(
		"states the default output cap for a llama.cpp model, not for %s",
		async (other) => {
			stubModels();
			renderWithProviders(<DirectInferencePage />);
			const maxOutput = screen.getByLabelText("Max output tokens") as HTMLInputElement;
			const pick = async (name: string): Promise<void> => {
				fireEvent.click(screen.getByTestId("direct-inference-model"));
				fireEvent.click(await screen.findByRole("option", { name, hidden: true }));
			};
			expect(maxOutput.placeholder).toBe("Model default");

			await pick("qwen3");
			expect(maxOutput.placeholder).toBe(`Default: ${directInferenceDefaultMaxOutputTokens.toLocaleString()}`);
			expect(screen.getByText("Thinking counts toward this limit.")).toBeTruthy();

			await pick(other);
			expect(maxOutput.placeholder).toBe("Model default");
			expect(screen.queryByText("Thinking counts toward this limit.")).toBeNull();
		},
	);

	it("sends no probability request unless asked, and shows no probability view", async () => {
		stubModels();
		hub.state.events = [{ type: "delta", textDelta: "hi" }, { type: "done" }] satisfies DirectInferenceEvent[];
		renderWithProviders(<DirectInferencePage />);

		await fillAndSend();

		await waitFor(() => expect(screen.getByTestId("direct-inference-output").textContent).toBe("hi"));
		expect(hub.state.requests[0]).toMatchObject({ modelId: "qwen3", topLogprobs: undefined });
		expect(screen.queryByTestId("direct-inference-logprobs")).toBeNull();
	});

	it("offers token probabilities for a llama.cpp model only, and does not send a value kept from one", async () => {
		stubModels();
		renderWithProviders(<DirectInferencePage />);
		const pick = async (name: string): Promise<void> => {
			fireEvent.click(screen.getByTestId("direct-inference-model"));
			fireEvent.click(await screen.findByRole("option", { name, hidden: true }));
		};

		await pick("qwen3");
		fireEvent.change(screen.getByLabelText("Token probabilities"), { target: { value: "3" } });
		await pick("gpt-6");
		expect(screen.queryByLabelText("Token probabilities")).toBeNull();
		fireEvent.change(screen.getByLabelText("Prompt"), { target: { value: "Hi" } });
		fireEvent.click(screen.getByRole("button", { name: "Send" }));

		await waitFor(() => expect(hub.state.requests).toHaveLength(1));
		expect(hub.state.requests[0]).toMatchObject({ modelId: "gpt-6", topLogprobs: undefined });
		expect(screen.queryByTestId("direct-inference-logprobs")).toBeNull();
	});

	it.each(["0", "21"])("does not keep the out-of-range value %s", async (typed) => {
		stubModels();
		renderWithProviders(<DirectInferencePage />);
		fireEvent.click(screen.getByTestId("direct-inference-model"));
		fireEvent.click(await screen.findByRole("option", { name: "qwen3", hidden: true }));
		const field = screen.getByLabelText("Token probabilities") as HTMLInputElement;

		fireEvent.change(field, { target: { value: typed } });

		expect(field.value).toBe("");
	});

	it("shows merged entries and gaps as they came, with what they are not, and the count beside the server's", async () => {
		stubModels();
		hub.state.events = [
			{ type: "delta", reasoningDelta: "hm" },
			{ type: "logprobs", logprobs: [{ text: "hm", logprob: -0.1, isReasoning: true, alternatives: [] }] },
			{ type: "delta", textDelta: "\n🌏" },
			{
				type: "logprobs",
				logprobs: [
					{
						text: "🌏",
						logprob: -0.5,
						isReasoning: false,
						alternatives: [
							{ text: "�", logprob: -0.5 },
							{ text: " a", logprob: -3.4028235e38 },
						],
					},
				],
			},
			{ type: "usage", usage: { outputTokens: 5 } },
			{ type: "done", finishReason: "stop" },
		] satisfies DirectInferenceEvent[];
		renderWithProviders(<DirectInferencePage />);

		fireEvent.click(screen.getByTestId("direct-inference-model"));
		fireEvent.click(await screen.findByRole("option", { name: "qwen3", hidden: true }));
		fireEvent.change(screen.getByLabelText("Token probabilities"), { target: { value: "2" } });
		fireEvent.change(screen.getByLabelText("Prompt"), { target: { value: "Hi" } });
		fireEvent.click(screen.getByRole("button", { name: "Send" }));

		const rows = await screen.findAllByTestId("direct-inference-logprob-entry");
		expect(hub.state.requests[0]).toMatchObject({ topLogprobs: 2 });
		expect(rows).toHaveLength(2);
		expect(rows[0]?.textContent).toContain("Thinking");
		expect(rows[1]?.textContent).toContain('"🌏"');
		expect(rows[1]?.textContent).toContain("60.65%");
		expect(rows[1]?.textContent).toContain('" a" <0.01%');
		expect(screen.getByTestId("direct-inference-logprobs").textContent).toContain("not an explanation of its reasoning");
		expect(screen.getByTestId("direct-inference-logprobs").textContent).toContain("Entries are not tokens.");
		expect(screen.getByTestId("direct-inference-logprobs-count").textContent).toBe(
			"Entries: 2. Generated tokens the server reported: 5.",
		);
		expect(screen.getByTestId("direct-inference-metric-output").textContent).toBe("5");
		expect(screen.getByTestId("direct-inference-output").textContent).toBe("\n🌏");
	});

	async function sendWithProbabilities(): Promise<void> {
		fireEvent.click(screen.getByTestId("direct-inference-model"));
		fireEvent.click(await screen.findByRole("option", { name: "qwen3", hidden: true }));
		fireEvent.change(screen.getByLabelText("Token probabilities"), { target: { value: "1" } });
		fireEvent.change(screen.getByLabelText("Prompt"), { target: { value: "Hi" } });
		fireEvent.click(screen.getByRole("button", { name: "Send" }));
	}

	it("says so when a finished run returned no entries", async () => {
		stubModels();
		hub.state.events = [
			{ type: "delta", textDelta: "hi" },
			{ type: "done", elapsedMs: 5 },
		] satisfies DirectInferenceEvent[];
		renderWithProviders(<DirectInferencePage />);

		await sendWithProbabilities();

		expect(await screen.findByTestId("direct-inference-logprobs-none")).toBeTruthy();
	});

	it("does not claim the server returned none while generating or after Stop", async () => {
		stubModels();
		hub.state.hang = true;
		hub.state.events = [{ type: "delta", textDelta: "hi" }] satisfies DirectInferenceEvent[];
		renderWithProviders(<DirectInferencePage />);

		await sendWithProbabilities();
		await waitFor(() => expect(screen.getByTestId("direct-inference-output").textContent).toBe("hi"));
		expect(screen.queryByTestId("direct-inference-logprobs-none")).toBeNull();

		fireEvent.click(screen.getByRole("button", { name: "Stop" }));

		expect(screen.queryByRole("button", { name: "Stop" })).toBeNull();
		expect(screen.queryByTestId("direct-inference-logprobs-none")).toBeNull();
	});

	it("does not claim the server returned none after Stop while connecting", async () => {
		stubModels();
		hub.pendingStart();
		renderWithProviders(<DirectInferencePage />);

		await sendWithProbabilities();
		fireEvent.click(screen.getByRole("button", { name: "Stop" }));
		await act(() => hub.finishStart());

		expect(screen.queryByTestId("direct-inference-logprobs-none")).toBeNull();
	});

	it("keeps the entries when the stream fails midway, beside the failure", async () => {
		stubModels();
		hub.state.events = [
			{ type: "logprobs", logprobs: [{ text: "a", logprob: -1, isReasoning: false, alternatives: [] }] },
			{ type: "error", errorCode: "Unknown", errorMessage: "it broke" },
		] satisfies DirectInferenceEvent[];
		renderWithProviders(<DirectInferencePage />);

		await sendWithProbabilities();

		expect(await screen.findByText("it broke")).toBeTruthy();
		expect(screen.getAllByTestId("direct-inference-logprob-entry")).toHaveLength(1);
	});

	it("shows no probability view when the request failed with no entries", async () => {
		stubModels();
		hub.state.events = [{ type: "error", errorCode: "Unknown", errorMessage: "it broke" }] satisfies DirectInferenceEvent[];
		renderWithProviders(<DirectInferencePage />);

		await sendWithProbabilities();

		expect(await screen.findByText("it broke")).toBeTruthy();
		expect(screen.queryByTestId("direct-inference-logprobs")).toBeNull();
	});

	it("says which kind of entry it left out", async () => {
		stubModels();
		const reasoning = { text: "t", logprob: -1, isReasoning: true, alternatives: [] };
		hub.state.events = [
			{ type: "logprobs", logprobs: Array.from({ length: directInferenceMaxKeptLogprobEntries + 2 }, () => reasoning) },
			{ type: "logprobs", logprobs: [{ ...reasoning, text: "a", isReasoning: false }] },
			{ type: "done", elapsedMs: 5 },
		] satisfies DirectInferenceEvent[];
		renderWithProviders(<DirectInferencePage />);

		await sendWithProbabilities();

		expect((await screen.findByTestId("direct-inference-logprobs-left-out-reasoning")).textContent).toContain(
			"Thinking: showing the first 500 entries; 2 more were left out.",
		);
		expect(screen.queryByTestId("direct-inference-logprobs-left-out-answer")).toBeNull();
		expect(screen.getAllByTestId("direct-inference-logprob-entry")).toHaveLength(directInferenceMaxKeptLogprobEntries + 1);
	});

	it("copies the JSON only when asked, with the result as of that click", async () => {
		stubModels();
		const writeText = vi.fn(() => Promise.resolve());
		vi.stubGlobal("navigator", { ...window.navigator, clipboard: { writeText } });
		hub.state.events = [
			{ type: "delta", textDelta: "hi" },
			{ type: "done", elapsedMs: 5 },
		] satisfies DirectInferenceEvent[];
		renderWithProviders(<DirectInferencePage />);

		await fillAndSend();
		await waitFor(() => expect(screen.getByTestId("direct-inference-output").textContent).toBe("hi"));
		expect(writeText).not.toHaveBeenCalled();

		fireEvent.click(screen.getByRole("button", { name: "Copy as JSON" }));

		expect(JSON.parse(writeText.mock.calls[0]?.at(0) as unknown as string)).toMatchObject({ result: { text: "hi" } });
		expect(await screen.findByRole("button", { name: "Copied" })).toBeTruthy();
	});

	it("goes idle as soon as Stop is pressed while connecting, and the late connection sends nothing", async () => {
		stubModels();
		hub.pendingStart();
		renderWithProviders(<DirectInferencePage />);

		await fillAndSend();
		fireEvent.click(screen.getByRole("button", { name: "Stop" }));

		expect(screen.queryByRole("button", { name: "Stop" })).toBeNull();
		expect(screen.getByRole("button", { name: "Send" }).hasAttribute("data-loading")).toBe(false);
		expect((screen.getByLabelText("Prompt") as HTMLTextAreaElement).disabled).toBe(false);

		await hub.finishStart();

		await waitFor(() => expect(hub.handle.release).toHaveBeenCalledTimes(1));
		expect(hub.connection.stream).not.toHaveBeenCalled();
		expect(screen.queryByRole("button", { name: "Stop" })).toBeNull();
	});

	it("keeps a Send made right after Stop running when the stopped attempt settles", async () => {
		stubModels();
		hub.pendingStart();
		renderWithProviders(<DirectInferencePage />);

		await fillAndSend();
		fireEvent.click(screen.getByRole("button", { name: "Stop" }));
		hub.state.hang = true;
		hub.state.events = [{ type: "delta", textDelta: "second" }] satisfies DirectInferenceEvent[];
		fireEvent.click(screen.getByRole("button", { name: "Send" }));
		await hub.finishStart();

		await waitFor(() => expect(screen.getByTestId("direct-inference-output").textContent).toBe("second"));
		await waitFor(() => expect(hub.handle.release).toHaveBeenCalledTimes(1));
		expect(hub.state.requests).toHaveLength(1);
		expect(hub.dispose).not.toHaveBeenCalled();

		// Still running, and Stop still reaches the second attempt: the first one's finish took neither from it.
		fireEvent.click(screen.getByRole("button", { name: "Stop" }));

		expect(hub.dispose).toHaveBeenCalledTimes(1);
		expect(screen.getByTestId("direct-inference-output").textContent).toBe("second");
	});

	it("keeps a newer Send's output when the stopped attempt's connection fails late", async () => {
		stubModels();
		hub.pendingStart();
		renderWithProviders(<DirectInferencePage />);

		await fillAndSend();
		fireEvent.click(screen.getByRole("button", { name: "Stop" }));
		// The second attempt holds a connection that is already up; only the stopped one is still waiting on its negotiation.
		hub.handle.whenStarted = Promise.resolve();
		hub.state.hang = true;
		hub.state.events = [{ type: "delta", textDelta: "second" }] satisfies DirectInferenceEvent[];
		fireEvent.click(screen.getByRole("button", { name: "Send" }));
		await waitFor(() => expect(screen.getByTestId("direct-inference-output").textContent).toBe("second"));

		hub.failStart(new Error("negotiation failed"));
		await waitFor(() => expect(hub.handle.release).toHaveBeenCalledTimes(1));
		// The page's own continuation runs after the lease is released.
		await act(() => new Promise((resolve) => setTimeout(resolve)));

		expect(screen.getByTestId("direct-inference-output").textContent).toBe("second");
		expect(screen.queryByText("negotiation failed")).toBeNull();
		expect(screen.getByRole("button", { name: "Stop" })).toBeTruthy();
	});

	it("cancels a running request when the page unmounts", async () => {
		stubModels();
		hub.state.hang = true;
		hub.state.events = [{ type: "delta", textDelta: "partial" }] satisfies DirectInferenceEvent[];
		const { unmount } = renderWithProviders(<DirectInferencePage />);

		await fillAndSend();
		await waitFor(() => expect(screen.getByTestId("direct-inference-output").textContent).toBe("partial"));
		expect(hub.dispose).not.toHaveBeenCalled();

		unmount();

		expect(hub.dispose).toHaveBeenCalledTimes(1);
	});

	it("sends the form as one request and renders the streamed output and metrics", async () => {
		stubModels();
		hub.state.events = [
			{ type: "delta", reasoningDelta: "thinking" },
			{ type: "delta", textDelta: "Hel" },
			{ type: "delta", textDelta: "lo" },
			{ type: "usage", usage: { inputTokens: 12, outputTokens: 3 } },
			{ type: "timings", timings: { promptTokens: 12, promptMs: 60, generationTokens: 3, generationMs: 30 } },
			{ type: "done", elapsedMs: 50, firstTokenMs: 10, finishReason: "stop" },
		] satisfies DirectInferenceEvent[];
		renderWithProviders(<DirectInferencePage />);

		await fillAndSend();

		await waitFor(() => expect(screen.getByTestId("direct-inference-output").textContent).toBe("Hello"));
		expect(hub.connection.stream).toHaveBeenCalledWith("StreamDirectInference", expect.anything());
		expect(hub.state.requests).toEqual([{ modelId: "qwen3", prompt: "Say hello.", systemPrompt: "Be terse." }]);
		expect(screen.getByTestId("direct-inference-reasoning").textContent).toBe("thinking");
		expect(screen.getByTestId("direct-inference-metric-input").textContent).toBe("12");
		expect(screen.getByTestId("direct-inference-metric-generationRate").textContent).toBe("100");
		expect(screen.getByTestId("direct-inference-metric-promptRate").textContent).toBe("200");
		expect(screen.getByTestId("direct-inference-metric-ttft").textContent).toBe("10");
		expect(screen.getByTestId("direct-inference-metric-total").textContent).toBe("50");
	});

	it("shows n/a for the llama timings a cloud answer does not carry", async () => {
		stubModels();
		hub.state.events = [
			{ type: "delta", textDelta: "Hi" },
			{ type: "usage", usage: { inputTokens: 4, outputTokens: 1 } },
			{ type: "done", elapsedMs: 20, firstTokenMs: 5, finishReason: "stop" },
		] satisfies DirectInferenceEvent[];
		renderWithProviders(<DirectInferencePage />);

		await fillAndSend();

		await waitFor(() => expect(screen.getByTestId("direct-inference-metric-total").textContent).toBe("20"));
		expect(screen.getByTestId("direct-inference-metric-promptMs").textContent).toBe("n/a");
		expect(screen.getByTestId("direct-inference-metric-generationRate").textContent).toBe("n/a");
	});

	it("says the answer was cut off only for a length finish", async () => {
		stubModels();
		hub.state.events = [
			{ type: "delta", textDelta: "Partial" },
			{ type: "done", elapsedMs: 20, finishReason: "length" },
		] satisfies DirectInferenceEvent[];
		renderWithProviders(<DirectInferencePage />);

		await fillAndSend();

		expect((await screen.findByTestId("direct-inference-cut-off")).textContent).toContain("cut off");

		hub.state.events = [{ type: "done", elapsedMs: 20, finishReason: "stop" }] satisfies DirectInferenceEvent[];
		fireEvent.click(screen.getByRole("button", { name: "Send" }));

		await waitFor(() => expect(screen.queryByTestId("direct-inference-cut-off")).toBeNull());
	});

	it("shows the error event's message, and the localized notice for an exclusive GPU holder", async () => {
		stubModels();
		hub.state.events = [
			{ type: "error", errorCode: "ProviderUnreachable", errorMessage: "The model provider could not be reached." },
		];
		renderWithProviders(<DirectInferencePage />);

		await fillAndSend();

		expect(await screen.findByText("The model provider could not be reached.")).toBeTruthy();

		hub.state.events = [
			{ type: "error", errorCode: "GpuWorkExclusive", errorMessage: "This message was queued behind a running training run." },
		];
		fireEvent.click(screen.getByRole("button", { name: "Send" }));

		expect(
			await screen.findByText("A training run, evaluation or export holds the GPU. Try again when it has finished."),
		).toBeTruthy();
	});
});
