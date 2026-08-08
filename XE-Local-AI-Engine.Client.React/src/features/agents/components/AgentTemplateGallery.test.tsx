// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("react-i18next", () => ({
	useTranslation: () => ({
		t: (_key: string, defaultValue?: string, options?: Record<string, unknown>) => {
			let text = defaultValue ?? _key;
			if (options) {
				for (const [name, value] of Object.entries(options)) {
					text = text.replace(`{{${name}}}`, String(value));
				}
			}
			return text;
		},
	}),
}));

const { toastMock, importMutateFn } = vi.hoisted(() => ({
	toastMock: { success: vi.fn(), error: vi.fn(), info: vi.fn(), warn: vi.fn(), warning: vi.fn() },
	importMutateFn: vi.fn(),
}));

vi.mock("@/core/ui/notifications/Toast", () => ({ toast: toastMock }));

// The real useAgentTemplates/useImportAgentTemplates hooks run against the mocked generated TanStack module. The
// list options resolve a deterministic template set; the import mutation records its variables and returns a fixed
// result so the hook's success-path invalidation + toast fire.
vi.mock("@/core/api/generated/@tanstack/react-query.gen", () => ({
	listAgentTemplatesOptions: vi.fn(() => ({
		// biome-ignore lint/style/useNamingConvention: generated hey-api query-key discriminator.
		queryKey: [{ _id: "listAgentTemplates" }],
		queryFn: async () => ({
			items: [
				{
					slug: "engineering-backend-architect",
					name: "Backend architect",
					description: "Designs services",
					division: "engineering",
					estimatedPromptTokens: 1200,
					hasOriginalTools: false,
					alreadyImported: false,
				},
				{
					slug: "engineering-rapid-prototyper",
					name: "Rapid prototyper",
					description: "Ships fast",
					division: "engineering",
					estimatedPromptTokens: 6500,
					hasOriginalTools: false,
					alreadyImported: false,
				},
				{
					slug: "product-feedback-synthesizer",
					name: "Feedback synthesizer",
					description: "Already here",
					division: "product",
					estimatedPromptTokens: 900,
					hasOriginalTools: false,
					alreadyImported: true,
				},
			],
		}),
	})),
	importAgentTemplatesMutation: vi.fn(() => ({
		mutationFn: async (variables: unknown) => {
			importMutateFn(variables);
			return { imported: ["engineering-backend-architect"], skippedExisting: [], unknown: [] };
		},
	})),
	listAgentTemplatesQueryKey: vi.fn(() => [
		// biome-ignore lint/style/useNamingConvention: generated hey-api query-key discriminator.
		{ _id: "listAgentTemplates" },
	]),
}));

import { listAgentTemplatesQueryKey } from "@/core/api/generated/@tanstack/react-query.gen";
import { AgentTemplateGallery } from "@/features/agents/components/AgentTemplateGallery";
import { agentDefinitionsInvalidationKey, agentDefinitionsQueryIds } from "@/features/agents/queries/useAgentDefinitions";

function installJsdomEnvironmentMocks(): void {
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
	Object.defineProperty(window, "ResizeObserver", {
		writable: true,
		value: class ResizeObserverMock {
			observe = vi.fn();

			unobserve = vi.fn();

			disconnect = vi.fn();
		},
	});
	Object.defineProperty(document, "fonts", {
		writable: true,
		value: { ready: Promise.resolve(), addEventListener: vi.fn(), removeEventListener: vi.fn() },
	});
}

function renderGallery(queryClient: QueryClient) {
	return render(
		<MantineProvider>
			<QueryClientProvider client={queryClient}>
				<AgentTemplateGallery opened={true} onClose={vi.fn()} />
			</QueryClientProvider>
		</MantineProvider>,
	);
}

function makeQueryClient(): QueryClient {
	return new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
}

describe("AgentTemplateGallery", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it("renders templates grouped by division", async () => {
		renderGallery(makeQueryClient());

		expect(await screen.findByTestId("agent-template-division-engineering")).toBeTruthy();
		expect(screen.getByTestId("agent-template-division-product")).toBeTruthy();
		expect(screen.getByText("Backend architect")).toBeTruthy();
		expect(screen.getByText("Rapid prototyper")).toBeTruthy();
	});

	it("shows a warning badge for a template over the token budget", async () => {
		renderGallery(makeQueryClient());

		// 6500 > 4000 → filled/yellow warning variant; 1200 stays neutral.
		const overBudgetBadge = await screen.findByTestId("agent-template-token-engineering-rapid-prototyper");
		expect(overBudgetBadge.getAttribute("data-variant")).toBe("filled");

		const underBudgetBadge = screen.getByTestId("agent-template-token-engineering-backend-architect");
		expect(underBudgetBadge.getAttribute("data-variant")).toBe("light");
	});

	it("disables the checkbox of an already-imported template", async () => {
		renderGallery(makeQueryClient());

		const checkbox = (await screen.findByTestId("agent-template-checkbox-product-feedback-synthesizer")) as HTMLInputElement;
		expect(checkbox.disabled).toBe(true);
	});

	it("imports the selected slugs and invalidates the definitions list", async () => {
		const queryClient = makeQueryClient();
		const invalidateSpy = vi.spyOn(queryClient, "invalidateQueries");

		renderGallery(queryClient);

		const checkbox = await screen.findByTestId("agent-template-checkbox-engineering-backend-architect");
		fireEvent.click(checkbox);

		fireEvent.click(screen.getByTestId("agent-template-import-button"));

		await waitFor(() => expect(importMutateFn).toHaveBeenCalledWith({ body: { slugs: ["engineering-backend-architect"] } }));

		// On success the hook invalidates both the agent-definitions list and the templates list. The keys are built
		// via the production helpers (the `_id` discriminator literal lives only there) so the assertion tracks them.
		const definitionsKey = agentDefinitionsInvalidationKey(agentDefinitionsQueryIds.list);
		await waitFor(() => expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: definitionsKey }));
		expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: listAgentTemplatesQueryKey() });
		expect(toastMock.success).toHaveBeenCalled();
	});
});
