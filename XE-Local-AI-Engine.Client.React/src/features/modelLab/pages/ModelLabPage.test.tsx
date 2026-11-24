// @vitest-environment jsdom

// The page's model picker and the Inspect tab against stubbed routes (MSW): the picker lists every installed GGUF and says
// why it has nothing to offer; Inspect opens the shared panel for the page's model once its tab shows it, and shows both
// ways a read can fail.

import { act, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { HttpResponse, http } from "msw";
import { useState } from "react";
import { describe, expect, it, vi } from "vitest";

import { applyModelLabPatch, type ModelLabSearch } from "@/features/modelLab/models/ModelLabNavigation";
import { ModelLabPage } from "@/features/modelLab/pages/ModelLabPage";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

vi.mock("@/core/api/signalr/SharedHubConnection", () => ({
	acquireHubConnection: () => ({
		connection: { state: "Connected", stream: vi.fn() },
		whenStarted: Promise.resolve(),
		release: vi.fn(),
	}),
}));
vi.mock("@/features/modelLab/components/TraceExperiments", () => ({ TraceExperiments: () => null }));
vi.mock("@/core/ui/components/CodeEditor/CodeEditor", () => ({ CodeEditor: () => null }));

setupMswServer();

const inspection = {
	modelName: "org/Tiny-GGUF:Q4_K_M",
	readFailure: null,
	ggufVersion: 3,
	architecture: "qwen35",
	displayName: "Tiny",
	parameterCount: 1000,
	fileType: "Q4_K_M",
	blockCount: 1,
	attentionHeadCount: 16,
	attentionHeadCountKv: 4,
	embeddingLength: 4096,
	contextLength: 262144,
	expertCount: null,
	chatTemplate: null,
	splitCount: null,
	tensorCount: 1,
	totalTensorBytes: 1024,
	headerBytes: 2048,
	fileSizeBytes: 4096,
	blocks: [
		{
			index: 0,
			byteSize: 1024,
			tensors: [{ name: "blk.0.attn_q.weight", shape: [256], ggmlType: "F32", ggmlTypeId: 0, offset: 0, byteSize: 1024 }],
		},
	],
	nonBlockTensors: [],
};

// The descriptor Architecture reads for the page's model; its content is not what these tests are about.
const descriptor = {
	descriptorVersion: 1,
	modelName: "org/Tiny-GGUF:Q4_K_M",
	architecture: "qwen35",
	support: "overview",
	readFailure: null,
	suggestedBaseRepo: null,
	hyperparameters: null,
	blocks: [],
	templates: [],
	nonBlock: [],
};

function modelItem(modelName: string, provider: string, kind = "Chat", displayLabel: string | null = null) {
	return {
		modelName,
		displayLabel,
		isSelected: false,
		kind,
		detectedKind: kind,
		capabilities: [],
		isReasoningCapable: false,
		isToolCapable: true,
		isOverridden: false,
		provider,
	};
}

function stubRoutes(respond: () => Response, kind = "Chat"): { inspections: number; architectures: string[] } {
	const seen = { inspections: 0, architectures: [] as string[] };
	server.use(
		http.get(localApiPath("models"), () =>
			HttpResponse.json({
				items: [
					["org/Tiny-GGUF:Q4_K_M", "llamacpp"],
					["org/Tiny-GGUF:Q3_K_XL", "llamacpp"],
					["gpt-6", "CodexOAuth"],
				].map(([modelName = "", provider = ""]) => modelItem(modelName, provider, kind)),
			}),
		),
		http.get(localApiPath("models/:modelName/gguf-inspection"), () => {
			seen.inspections++;
			return respond();
		}),
		http.get(localApiPath("model-lab/architecture/:modelName"), ({ params }) => {
			seen.architectures.push(String(params["modelName"]));
			return HttpResponse.json(descriptor);
		}),
		http.get(localApiPath("model-lab/experiments"), () =>
			HttpResponse.json({
				platformSupported: true,
				unsupportedMessage: null,
				activeExperimentId: null,
				maxRetainedExperiments: 20,
				maxPinnedExperiments: 20,
				experiments: [],
			}),
		),
	);
	return seen;
}

// The page suspends on its strings' import, and React retries that inside an awaited act scope only. The router is for
// the empty picker's Models link.
async function renderPage(search?: ModelLabSearch): Promise<void> {
	await act(async () => {
		renderWithProviders(search ? <ModelLabPage search={search} onSearchChange={() => undefined} /> : <ModelLabPage />, {
			withRouter: true,
		});
	});
	await screen.findByRole("tab", { selected: true });
}

async function openModelDropdown(): Promise<ReturnType<typeof within>> {
	const input = await screen.findByRole("combobox", { name: /Model for Architecture, Next token and Inspect/ });
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

async function pickModel(name = "org/Tiny-GGUF:Q4_K_M"): Promise<void> {
	const dropdown = await openModelDropdown();
	fireEvent.click(await dropdown.findByRole("option", { name, hidden: true }));
}

async function openInspectAndPick(): Promise<void> {
	await pickModel();
	fireEvent.click(screen.getByRole("tab", { name: "Inspect" }));
}

describe("ModelLabPage model picker", () => {
	it("offers every installed llama.cpp GGUF and nothing else", async () => {
		stubRoutes(() => HttpResponse.json(inspection));
		await renderPage();

		const dropdown = await openModelDropdown();

		await dropdown.findByRole("option", { name: "org/Tiny-GGUF:Q3_K_XL", hidden: true });
		expect(dropdown.getAllByRole("option", { hidden: true })).toHaveLength(2);
		expect(dropdown.queryByRole("option", { name: "gpt-6", hidden: true })).toBeNull();
	});

	it.each([
		[
			"model-lab-models-failed",
			() => HttpResponse.json({ title: "gone" }, { status: 500 }),
			"The installed models could not be loaded.",
			0,
		],
		[
			"model-lab-no-models",
			() => HttpResponse.json({ items: [] }),
			"No installed llama.cpp model is available. Install one under Models first.Open Models",
			1,
		],
	])("shows %s once for the three tabs it serves, and not on A/B", async (testId, respond, text, actions) => {
		server.use(http.get(localApiPath("models"), respond));
		await renderPage();

		// The page's notice above the tabs, plus A/B's own in its mounted panel.
		const [pageNotice] = await screen.findAllByTestId(testId);
		expect(screen.getAllByTestId(testId)).toHaveLength(2);
		expect(pageNotice?.closest("[role=tabpanel]")).toBeNull();
		expect(pageNotice?.textContent).toBe(text);
		expect(within(pageNotice as HTMLElement).queryAllByRole("link")).toHaveLength(actions);

		for (const tab of ["Next token", "Inspect"]) {
			fireEvent.click(screen.getByRole("tab", { name: tab }));
			expect(screen.getAllByTestId(testId)).toHaveLength(2);
		}
		fireEvent.click(screen.getByRole("tab", { name: "Same-prompt A/B" }));
		expect(screen.getAllByTestId(testId)).toHaveLength(1);
		fireEvent.click(screen.getByRole("tab", { name: "Trace and steer" }));
		expect(screen.getAllByTestId(testId)).toHaveLength(1);
	});

	it.each([
		["resolves", () => HttpResponse.json({ items: [modelItem("org/Tiny-GGUF:Q4_K_M", "llamacpp")] }), "org/Tiny-GGUF:Q4_K_M"],
		["fails", () => HttpResponse.json({ title: "gone" }, { status: 500 }), ""],
	])("says the models are loading until the first list %s", async (_case, respond, shown) => {
		let answer: () => void = () => undefined;
		const answered = new Promise<void>((resolve) => {
			answer = resolve;
		});
		stubRoutes(() => HttpResponse.json(inspection));
		server.use(
			http.get(localApiPath("models"), async () => {
				await answered;
				return respond();
			}),
		);
		await renderPage({ model: "org/Tiny-GGUF:Q4_K_M" });

		expect(screen.getByRole("status").textContent).toBe("Loading the installed models…");

		answer();

		await waitFor(() => expect(screen.queryByTestId("model-lab-models-loading")).toBeNull());
		expect(screen.queryByRole("status")).toBeNull();
		expect((screen.getByRole("combobox", { name: /Model for Architecture/ }) as HTMLInputElement).value).toBe(shown);
		expect(screen.queryAllByTestId("model-lab-models-failed")).toHaveLength(shown === "" ? 2 : 0);
	});

	it("shows a model's display label and writes its name", async () => {
		const onSearchChange = vi.fn();
		stubRoutes(() => HttpResponse.json(inspection));
		server.use(
			http.get(localApiPath("models"), () =>
				HttpResponse.json({
					items: [
						modelItem("org/Tiny-GGUF:Q4_K_M", "llamacpp", "Chat", "Tiny, 4-bit"),
						modelItem("org/Tiny-GGUF:Q3_K_XL", "llamacpp"),
					],
				}),
			),
		);
		await act(async () => {
			renderWithProviders(<ModelLabPage search={{}} onSearchChange={onSearchChange} />, { withRouter: true });
		});

		const dropdown = await openModelDropdown();
		await dropdown.findByRole("option", { name: "org/Tiny-GGUF:Q3_K_XL", hidden: true });
		fireEvent.click(dropdown.getByRole("option", { name: "Tiny, 4-bit", hidden: true }));

		expect(onSearchChange.mock.calls.map(([update]) => update)).toEqual([{ model: "org/Tiny-GGUF:Q4_K_M" }]);
	});

	it("shows a linked model under its display label", async () => {
		stubRoutes(() => HttpResponse.json(inspection));
		server.use(
			http.get(localApiPath("models"), () =>
				HttpResponse.json({ items: [modelItem("org/Tiny-GGUF:Q4_K_M", "llamacpp", "Chat", "Tiny, 4-bit")] }),
			),
		);
		await renderPage({ model: "org/Tiny-GGUF:Q4_K_M" });

		await waitFor(() =>
			expect((screen.getByRole("combobox", { name: /Model for Architecture/ }) as HTMLInputElement).value).toBe("Tiny, 4-bit"),
		);
	});

	it("says only that no model is installed when a linked model meets an empty inventory", async () => {
		stubRoutes(() => HttpResponse.json(inspection));
		server.use(http.get(localApiPath("models"), () => HttpResponse.json({ items: [] })));
		await renderPage({ model: "org/Gone-GGUF:Q4_K_M" });

		expect(await screen.findAllByTestId("model-lab-no-models")).toHaveLength(2);
		expect(screen.queryByTestId("model-lab-model-not-installed")).toBeNull();
	});

	it("says a linked model is not installed once the list lacks it, and only then", async () => {
		let answer: () => void = () => undefined;
		const answered = new Promise<void>((resolve) => {
			answer = resolve;
		});
		stubRoutes(() => HttpResponse.json(inspection));
		server.use(
			http.get(localApiPath("models"), async () => {
				await answered;
				return HttpResponse.json({ items: [modelItem("org/Tiny-GGUF:Q4_K_M", "llamacpp")] });
			}),
		);
		await renderPage({ model: "org/Gone-GGUF:Q4_K_M" });

		expect(screen.queryByTestId("model-lab-model-not-installed")).toBeNull();
		expect(screen.queryByTestId("model-lab-no-models")).toBeNull();

		answer();

		const notice = await screen.findByTestId("model-lab-model-not-installed");
		expect(notice.textContent).toBe(
			"org/Gone-GGUF:Q4_K_M is not installed as a llama.cpp model on this node. Choose an installed model.",
		);
		expect(within(notice).queryAllByRole("link")).toHaveLength(0);
		expect((screen.getByRole("combobox", { name: /Model for Architecture/ }) as HTMLInputElement).value).toBe("");
	});

	it("says nothing about a linked model while the list has failed to load", async () => {
		server.use(http.get(localApiPath("models"), () => HttpResponse.json({ title: "gone" }, { status: 500 })));
		await renderPage({ model: "org/Gone-GGUF:Q4_K_M" });

		expect(await screen.findAllByTestId("model-lab-models-failed")).toHaveLength(2);
		expect(screen.queryByTestId("model-lab-model-not-installed")).toBeNull();
	});

	it("lists GGUFs that are not chat models, and Next token says it cannot run them", async () => {
		stubRoutes(() => HttpResponse.json(inspection), "Embedding");
		await renderPage();

		await pickModel();
		fireEvent.click(screen.getByRole("tab", { name: "Next token" }));

		expect((await screen.findByTestId("model-lab-next-token-not-runnable")).textContent).toBe(
			"Next token runs llama.cpp chat models only, and org/Tiny-GGUF:Q4_K_M is not one. Choose a chat model above.",
		);
		expect(screen.getByRole("button", { name: "Run" })).toHaveProperty("disabled", true);
	});
});

describe("ModelLabPage inspect tab", () => {
	it("reads no file before its tab shows the page's model", async () => {
		const routes = stubRoutes(() => HttpResponse.json(inspection));
		await renderPage();

		await pickModel();
		await screen.findByTestId("model-lab-architecture-overview-only");

		expect(routes.inspections).toBe(0);
		fireEvent.click(screen.getByRole("tab", { name: "Inspect" }));
		expect(await screen.findByTestId("model-inspect-panel")).toBeTruthy();
		expect(routes.inspections).toBe(1);
	});

	it("opens the shared panel for the chosen installed llama.cpp model", async () => {
		stubRoutes(() => HttpResponse.json(inspection));
		await renderPage();

		await openInspectAndPick();

		expect(await screen.findByTestId("model-inspect-panel")).toBeTruthy();
		expect(screen.getByText("qwen35")).toBeTruthy();
		expect(screen.getByTestId("model-inspect-block-strip")).toBeTruthy();
	});

	it("shows a partial read as the panel's warning", async () => {
		stubRoutes(() => HttpResponse.json({ ...inspection, readFailure: "truncated", blocks: [] }));
		await renderPage();

		await openInspectAndPick();

		expect(await screen.findByText(/could not be read completely \(truncated\)/)).toBeTruthy();
	});

	it("shows a failed request as an error and no panel", async () => {
		stubRoutes(() => HttpResponse.json({ title: "gone" }, { status: 500 }));
		await renderPage();

		await openInspectAndPick();

		await screen.findByRole("alert");
		expect(screen.queryByTestId("model-inspect-panel")).toBeNull();
	});
});

describe("ModelLabPage architecture tab", () => {
	it("reads a linked model's architecture at once on the default tab", async () => {
		const routes = stubRoutes(() => HttpResponse.json(inspection));
		await renderPage({ model: "org/Tiny-GGUF:Q4_K_M" });

		expect(await screen.findByTestId("model-lab-architecture-overview-only")).toBeTruthy();
		expect(routes.architectures).toEqual(["org/Tiny-GGUF:Q4_K_M"]);
	});

	it("reads no architecture for a model picked on Next token or Inspect until its tab shows it", async () => {
		const routes = stubRoutes(() => HttpResponse.json(inspection));
		await renderPage();
		await pickModel();
		await screen.findByTestId("model-lab-architecture-overview-only");
		expect(routes.architectures).toEqual(["org/Tiny-GGUF:Q4_K_M"]);

		fireEvent.click(screen.getByRole("tab", { name: "Next token" }));
		await pickModel("org/Tiny-GGUF:Q3_K_XL");
		fireEvent.click(screen.getByRole("tab", { name: "Inspect" }));
		await pickModel();
		await pickModel("org/Tiny-GGUF:Q3_K_XL");
		await waitFor(() => expect(routes.inspections).toBe(2));
		expect(routes.architectures).toEqual(["org/Tiny-GGUF:Q4_K_M"]);

		fireEvent.click(screen.getByRole("tab", { name: "Architecture" }));
		await waitFor(() => expect(routes.architectures).toEqual(["org/Tiny-GGUF:Q4_K_M", "org/Tiny-GGUF:Q3_K_XL"]));
	});
});

const traceId = "11111111-1111-4111-8111-111111111111";
const routedTensor = (ggufName: string) => ({
	ggufName,
	hfName: null,
	shape: [8],
	ggmlType: "F32",
	byteSize: 32,
	valueRelation: null,
});
// Two full-attention blocks in one chip, of a family whose graph has a router, so a laid-over run may have routing.
const routedDescriptor = {
	...descriptor,
	architecture: "olmoe",
	support: "detailed",
	blocks: [0, 1].map((index) => ({
		index,
		kind: "fullAttention",
		byteSize: 1,
		groups: [
			{
				componentId: `blk.${index}.attention`,
				component: "attention",
				provenance: "read",
				tensors: [routedTensor(`blk.${index}.attn_q.weight`)],
			},
		],
	})),
	templates: [
		{
			kind: "fullAttention",
			provenance: "inferred",
			source: "llama.cpp src/models/olmoe.cpp",
			nodes: [{ id: "router", label: "Router", component: null }],
			edges: [],
		},
	],
	nonBlock: [
		{ componentId: "embedding", component: "embedding", provenance: "read", tensors: [routedTensor("token_embd.weight")] },
	],
};

// The routed descriptor, a finished trace of the model, and a count of every run and PyTorch read.
function stubRoutedModel(): { trace: number; routing: number; pytorch: number } {
	const seen = { trace: 0, routing: 0, pytorch: 0 };
	stubRoutes(() => HttpResponse.json(inspection));
	server.use(
		http.get(localApiPath("model-lab/architecture/:modelName"), () => HttpResponse.json(routedDescriptor)),
		http.get(localApiPath("model-lab/architecture/:modelName/pytorch"), () => {
			seen.pytorch++;
			return new HttpResponse(null, { status: 404 });
		}),
		http.get(localApiPath("training/base-artifacts"), () => HttpResponse.json({ items: [] })),
		http.get(localApiPath("model-lab/experiments"), () =>
			HttpResponse.json({
				platformSupported: true,
				unsupportedMessage: null,
				activeExperimentId: null,
				maxRetainedExperiments: 20,
				maxPinnedExperiments: 20,
				experiments: [
					{
						id: traceId,
						kind: "Trace",
						status: "Succeeded",
						installedModelName: "org/Tiny-GGUF:Q4_K_M",
						repoId: "allenai/OLMoE-1B-7B-0924-Instruct",
						revision: "rev",
						prompt: "hello",
						errorMessage: null,
						traceByteCount: 10,
						result: null,
						createdAtUtc: "2026-11-23T00:00:00Z",
						updatedAtUtc: "2026-11-23T00:00:00Z",
						isPinned: false,
						appVersion: null,
						notes: null,
					},
				],
			}),
		),
		http.get(localApiPath(`model-lab/experiments/${traceId}/trace`), () => {
			seen.trace++;
			return HttpResponse.json({
				records: [{ step: 0, layer: 1, kind: "F", norm: 2, percentiles: [0, 1, 5], topIndices: [7], topValues: [1] }],
				truncated: false,
				droppedRecords: 0,
				byteCap: 1000,
			});
		}),
		http.get(localApiPath(`model-lab/experiments/${traceId}/routing`), () => {
			seen.routing++;
			return HttpResponse.json({
				layers: [0, 1],
				experts: 8,
				expertsPerToken: 2,
				normalizedTopK: false,
				steps: [{ step: 0, blocks: [{ layer: 1, experts: [3, 5], weights: [0.5, 0.25] }] }],
				truncated: false,
				droppedSteps: 0,
			});
		}),
	);
	return seen;
}

// Holds the search as the route would; "Change the URL" applies `external` as Back or an edited link does.
function SearchHolder({ initial, external }: { initial: ModelLabSearch; external: ModelLabSearch }) {
	const [search, setSearch] = useState(initial);
	return (
		<>
			<button type="button" onClick={() => setSearch((previous) => applyModelLabPatch(previous, external))}>
				Change the URL
			</button>
			<ModelLabPage search={search} onSearchChange={(update) => setSearch((previous) => applyModelLabPatch(previous, update))} />
		</>
	);
}

describe("ModelLabPage architecture hierarchy", () => {
	it("moves focus to the new heading when a detail control opens a block, and back up through the breadcrumb", async () => {
		stubRoutedModel();
		await renderPage();
		await pickModel();

		fireEvent.click(await screen.findByRole("button", { name: "0–1 · Full attention ×2" }));
		fireEvent.click(await screen.findByRole("button", { name: "Open block 1" }));

		expect(document.activeElement).toBe(screen.getByRole("heading", { name: "Block 1: Full attention" }));
		fireEvent.click(within(screen.getByTestId("model-lab-architecture-breadcrumb")).getByRole("button", { name: "Model" }));
		expect(document.activeElement).toBe(screen.getByRole("heading", { name: "Whole model" }));
	});

	it("opens no detail and reads no run while Architecture is hidden, and catches up once it shows", async () => {
		const seen = stubRoutedModel();
		await act(async () => {
			renderWithProviders(
				<SearchHolder
					initial={{ model: "org/Tiny-GGUF:Q4_K_M", block: 0 }}
					external={{ tab: "inspect", block: 1, trace: traceId }}
				/>,
				{ withRouter: true },
			);
		});
		await screen.findByRole("heading", { name: "Block 0: Full attention" });
		await waitFor(() => expect(seen.pytorch).toBe(1));

		// Inspect shows, and the URL now names another block and a run.
		fireEvent.click(screen.getByRole("button", { name: "Change the URL" }));
		await screen.findByTestId("model-inspect-panel");
		await act(async () => undefined);

		expect(seen.trace).toBe(0);
		expect(seen.routing).toBe(0);
		expect(seen.pytorch).toBe(1);
		// The hidden panel still shows block 0: nothing new mounted.
		expect(screen.getByRole("heading", { name: "Block 0: Full attention", hidden: true })).toBeTruthy();
		expect(screen.queryByRole("heading", { name: "Block 1: Full attention", hidden: true })).toBeNull();

		fireEvent.click(screen.getByRole("tab", { name: "Architecture" }));

		expect(await screen.findByRole("heading", { name: "Block 1: Full attention" })).toBeTruthy();
		await waitFor(() => expect(seen.trace).toBe(1));
		await waitFor(() => expect(seen.routing).toBe(1));
		expect(await screen.findByRole("button", { name: "Block 1: Full attention, traced, routed" })).toBeTruthy();
	});
});
