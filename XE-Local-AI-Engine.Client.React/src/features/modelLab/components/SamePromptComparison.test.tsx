// @vitest-environment jsdom

// The A/B panel against a stubbed models route (MSW) and a faked shared-hub seam: both runs send the same settings in
// order, and Stop cancels the active run without starting the next.

import { act, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { HttpResponse, http } from "msw";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { DirectInferenceEvent, DirectInferenceRequest } from "@/core/directInference/DirectInferenceModels";
import { SamePromptComparison } from "@/features/modelLab/components/SamePromptComparison";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

const hub = vi.hoisted(() => {
	const state = {
		requests: [] as DirectInferenceRequest[],
		// Per model; a model listed in `hangs` keeps its stream open, one in `refuses` errors the stream.
		events: {} as Record<string, unknown[]>,
		hangs: new Set<string>(),
		refuses: new Set<string>(),
	};
	const dispose = vi.fn();
	const connection = {
		state: "Connected",
		stream: vi.fn((_method: string, request: DirectInferenceRequest) => {
			state.requests.push(request);
			return {
				subscribe: (observer: { next: (event: unknown) => void; complete: () => void; error: (error: Error) => void }) => {
					for (const event of state.events[request.modelId] ?? []) {
						observer.next(event);
					}
					if (state.refuses.has(request.modelId)) {
						observer.error(new Error("the hub refused it"));
					} else if (!state.hangs.has(request.modelId)) {
						observer.complete();
					}
					return { dispose };
				},
			};
		}),
	};
	const handle = { connection, whenStarted: Promise.resolve(), release: vi.fn() };
	let finishStart: () => void = () => undefined;
	return {
		state,
		connection,
		handle,
		dispose,
		pendingStart(): void {
			handle.whenStarted = new Promise<void>((resolve) => {
				finishStart = resolve;
			});
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
					isReasoningCapable: false,
					isToolCapable: true,
					isOverridden: false,
					provider,
				})),
			}),
		),
	);
}

// Both selects stay in the DOM, so an option is looked up in the dropdown this input controls.
async function openDropdown(label: string): Promise<ReturnType<typeof within>> {
	const input = screen.getByRole("combobox", { name: label });
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

async function pick(label: string, name: string): Promise<void> {
	fireEvent.click(await (await openDropdown(label)).findByRole("option", { name, hidden: true }));
}

async function fillAndRun(): Promise<void> {
	await pick("Model A", "model-a");
	await pick("Model B", "model-b");
	fireEvent.change(screen.getByLabelText("Prompt"), { target: { value: "Say hello." } });
	fireEvent.click(screen.getByRole("button", { name: "Run both" }));
}

function column(index: number): ReturnType<typeof within> {
	return within(screen.getByTestId(`model-lab-compare-${index}`));
}

describe("SamePromptComparison", () => {
	afterEach(() => {
		hub.state.requests = [];
		hub.state.events = {};
		hub.state.hangs.clear();
		hub.state.refuses.clear();
		hub.connection.stream.mockClear();
		hub.dispose.mockClear();
		hub.handle.release.mockClear();
		hub.handle.whenStarted = Promise.resolve();
	});

	it("states what a comparison cannot show", () => {
		stubModels();
		renderWithProviders(<SamePromptComparison />);

		const honesty = screen.getByTestId("model-lab-compare-honesty").textContent;
		expect(honesty).toContain("Identical settings do not guarantee identical results");
		expect(honesty).toContain("A difference alone is not evidence of a meaningful effect");
	});

	it("offers installed llama.cpp models only", async () => {
		stubModels();
		renderWithProviders(<SamePromptComparison />);

		const dropdown = await openDropdown("Model A");

		await dropdown.findByRole("option", { name: "model-a", hidden: true });
		expect(dropdown.queryByRole("option", { name: "gpt-6", hidden: true })).toBeNull();
		expect(dropdown.queryByRole("option", { name: "ext:lan/qwen3", hidden: true })).toBeNull();
	});

	it("runs both in order with one seed and the same settings, and shows each side's output and counts", async () => {
		stubModels();
		hub.state.events = {
			"model-a": [{ type: "delta", textDelta: "from a" }, { type: "usage", usage: { outputTokens: 2 } }, { type: "done" }],
			"model-b": [{ type: "delta", textDelta: "from b" }, { type: "usage", usage: { outputTokens: 7 } }, { type: "done" }],
		} satisfies Record<string, DirectInferenceEvent[]>;
		renderWithProviders(<SamePromptComparison />);

		await fillAndRun();

		await waitFor(() => expect(column(1).getByTestId("direct-inference-output").textContent).toBe("from b"));
		const [first, second] = hub.state.requests;
		expect([first?.modelId, second?.modelId]).toEqual(["model-a", "model-b"]);
		expect(first?.prompt).toBe(second?.prompt);
		expect(first?.samplingOptions?.seed).toMatch(/^\d+$/);
		expect(second?.samplingOptions).toEqual(first?.samplingOptions);
		expect(screen.getByTestId("model-lab-compare-seed").textContent).toContain(first?.samplingOptions?.seed);
		expect(column(0).getByTestId("direct-inference-output").textContent).toBe("from a");
		expect(column(0).getByTestId("direct-inference-metric-output").textContent).toBe("2");
		expect(column(1).getByTestId("direct-inference-metric-output").textContent).toBe("7");
		await waitFor(() => expect(screen.queryByRole("button", { name: "Stop" })).toBeNull());
	});

	it("still runs the second model when the first fails, and shows the failure on its side", async () => {
		stubModels();
		hub.state.refuses.add("model-a");
		hub.state.events = { "model-b": [{ type: "delta", textDelta: "from b" }, { type: "done" }] };
		renderWithProviders(<SamePromptComparison />);

		await fillAndRun();

		await waitFor(() => expect(column(1).getByTestId("direct-inference-output").textContent).toBe("from b"));
		expect(column(0).getByText("the hub refused it")).toBeTruthy();
	});

	it("Stop during the first run cancels it, keeps its partial output and never starts the second", async () => {
		stubModels();
		hub.state.hangs.add("model-a");
		hub.state.events = { "model-a": [{ type: "delta", textDelta: "partial" }] };
		renderWithProviders(<SamePromptComparison />);

		await fillAndRun();
		await waitFor(() => expect(column(0).getByTestId("direct-inference-output").textContent).toBe("partial"));
		expect(column(1).getByText("Waiting for the first run to finish.")).toBeTruthy();

		fireEvent.click(screen.getByRole("button", { name: "Stop" }));

		await waitFor(() => expect(hub.dispose).toHaveBeenCalledTimes(1));
		await waitFor(() => expect(hub.handle.release).toHaveBeenCalledTimes(1));
		expect(hub.state.requests.map((request) => request.modelId)).toEqual(["model-a"]);
		expect(column(0).getByTestId("direct-inference-output").textContent).toBe("partial");
		expect(column(1).getByText("Not run: stopped before this model started.")).toBeTruthy();
		expect(screen.queryByRole("button", { name: "Stop" })).toBeNull();
	});

	it("Stop during the second run cancels that one and keeps the first result", async () => {
		stubModels();
		hub.state.hangs.add("model-b");
		hub.state.events = {
			"model-a": [{ type: "delta", textDelta: "from a" }, { type: "done" }],
			"model-b": [{ type: "delta", textDelta: "partial b" }],
		};
		renderWithProviders(<SamePromptComparison />);

		await fillAndRun();
		await waitFor(() => expect(column(1).getByTestId("direct-inference-output").textContent).toBe("partial b"));

		fireEvent.click(screen.getByRole("button", { name: "Stop" }));

		await waitFor(() => expect(hub.dispose).toHaveBeenCalledTimes(1));
		expect(column(0).getByTestId("direct-inference-output").textContent).toBe("from a");
		expect(hub.state.requests).toHaveLength(2);
	});

	it("Stop while the hub connects starts neither run, even after the connection settles", async () => {
		stubModels();
		hub.pendingStart();
		renderWithProviders(<SamePromptComparison />);

		await fillAndRun();
		fireEvent.click(screen.getByRole("button", { name: "Stop" }));
		expect(screen.queryByRole("button", { name: "Stop" })).toBeNull();

		await act(() => hub.finishStart());

		await waitFor(() => expect(hub.handle.release).toHaveBeenCalledTimes(1));
		expect(hub.connection.stream).not.toHaveBeenCalled();
	});

	it("leaving mid-run cancels it and never starts the next", async () => {
		stubModels();
		hub.state.hangs.add("model-a");
		const { unmount } = renderWithProviders(<SamePromptComparison />);

		await fillAndRun();
		await waitFor(() => expect(hub.state.requests).toHaveLength(1));

		unmount();

		await waitFor(() => expect(hub.dispose).toHaveBeenCalledTimes(1));
		await waitFor(() => expect(hub.handle.release).toHaveBeenCalledTimes(1));
		expect(hub.state.requests.map((request) => request.modelId)).toEqual(["model-a"]);
	});

	it("says when the models could not be loaded", async () => {
		server.use(http.get(localApiPath("models"), () => HttpResponse.json({ title: "gone" }, { status: 500 })));
		renderWithProviders(<SamePromptComparison />);

		expect(await screen.findByTestId("model-lab-models-failed")).toBeTruthy();
	});

	it("says when no eligible model is installed", async () => {
		server.use(http.get(localApiPath("models"), () => HttpResponse.json({ isAvailable: true, items: [] })));
		renderWithProviders(<SamePromptComparison />);

		expect((await screen.findByTestId("model-lab-no-models")).textContent).toContain("No installed llama.cpp model");
	});

	it("uses a typed seed for both runs", async () => {
		stubModels();
		hub.state.events = { "model-a": [{ type: "done" }], "model-b": [{ type: "done" }] };
		renderWithProviders(<SamePromptComparison />);

		fireEvent.change(screen.getByLabelText("Seed"), { target: { value: "1234" } });
		await fillAndRun();

		await waitFor(() => expect(hub.state.requests).toHaveLength(2));
		expect(hub.state.requests.map((request) => request.samplingOptions?.seed)).toEqual(["1234", "1234"]);
	});
});
