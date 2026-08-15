// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("react-i18next", () => ({
	useTranslation: () => ({
		t: (_key: string, defaultValue?: string) => defaultValue ?? _key,
	}),
}));

// The affordance derives eligibility from the installed-model list; stub only that query so the rest of the generated
// TanStack module (and the real withResponseValidation bridge) stays live.
const { listLocalModelsSpy } = vi.hoisted(() => ({ listLocalModelsSpy: vi.fn() }));
vi.mock("@/core/api/generated/@tanstack/react-query.gen", async (importOriginal) => ({
	...(await importOriginal<typeof import("@/core/api/generated/@tanstack/react-query.gen")>()),
	listLocalModelsOptions: () => ({
		// biome-ignore lint/style/useNamingConvention: generated hey-api query-key discriminator.
		queryKey: [{ _id: "listLocalModels" }],
		queryFn: listLocalModelsSpy,
	}),
}));
// Nothing is loaded in these tests; the running-set poll would otherwise issue a real request.
vi.mock("@/features/loaded-models/queries/useLoadedModels", () => ({
	useLoadedModels: () => ({ data: { isAvailable: true, ollamaConfigured: false, error: null, models: [] } }),
}));

import { AssistActions } from "@/features/assist/components/AssistActions";

const chatModel = { modelName: "qwen3-4b", kind: "Chat", provider: "llamacpp" };
const embeddingModel = { modelName: "nomic-embed", kind: "Embedding", provider: "llamacpp" };
const cloudModel = { modelName: "gpt-5", kind: "Chat", provider: "CodexOAuth" };

function renderActions(existingContent = "") {
	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
	return render(
		<QueryClientProvider client={queryClient}>
			<MantineProvider>
				<AssistActions
					surface="skill"
					existing={{ name: "", description: "", content: existingContent }}
					onApply={vi.fn()}
					onDiscard={vi.fn()}
				/>
			</MantineProvider>
		</QueryClientProvider>,
	);
}

beforeEach(() => {
	Object.defineProperty(window, "matchMedia", {
		writable: true,
		value: vi.fn().mockImplementation((query: string) => ({
			matches: false,
			media: query,
			onchange: null,
			addEventListener: vi.fn(),
			removeEventListener: vi.fn(),
			dispatchEvent: vi.fn(),
		})),
	});
});

afterEach(() => {
	cleanup();
	vi.clearAllMocks();
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
});
