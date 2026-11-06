// @vitest-environment jsdom

// The Trace tab against stubbed routes (MSW): the other-platform explanation, a refusal before any download, the flow to a saved
// trace, a refused start, and Cancel on a running job.

import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import { HttpResponse, http } from "msw";
import { describe, expect, it, vi } from "vitest";

import { TraceExperiments } from "@/features/modelLab/components/TraceExperiments";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

const hubHandlers = vi.hoisted(() => new Map<string, (value: unknown) => void>());

vi.mock("@/core/api/signalr/SharedHubConnection", () => ({
	acquireHubConnection: () => ({
		connection: {
			state: "Disconnected",
			on: (name: string, handler: (value: unknown) => void) => hubHandlers.set(name, handler),
			off: vi.fn(),
			invoke: vi.fn(),
		},
		whenStarted: Promise.resolve(),
		onReconnected: vi.fn(),
		onReconnecting: vi.fn(),
		onClosed: vi.fn(),
		release: vi.fn(),
	}),
}));

setupMswServer();

const gguf = "org/Tiny-GGUF:Q4_K_M";
const experimentId = "11111111-1111-4111-8111-111111111111";

const emptyList = {
	platformSupported: true,
	unsupportedMessage: null,
	activeExperimentId: null,
	maxRetainedExperiments: 20,
	experiments: [],
};

const artifactId = "22222222-2222-4222-8222-222222222222";

function artifactRow(status: string, errorMessage: string | null = null) {
	return {
		id: artifactId,
		repoId: "Qwen/Qwen3.5-0.8B",
		revision: "2fc06364715b967f1860aea9cf38778875588b17",
		status,
		totalBytes: 1,
		files: [],
		license: null,
		errorMessage,
		version: 1,
		createdAtUtc: 1,
		updatedAtUtc: 1,
		progress: null,
	};
}

const preflightBase = {
	repoId: "Qwen/Qwen3.5-0.8B",
	revision: "2fc06364715b967f1860aea9cf38778875588b17",
	weightBytes: 1,
	requiredVramBytes: 3_000_000_000,
	cardVramBytes: 34_000_000_000,
	license: "apache-2.0",
	isGated: false,
	checkpointHiddenSize: 1024,
	checkpointBlockCount: 24,
	steerableBlockCount: 24,
	checkpointLayerTypes: Array.from({ length: 24 }, (_, block) => (block % 4 === 3 ? "full_attention" : "linear_attention")),
	defaultSteeringLayer: 15,
	ggufEmbeddingLength: 1024,
	ggufBlockCount: 24,
	licenseConfirmationText: "The base checkpoint declares the license 'apache-2.0'.",
	refusal: null,
	refusalMessage: null,
};

function stubModels(): void {
	server.use(
		http.get(localApiPath("models"), () =>
			HttpResponse.json({
				isAvailable: true,
				items: [
					{
						modelName: gguf,
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
		http.get(localApiPath("training/base-artifacts"), () => HttpResponse.json({ items: [] })),
	);
}

async function chooseOption(label: RegExp, option: string): Promise<void> {
	const select = await screen.findByRole("combobox", { name: label });
	fireEvent.click(select);
	const listbox = await waitFor(() => {
		const element = document.getElementById(select.getAttribute("aria-controls") ?? "");
		if (element === null) {
			throw new Error("the dropdown is not open yet");
		}
		return element;
	});
	fireEvent.click(await within(listbox).findByRole("option", { name: option, hidden: true }));
}

async function pickCheckpoint(): Promise<void> {
	await screen.findByPlaceholderText("Qwen/Qwen3.5-0.8B");
	await chooseOption(/Installed GGUF/, gguf);
	fireEvent.change(screen.getByPlaceholderText("Qwen/Qwen3.5-0.8B"), { target: { value: "Qwen/Qwen3.5-0.8B" } });
	fireEvent.click(screen.getByTestId("model-lab-check"));
}

describe("TraceExperiments", () => {
	it("explains other platforms where a trace would start, and offers no form", async () => {
		server.use(
			http.get(localApiPath("model-lab/experiments"), () =>
				HttpResponse.json({ ...emptyList, platformSupported: false, unsupportedMessage: "Tracing runs on Linux only for now." }),
			),
		);

		renderWithProviders(<TraceExperiments />);

		expect((await screen.findByTestId("model-lab-unsupported")).textContent).toContain("Linux only");
		expect(screen.queryByTestId("model-lab-check")).toBeNull();
	});

	it("shows a refusal with its reason and offers no download", async () => {
		stubModels();
		server.use(
			http.get(localApiPath("model-lab/experiments"), () => HttpResponse.json(emptyList)),
			http.post(localApiPath("model-lab/checkpoint-preflight"), () =>
				HttpResponse.json({
					...preflightBase,
					refusal: "CannotFitCard",
					refusalMessage: "Qwen/Qwen3.8-27B cannot fit this card.",
				}),
			),
		);

		renderWithProviders(<TraceExperiments />);
		await pickCheckpoint();

		expect((await screen.findByTestId("model-lab-refusal")).textContent).toContain("cannot fit this card");
		expect(screen.queryByTestId("model-lab-download")).toBeNull();
	});

	it("needs the license confirmed, then downloads, and a loaded model's refusal reads as its message", async () => {
		stubModels();
		const downloads: unknown[] = [];
		server.use(
			http.get(localApiPath("model-lab/experiments"), () => HttpResponse.json(emptyList)),
			http.post(localApiPath("model-lab/checkpoint-preflight"), () => HttpResponse.json(preflightBase)),
			http.post(localApiPath("model-lab/checkpoint-download"), async ({ request }) => {
				downloads.push(await request.json());
				return HttpResponse.json({
					preflight: preflightBase,
					baseArtifactId: "22222222-2222-4222-8222-222222222222",
					artifactStatus: "Ready",
				});
			}),
			http.get(localApiPath("training/base-artifacts/:artifactId"), () =>
				HttpResponse.json({
					id: "22222222-2222-4222-8222-222222222222",
					repoId: "Qwen/Qwen3.5-0.8B",
					revision: preflightBase.revision,
					status: "Ready",
					totalBytes: 1,
					files: [],
					license: null,
					errorMessage: null,
					version: 1,
					createdAtUtc: 1,
					updatedAtUtc: 1,
					progress: null,
				}),
			),
			http.post(localApiPath("model-lab/experiments"), () =>
				HttpResponse.json(
					{ reason: "ModelLoaded", message: "A model is loaded. Eject it, then start the trace again." },
					{ status: 409 },
				),
			),
		);

		renderWithProviders(<TraceExperiments />);
		await pickCheckpoint();

		const download = await screen.findByTestId("model-lab-download");
		expect((download as HTMLButtonElement).disabled).toBe(true);
		fireEvent.click(screen.getByRole("checkbox"));
		fireEvent.click(download);
		await waitFor(() => expect(downloads).toHaveLength(1));
		expect(downloads[0]).toMatchObject({ licenseConfirmed: true, revision: preflightBase.revision });

		fireEvent.change(await screen.findByRole("textbox", { name: /Prompt/ }), { target: { value: "hello" } });
		fireEvent.click(screen.getByTestId("model-lab-start"));

		expect((await screen.findByTestId("model-lab-start-refused")).textContent).toContain("Eject it");
	});

	it("follows the running job and sends Cancel for it, with the chat notice in view", async () => {
		const cancelled: string[] = [];
		stubModels();
		server.use(
			http.get(localApiPath("model-lab/experiments"), () =>
				HttpResponse.json({ ...emptyList, activeExperimentId: experimentId }),
			),
			http.post(localApiPath(`model-lab/experiments/${experimentId}/cancel`), () => {
				cancelled.push(experimentId);
				return new HttpResponse(null, { status: 202 });
			}),
		);

		renderWithProviders(<TraceExperiments />);

		expect((await screen.findByTestId("model-lab-chat-wait")).textContent).toContain("wait");
		fireEvent.click(screen.getByTestId("model-lab-cancel"));
		await waitFor(() => expect(cancelled).toEqual([experimentId]));
	});

	it("traces a checkpoint already on the node without any repository or download", async () => {
		const checks: unknown[] = [];
		const starts: unknown[] = [];
		stubModels();
		server.use(
			http.get(localApiPath("model-lab/experiments"), () => HttpResponse.json(emptyList)),
			http.get(localApiPath("training/base-artifacts"), () => HttpResponse.json({ items: [artifactRow("Ready")] })),
			http.get(localApiPath("training/base-artifacts/:artifactId"), () => HttpResponse.json(artifactRow("Ready"))),
			http.post(localApiPath("model-lab/checkpoint-preflight"), async ({ request }) => {
				checks.push(await request.json());
				return HttpResponse.json(preflightBase);
			}),
			http.post(localApiPath("model-lab/experiments"), async ({ request }) => {
				starts.push(await request.json());
				return HttpResponse.json({ experimentId }, { status: 202 });
			}),
		);

		renderWithProviders(<TraceExperiments />);
		await chooseOption(/Installed GGUF/, gguf);
		await chooseOption(/Checkpoint on this node/, "Qwen/Qwen3.5-0.8B @ 2fc06364715b");
		fireEvent.click(screen.getByTestId("model-lab-check"));

		fireEvent.click(await screen.findByRole("checkbox"));
		expect(screen.queryByTestId("model-lab-download")).toBeNull();
		expect(checks[0]).toEqual({ baseArtifactId: artifactId, installedModelName: gguf });
		fireEvent.change(await screen.findByRole("textbox", { name: /Prompt/ }), { target: { value: "hello" } });
		fireEvent.click(screen.getByTestId("model-lab-start"));

		await waitFor(() => expect(starts).toHaveLength(1));
		expect(starts[0]).toMatchObject({ baseArtifactId: artifactId, installedModelName: gguf, licenseConfirmed: true });
		// Nothing chosen sends no layers, and the form says which the server then picks.
		expect(starts[0]).not.toHaveProperty("layers");
		expect(screen.getByText("None chosen: blocks 6, 12, 18 are traced.")).toBeTruthy();
	});

	it("sends the chosen blocks in ascending order, all of them on request, and none again after Clear", async () => {
		const starts: { layers?: number[] }[] = [];
		stubModels();
		server.use(
			http.get(localApiPath("model-lab/experiments"), () => HttpResponse.json(emptyList)),
			http.get(localApiPath("training/base-artifacts"), () => HttpResponse.json({ items: [artifactRow("Ready")] })),
			http.get(localApiPath("training/base-artifacts/:artifactId"), () => HttpResponse.json(artifactRow("Ready"))),
			http.post(localApiPath("model-lab/checkpoint-preflight"), () => HttpResponse.json(preflightBase)),
			http.post(localApiPath("model-lab/experiments"), async ({ request }) => {
				starts.push((await request.json()) as { layers?: number[] });
				return HttpResponse.json(
					{ reason: "InvalidTraceLayers", message: "This checkpoint has blocks 0 to 23 only." },
					{ status: 409 },
				);
			}),
		);

		renderWithProviders(<TraceExperiments />);
		await chooseOption(/Installed GGUF/, gguf);
		await chooseOption(/Checkpoint on this node/, "Qwen/Qwen3.5-0.8B @ 2fc06364715b");
		fireEvent.click(screen.getByTestId("model-lab-check"));
		fireEvent.click(await screen.findByRole("checkbox"));
		fireEvent.change(await screen.findByRole("textbox", { name: /Prompt/ }), { target: { value: "hello" } });
		await chooseOption(/Blocks to trace/, "Block 18");
		await chooseOption(/Blocks to trace/, "Block 3");
		fireEvent.click(screen.getByTestId("model-lab-start"));

		expect((await screen.findByTestId("model-lab-start-refused")).textContent).toContain("blocks 0 to 23 only");
		expect(starts[0]?.layers).toEqual([3, 18]);

		fireEvent.click(screen.getByTestId("model-lab-trace-layers-all"));
		// Every block is chosen, so there is nothing left for "All blocks" to add.
		expect((screen.getByTestId("model-lab-trace-layers-all") as HTMLButtonElement).disabled).toBe(true);
		fireEvent.click(screen.getByTestId("model-lab-start"));
		await waitFor(() => expect(starts).toHaveLength(2));
		expect(starts[1]?.layers).toEqual(Array.from({ length: 24 }, (_, index) => index));

		fireEvent.click(screen.getByTestId("model-lab-trace-layers-clear"));
		fireEvent.click(screen.getByTestId("model-lab-start"));
		await waitFor(() => expect(starts).toHaveLength(3));
		expect(starts[2]).not.toHaveProperty("layers");
	});

	it("marks a steering row in the list, and only that one", async () => {
		const row = {
			repoId: "Qwen/Qwen3.5-0.8B",
			revision: "rev",
			installedModelName: gguf,
			prompt: "hello",
			errorMessage: null,
			traceByteCount: null,
			result: null,
			status: "Succeeded",
			createdAtUtc: "2026-11-03T00:00:00Z",
			updatedAtUtc: "2026-11-03T00:00:00Z",
		};
		stubModels();
		server.use(
			http.get(localApiPath("model-lab/experiments"), () =>
				HttpResponse.json({
					...emptyList,
					experiments: [
						{ ...row, id: experimentId, kind: "Steering" },
						{ ...row, id: artifactId, kind: "Trace" },
					],
				}),
			),
		);

		renderWithProviders(<TraceExperiments />);

		expect(await screen.findAllByTestId("model-lab-steering-badge")).toHaveLength(1);
	});

	it("says so when the saved checkpoints could not be read, instead of hiding the picker", async () => {
		stubModels();
		server.use(
			http.get(localApiPath("model-lab/experiments"), () => HttpResponse.json(emptyList)),
			http.get(localApiPath("training/base-artifacts"), () => HttpResponse.json({ title: "Nope" }, { status: 500 })),
		);

		renderWithProviders(<TraceExperiments />);

		expect(await screen.findByTestId("model-lab-saved-failed")).toBeTruthy();
	});

	it("says so when the chosen checkpoint could not be read, instead of offering nothing", async () => {
		stubModels();
		server.use(
			http.get(localApiPath("model-lab/experiments"), () => HttpResponse.json(emptyList)),
			http.get(localApiPath("training/base-artifacts"), () => HttpResponse.json({ items: [artifactRow("Ready")] })),
			http.get(localApiPath("training/base-artifacts/:artifactId"), () => HttpResponse.json({ title: "Nope" }, { status: 500 })),
			http.post(localApiPath("model-lab/checkpoint-preflight"), () => HttpResponse.json(preflightBase)),
		);

		renderWithProviders(<TraceExperiments />);
		await chooseOption(/Installed GGUF/, gguf);
		await chooseOption(/Checkpoint on this node/, "Qwen/Qwen3.5-0.8B @ 2fc06364715b");

		expect(await screen.findByTestId("model-lab-artifact-failed")).toBeTruthy();
	});

	it("starts a steering run from the edited starter set, names the layer's kind, and shows a refused layer's reason", async () => {
		const starts: { steering: { positivePrompts: string[] } }[] = [];
		stubModels();
		server.use(
			http.get(localApiPath("model-lab/experiments"), () => HttpResponse.json(emptyList)),
			http.get(localApiPath("training/base-artifacts"), () => HttpResponse.json({ items: [artifactRow("Ready")] })),
			http.get(localApiPath("training/base-artifacts/:artifactId"), () => HttpResponse.json(artifactRow("Ready"))),
			http.post(localApiPath("model-lab/checkpoint-preflight"), () => HttpResponse.json(preflightBase)),
			http.post(localApiPath("model-lab/experiments"), async ({ request }) => {
				starts.push((await request.json()) as { steering: { positivePrompts: string[] } });
				return HttpResponse.json(
					{ reason: "InvalidLayer", message: "This model can be steered only at layers 1 to 23." },
					{ status: 409 },
				);
			}),
		);

		renderWithProviders(<TraceExperiments />);
		await chooseOption(/Installed GGUF/, gguf);
		await chooseOption(/Checkpoint on this node/, "Qwen/Qwen3.5-0.8B @ 2fc06364715b");
		fireEvent.click(screen.getByTestId("model-lab-check"));
		fireEvent.click(await screen.findByRole("checkbox"));
		fireEvent.click(await screen.findByRole("radio", { name: "Steering" }));

		expect((await screen.findByTestId("model-lab-steering-layer-kind")).textContent).toBe("Layer 15 of 23: full attention.");
		fireEvent.change(screen.getByRole("textbox", { name: /Layer/ }), { target: { value: "12" } });
		expect(screen.getByTestId("model-lab-steering-layer-kind").textContent).toBe("Layer 12 of 23: linear attention.");
		fireEvent.change(screen.getByRole("textbox", { name: /Test prompts/ }), { target: { value: "first\n\n  second  " } });
		fireEvent.change(screen.getByRole("textbox", { name: /Negative prompts/ }), { target: { value: "only one" } });
		fireEvent.click(screen.getByTestId("model-lab-steering-start"));

		expect((await screen.findByTestId("model-lab-start-refused")).textContent).toContain("layers 1 to 23");
		expect(starts[0]).toMatchObject({
			prompt: "first",
			licenseConfirmed: true,
			steering: { additionalTestPrompts: ["second"], negativePrompts: ["only one"], layer: 12, scale: 1 },
		});
		expect(starts[0]?.steering.positivePrompts).toHaveLength(8);
	});

	async function openSteeringForm(): Promise<void> {
		stubModels();
		server.use(
			http.get(localApiPath("model-lab/experiments"), () => HttpResponse.json(emptyList)),
			http.get(localApiPath("training/base-artifacts"), () => HttpResponse.json({ items: [artifactRow("Ready")] })),
			http.get(localApiPath("training/base-artifacts/:artifactId"), () => HttpResponse.json(artifactRow("Ready"))),
			http.post(localApiPath("model-lab/checkpoint-preflight"), () => HttpResponse.json(preflightBase)),
		);

		renderWithProviders(<TraceExperiments />);
		await chooseOption(/Installed GGUF/, gguf);
		await chooseOption(/Checkpoint on this node/, "Qwen/Qwen3.5-0.8B @ 2fc06364715b");
		fireEvent.click(screen.getByTestId("model-lab-check"));
		fireEvent.click(await screen.findByRole("checkbox"));
		fireEvent.click(await screen.findByRole("radio", { name: "Steering" }));
		await screen.findByTestId("model-lab-steering-form");
	}

	const repeatLines = (count: number, line = "x"): string => Array.from({ length: count }, () => line).join("\n");

	it.each([
		["more than 32 prompts in a set", "Positive prompts", repeatLines(33), "1 to 32 prompts"],
		["a prompt over 2,000 characters", "Negative prompts", "x".repeat(2001), "1 to 32 prompts"],
		["more than 8 test prompts", "Test prompts", repeatLines(9), "1 to 8 test prompts"],
		["a test prompt over 8,000 characters", "Test prompts", "x".repeat(8001), "1 to 8 test prompts"],
	])("blocks the start and says why for %s", async (_case, field, value, message) => {
		await openSteeringForm();

		fireEvent.change(screen.getByRole("textbox", { name: new RegExp(field) }), { target: { value } });

		expect(screen.getByTestId("model-lab-steering-problem").textContent).toContain(message);
		expect((screen.getByTestId("model-lab-steering-start") as HTMLButtonElement).disabled).toBe(true);
	});

	it.each(["0", "21", "-21"])("blocks the start and says why for scale %s", async (value) => {
		await openSteeringForm();

		fireEvent.change(screen.getByRole("textbox", { name: /Scale/ }), { target: { value } });

		expect(screen.getByTestId("model-lab-steering-problem").textContent).toContain("scale");
		expect((screen.getByTestId("model-lab-steering-start") as HTMLButtonElement).disabled).toBe(true);
	});

	it("takes a negative scale and shows no problem with the starter set", async () => {
		await openSteeringForm();

		fireEvent.change(screen.getByRole("textbox", { name: /Scale/ }), { target: { value: "-1" } });

		expect(screen.queryByTestId("model-lab-steering-problem")).toBeNull();
		expect((screen.getByTestId("model-lab-steering-start") as HTMLButtonElement).disabled).toBe(false);
	});

	it("keeps the steering edits across a switch of job kind", async () => {
		await openSteeringForm();
		fireEvent.change(screen.getByRole("textbox", { name: /Test prompts/ }), { target: { value: "my own prompt" } });
		fireEvent.change(screen.getByRole("textbox", { name: /Layer/ }), { target: { value: "12" } });

		fireEvent.click(screen.getByRole("radio", { name: "Trace" }));
		await screen.findByTestId("model-lab-start");
		fireEvent.click(screen.getByRole("radio", { name: "Steering" }));

		expect((await screen.findByRole<HTMLTextAreaElement>("textbox", { name: /Test prompts/ })).value).toBe("my own prompt");
		expect(screen.getByRole<HTMLInputElement>("textbox", { name: /Layer/ }).value).toBe("12");
	});

	it("offers no steering start with an empty set", async () => {
		stubModels();
		server.use(
			http.get(localApiPath("model-lab/experiments"), () => HttpResponse.json(emptyList)),
			http.get(localApiPath("training/base-artifacts"), () => HttpResponse.json({ items: [artifactRow("Ready")] })),
			http.get(localApiPath("training/base-artifacts/:artifactId"), () => HttpResponse.json(artifactRow("Ready"))),
			http.post(localApiPath("model-lab/checkpoint-preflight"), () => HttpResponse.json(preflightBase)),
		);

		renderWithProviders(<TraceExperiments />);
		await chooseOption(/Installed GGUF/, gguf);
		await chooseOption(/Checkpoint on this node/, "Qwen/Qwen3.5-0.8B @ 2fc06364715b");
		fireEvent.click(screen.getByTestId("model-lab-check"));
		fireEvent.click(await screen.findByRole("checkbox"));
		fireEvent.click(await screen.findByRole("radio", { name: "Steering" }));
		fireEvent.change(await screen.findByRole("textbox", { name: /Positive prompts/ }), { target: { value: "   " } });

		expect((screen.getByTestId("model-lab-steering-start") as HTMLButtonElement).disabled).toBe(true);
	});

	it("offers the download again after it failed, and reads the row anew", async () => {
		let status = "Failed";
		let downloads = 0;
		stubModels();
		server.use(
			http.get(localApiPath("model-lab/experiments"), () => HttpResponse.json(emptyList)),
			http.post(localApiPath("model-lab/checkpoint-preflight"), () => HttpResponse.json(preflightBase)),
			http.post(localApiPath("model-lab/checkpoint-download"), () => {
				downloads += 1;
				return HttpResponse.json({ preflight: preflightBase, baseArtifactId: artifactId, artifactStatus: "Downloading" });
			}),
			http.get(localApiPath("training/base-artifacts/:artifactId"), () =>
				HttpResponse.json(artifactRow(status, status === "Failed" ? "The transfer failed." : null)),
			),
		);

		renderWithProviders(<TraceExperiments />);
		await pickCheckpoint();
		fireEvent.click(await screen.findByRole("checkbox"));
		fireEvent.click(screen.getByTestId("model-lab-download"));

		await screen.findByText("The transfer failed.");
		status = "Ready";
		await waitFor(() => expect((screen.getByTestId("model-lab-download") as HTMLButtonElement).disabled).toBe(false));
		fireEvent.click(screen.getByTestId("model-lab-download"));

		await waitFor(() => expect(downloads).toBe(2));
		expect(await screen.findByRole("textbox", { name: /Prompt/ })).toBeTruthy();
		expect(screen.queryByText("The transfer failed.")).toBeNull();
	});

	it("shows the end of a row opened while it ran, when the hub says its state changed", async () => {
		let status = "Running";
		const row = () => ({
			id: experimentId,
			status,
			kind: "Trace",
			repoId: "Qwen/Qwen3.5-0.8B",
			revision: preflightBase.revision,
			installedModelName: gguf,
			prompt: "hello",
			errorMessage: status === "Failed" ? "The script failed." : null,
			traceByteCount: null,
			result: null,
			createdAtUtc: "2026-11-03T00:00:00Z",
			updatedAtUtc: "2026-11-03T00:00:00Z",
		});
		stubModels();
		server.use(
			http.get(localApiPath("model-lab/experiments"), () =>
				HttpResponse.json({
					...emptyList,
					activeExperimentId: status === "Running" ? experimentId : null,
					experiments: [row()],
				}),
			),
			http.get(localApiPath(`model-lab/experiments/${experimentId}`), () => HttpResponse.json(row())),
		);

		renderWithProviders(<TraceExperiments />);
		fireEvent.click(await screen.findByRole("button", { name: "Qwen/Qwen3.5-0.8B" }));
		await screen.findByTestId("model-lab-trace-view");
		expect(screen.queryByText("The script failed.")).toBeNull();

		status = "Failed";
		await waitFor(() => expect(hubHandlers.has("trainingRun.event")).toBe(true));
		hubHandlers.get("trainingRun.event")?.({ runId: experimentId, sequence: 1, kind: "State", payload: {} });

		expect(await screen.findByText("The script failed.")).toBeTruthy();
	});

	it("lets a row with no live job be deleted, and shows why a delete was refused", async () => {
		stubModels();
		server.use(
			http.get(localApiPath("model-lab/experiments"), () =>
				HttpResponse.json({
					...emptyList,
					experiments: [
						{
							id: experimentId,
							status: "Running",
							kind: "Trace",
							repoId: "Qwen/Qwen3.5-0.8B",
							revision: preflightBase.revision,
							installedModelName: gguf,
							prompt: "hello",
							errorMessage: null,
							traceByteCount: null,
							result: null,
							createdAtUtc: "2026-11-03T00:00:00Z",
							updatedAtUtc: "2026-11-03T00:00:00Z",
						},
					],
				}),
			),
			http.delete(localApiPath(`model-lab/experiments/${experimentId}`), () =>
				HttpResponse.json(
					{ reason: "ProcessNotConfirmedStopped", message: "The next start of the node checks it again." },
					{ status: 409 },
				),
			),
		);

		renderWithProviders(<TraceExperiments />);
		fireEvent.click(await screen.findByRole("button", { name: "Delete" }));

		expect((await screen.findByTestId("model-lab-delete-refused")).textContent).toContain("next start");
	});

	it("reopens a saved trace labelled as measured, and says when it was cut at the cap", async () => {
		stubModels();
		const saved = {
			id: experimentId,
			status: "Succeeded",
			kind: "Trace",
			repoId: "Qwen/Qwen3.5-0.8B",
			revision: preflightBase.revision,
			installedModelName: gguf,
			prompt: "hello",
			errorMessage: null,
			traceByteCount: 100,
			result: null,
			createdAtUtc: "2026-11-03T00:00:00Z",
			updatedAtUtc: "2026-11-03T00:00:00Z",
		};
		server.use(
			http.get(localApiPath("model-lab/experiments"), () => HttpResponse.json({ ...emptyList, experiments: [saved] })),
			http.get(localApiPath(`model-lab/experiments/${experimentId}`), () =>
				HttpResponse.json({
					...saved,
					result: {
						promptTokens: 3,
						tokens: [{ id: 9, text: "Hi" }],
						text: "Hi",
						seconds: 0.5,
						peakVramBytes: 1024,
						blockCount: 24,
						modelClass: "Qwen3_5ForCausalLM",
						device: "GPU",
						torch: "2",
						transformers: "5",
						python: "3",
					},
				}),
			),
			http.get(localApiPath(`model-lab/experiments/${experimentId}/trace`), () =>
				HttpResponse.json({
					records: [{ step: 0, layer: 6, kind: "F", norm: 3.25, percentiles: [], topIndices: [], topValues: [] }],
					truncated: true,
					droppedRecords: 7,
					byteCap: 262144,
				}),
			),
		);

		renderWithProviders(<TraceExperiments />);
		fireEvent.click(await screen.findByRole("button", { name: "Qwen/Qwen3.5-0.8B" }));

		expect((await screen.findByTestId("model-lab-trace-measured")).textContent).toContain("Measured");
		expect((await screen.findByTestId("model-lab-trace-table")).textContent).toContain("3.25");
		expect(screen.getByTestId("model-lab-trace-truncated").textContent).toContain("7 records");
	});
});
