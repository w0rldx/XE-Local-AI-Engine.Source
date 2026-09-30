// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

// The affordance derives eligibility from the installed-model list; stub only that query so the rest of the generated
// TanStack module (and the real withResponseValidation bridge) stays live.
const { listLocalModelsSpy, runningModelsState } = vi.hoisted(() => ({
	listLocalModelsSpy: vi.fn(),
	runningModelsState: { data: [] as unknown[] },
}));
vi.mock("@/core/api/generated/@tanstack/react-query.gen", async (importOriginal) => ({
	...(await importOriginal<typeof import("@/core/api/generated/@tanstack/react-query.gen")>()),
	listLocalModelsOptions: () => ({
		queryKey: [{ _id: "listLocalModels" }],
		queryFn: listLocalModelsSpy,
	}),
}));
// Ollama is not configured on these nodes, so its in-memory list is empty; the llama.cpp residents come from the
// running-models poll, which each test sets. Both polls would otherwise issue real requests.
vi.mock("@/features/loaded-models/queries/useLoadedModels", () => ({
	useLoadedModels: () => ({ data: { isAvailable: true, ollamaConfigured: false, error: null, models: [] } }),
}));
vi.mock("@/features/loaded-models/queries/useRunningModels", () => ({
	useRunningModels: () => runningModelsState,
}));

import { AssistActions } from "@/features/assist/components/AssistActions";
import { renderWithProviders } from "@/test/RenderWithProviders";

const chatModel = { modelName: "qwen3-4b", kind: "Chat", provider: "llamacpp" };
const embeddingModel = { modelName: "nomic-embed", kind: "Embedding", provider: "llamacpp" };
const cloudModel = { modelName: "gpt-5", kind: "Chat", provider: "CodexOAuth" };
// Newest first wins the fallback default, so without a warm match the picker lands on this one, not on qwen3-4b.
const newerChatModel = { modelName: "llama-3b", kind: "Chat", provider: "llamacpp", modifiedAtUtc: 2_000 };

const notLoadedNote = "This model is not loaded — the first generation will load it, which takes longer.";

function resident(overrides: Record<string, unknown> = {}) {
	return {
		modelName: "qwen3-4b",
		role: "chat",
		isResponsive: true,
		detail: "",
		detailCode: "responsive",
		isBusy: false,
		lastUsedUtc: null,
		isTransient: false,
		...overrides,
	};
}

function renderActions(existingContent = "") {
	return renderWithProviders(
		<AssistActions
			surface="skill"
			existing={{ name: "", description: "", content: existingContent }}
			onApply={vi.fn()}
			onDiscard={vi.fn()}
		/>,
	);
}

async function openDialog(): Promise<HTMLInputElement> {
	renderActions();
	await waitFor(() => expect(screen.getByTestId("assist-open-create")).toHaveProperty("disabled", false));
	fireEvent.click(screen.getByTestId("assist-open-create"));
	return (await screen.findByTestId("assist-model")) as HTMLInputElement;
}

afterEach(() => {
	cleanup();
	vi.clearAllMocks();
	runningModelsState.data = [];
});

describe("AssistActions", () => {
	it("disables the affordance when the node has no eligible local chat model", async () => {
		// Neither an embedding model nor a cloud entry can draft: the endpoint is local-chat-only, fail-closed.
		listLocalModelsSpy.mockResolvedValue({ items: [embeddingModel, cloudModel] });

		renderActions();

		await waitFor(() => expect(listLocalModelsSpy).toHaveBeenCalled());
		await waitFor(() => expect(screen.getByTestId("assist-open-create")).toHaveProperty("disabled", true));
	});

	it("enables the affordance once a local chat model is installed", async () => {
		listLocalModelsSpy.mockResolvedValue({ items: [chatModel] });

		renderActions();

		await waitFor(() => expect(screen.getByTestId("assist-open-create")).toHaveProperty("disabled", false));
	});

	it("offers Improve only once the form has content to improve", async () => {
		listLocalModelsSpy.mockResolvedValue({ items: [chatModel] });

		renderActions();
		await waitFor(() => expect(screen.getByTestId("assist-open-create")).toBeTruthy());
		expect(screen.queryByTestId("assist-open-improve")).toBeNull();

		cleanup();
		renderActions("# Existing body");
		await waitFor(() => expect(screen.getByTestId("assist-open-improve")).toBeTruthy());
	});

	it("defaults to a warm llama.cpp chat resident and drops the not-loaded note", async () => {
		listLocalModelsSpy.mockResolvedValue({ items: [chatModel, newerChatModel] });
		runningModelsState.data = [resident()];

		const picker = await openDialog();

		await waitFor(() => expect(picker.value).toBe("qwen3-4b"));
		expect(screen.queryByText(notLoadedNote)).toBeNull();
	});

	it("matches a resident whose name differs only in case and whitespace", async () => {
		listLocalModelsSpy.mockResolvedValue({ items: [chatModel, newerChatModel] });
		runningModelsState.data = [resident({ modelName: " QWEN3-4B " })];

		const picker = await openDialog();

		await waitFor(() => expect(picker.value).toBe("qwen3-4b"));
		expect(screen.queryByText(notLoadedNote)).toBeNull();
	});

	it("does not count an exited process or an embedding-role process as loaded", async () => {
		listLocalModelsSpy.mockResolvedValue({ items: [chatModel, newerChatModel] });
		runningModelsState.data = [
			resident({ detailCode: "exited", isResponsive: false }),
			resident({ modelName: "llama-3b", role: "embedding" }),
		];

		const picker = await openDialog();

		// No warm match, so the picker falls back to the newest install and still warns it must load.
		await waitFor(() => expect(picker.value).toBe("llama-3b"));
		expect(screen.getByText(notLoadedNote)).toBeTruthy();
	});
});
