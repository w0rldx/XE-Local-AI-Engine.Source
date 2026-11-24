// @vitest-environment jsdom

// The page's model picker and the Inspect tab against stubbed routes (MSW): the picker lists every installed GGUF and says
// why it has nothing to offer; Inspect opens the shared panel for the page's model once its tab shows it, and shows both
// ways a read can fail.

import { act, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { HttpResponse, http } from "msw";
import { describe, expect, it, vi } from "vitest";

import type { ModelLabSearch } from "@/features/modelLab/models/ModelLabNavigation";
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
