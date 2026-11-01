// @vitest-environment jsdom

// The Direct Inference page against a stubbed models route (MSW) and a faked shared-hub seam: it sends the request the
// form describes, folds the streamed events into output and metrics, and shows an error event.

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { DirectInferenceEvent } from "@/features/directInference/models/DirectInferenceModels";
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
	return { state, connection, handle, dispose };
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
					{
						modelName: "qwen3",
						displayLabel: null,
						isSelected: false,
						kind: "Chat",
						detectedKind: "Chat",
						capabilities: [],
						isReasoningCapable: false,
						isToolCapable: true,
						isOverridden: false,
						provider: "llamacpp",
					},
				],
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
