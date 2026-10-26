// @vitest-environment jsdom

import { QueryClient } from "@tanstack/react-query";
import { cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { ExternalProviderConnectionDto } from "@/features/cloud-providers/external/models/ExternalProviderFormState";

// The three reads the page composes. Everything else in the generated module stays real; no test here writes.
const { generatedMock } = vi.hoisted(() => ({
	generatedMock: {
		listFn: vi.fn(),
		cloudSettingsFn: vi.fn(),
		codexStatusFn: vi.fn(),
	},
}));

vi.mock("@/core/api/generated/@tanstack/react-query.gen", async (importOriginal) => {
	const actual = await importOriginal<typeof import("@/core/api/generated/@tanstack/react-query.gen")>();
	return {
		...actual,
		listExternalProviderConnectionsOptions: () => ({
			queryKey: [{ _id: "listExternalProviderConnections" }],
			queryFn: generatedMock.listFn,
		}),
		getCloudSettingsOptions: () => ({ queryKey: [{ _id: "getCloudSettings" }], queryFn: generatedMock.cloudSettingsFn }),
		codexStatusOptions: () => ({ queryKey: [{ _id: "codexStatus" }], queryFn: generatedMock.codexStatusFn }),
	};
});

// The editors ask before deleting; no test here gets that far.
vi.mock("@/core/ui/hooks/useConfirm", () => ({ useConfirm: () => ({ confirm: vi.fn() }) }));

import { CloudProviders } from "@/features/cloud-providers/pages/CloudProviders";
import { renderWithProviders } from "@/test/RenderWithProviders";

const connection: ExternalProviderConnectionDto = {
	id: "office-gateway",
	displayName: "Office gateway",
	baseUrl: "https://gw.example.com/openai/v1",
	locality: "Cloud",
	cloudGrants: { localData: false, unattendedRuns: false, webTools: false, mcpTools: false, subAgents: false },
	hasApiKey: true,
	timeoutSeconds: 120,
	allowInsecureHttp: false,
	insecureTransport: false,
	models: [],
};

const storedAzure = {
	providerName: "AzureFoundry",
	azureFoundry: {
		endpoint: "https://contoso.openai.azure.com",
		authMode: "ApiKey",
		hasStoredApiKey: true,
		models: [{ deploymentName: "gpt-6" }, { deploymentName: "gpt-6-mini" }],
	},
};

function renderPage(): void {
	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
	renderWithProviders(<CloudProviders />, { queryClient });
}

async function openTemplate(id: string): Promise<HTMLElement> {
	await waitFor(() => expect(screen.queryByText("Loading providers…")).toBeNull());
	fireEvent.click(screen.getByTestId("cloud-provider-add"));
	return await screen.findByTestId(`cloud-provider-template-${id}`);
}

describe("CloudProviders page", () => {
	beforeEach(() => {
		generatedMock.listFn.mockResolvedValue({ revision: "rev-1", connections: [] });
		generatedMock.cloudSettingsFn.mockResolvedValue({ providerName: "AzureFoundry", azureFoundry: null });
		generatedMock.codexStatusFn.mockResolvedValue({ signedIn: false });
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it("says nothing is set up when no provider of any kind is configured", async () => {
		renderPage();

		expect(await screen.findByTestId("cloud-provider-list-empty")).toBeTruthy();
	});

	it("lists a connection, the Azure Foundry slot and the Codex session together", async () => {
		generatedMock.listFn.mockResolvedValue({ revision: "rev-1", connections: [connection] });
		generatedMock.cloudSettingsFn.mockResolvedValue(storedAzure);
		generatedMock.codexStatusFn.mockResolvedValue({ signedIn: true, accountId: "acct-42" });
		renderPage();

		const external = await screen.findByTestId("external-provider-connection-office-gateway");
		expect(within(external).getByText("Office gateway")).toBeTruthy();
		expect(within(external).getByText("OpenAI-compatible")).toBeTruthy();

		const azure = await screen.findByTestId("cloud-provider-azure-foundry");
		expect(within(azure).getByText("https://contoso.openai.azure.com")).toBeTruthy();
		expect(within(azure).getByText("2 registered models")).toBeTruthy();

		const codex = await screen.findByTestId("cloud-provider-codex");
		expect(within(codex).getByText("acct-42")).toBeTruthy();
		// A live Codex session keeps its egress warning on the page.
		expect(screen.getByText(/your messages leave your device/)).toBeTruthy();
	});

	it("opens the Azure Foundry editor from its list entry and closes it again", async () => {
		generatedMock.cloudSettingsFn.mockResolvedValue(storedAzure);
		renderPage();

		fireEvent.click(await screen.findByTestId("cloud-provider-azure-foundry-edit"));

		expect(await screen.findByTestId("azure-foundry-panel")).toBeTruthy();
		fireEvent.click(screen.getByTestId("cloud-provider-close"));
		expect(screen.queryByTestId("azure-foundry-panel")).toBeNull();
	});

	it("opens the Codex card with its sign-out from the list entry", async () => {
		generatedMock.codexStatusFn.mockResolvedValue({ signedIn: true, accountId: "acct-42" });
		renderPage();

		fireEvent.click(await screen.findByTestId("cloud-provider-codex-edit"));

		expect(await screen.findByRole("button", { name: "Sign out" })).toBeTruthy();
	});

	it("disables the single-slot templates whose slot is taken, and says why", async () => {
		generatedMock.cloudSettingsFn.mockResolvedValue(storedAzure);
		generatedMock.codexStatusFn.mockResolvedValue({ signedIn: true, accountId: "acct-42" });
		renderPage();
		await screen.findByTestId("cloud-provider-codex");

		const azure = await openTemplate("azureFoundry");
		const codex = screen.getByTestId("cloud-provider-template-codex");

		expect(azure.hasAttribute("disabled") || azure.hasAttribute("data-disabled")).toBe(true);
		expect(codex.hasAttribute("disabled") || codex.hasAttribute("data-disabled")).toBe(true);
		expect(within(azure).getByText(/This node holds one Azure AI Foundry connection/)).toBeTruthy();
		expect(within(codex).getByText(/Already signed in/)).toBeTruthy();
		const generic = screen.getByTestId("cloud-provider-template-generic");
		expect(generic.hasAttribute("disabled") || generic.hasAttribute("data-disabled")).toBe(false);
	});

	it("opens the existing editor for each free single-slot template", async () => {
		renderPage();

		fireEvent.click(await openTemplate("azureFoundry"));
		expect(await screen.findByTestId("azure-foundry-panel")).toBeTruthy();

		fireEvent.click(await openTemplate("codex"));
		expect(await screen.findByRole("button", { name: "Sign in with ChatGPT" })).toBeTruthy();
		expect(screen.queryByTestId("azure-foundry-panel")).toBeNull();
	});

	it("pre-fills a new connection from the enterprise gateway template", async () => {
		renderPage();

		fireEvent.click(await openTemplate("gateway"));

		await screen.findByTestId("external-provider-editor");
		const baseUrl = screen.getByTestId("external-provider-base-url") as HTMLInputElement;
		expect(baseUrl.value).toBe("");
		expect(baseUrl.placeholder).toBe("https://<host>/…/openai/v1");
		// One header row, its name left for the operator: the gateway's header name is never guessed.
		const headerName = screen.getByTestId("external-provider-header-name-0") as HTMLInputElement;
		expect(headerName.value).toBe("");
		expect(headerName.placeholder).toBe("Header name from your gateway");
		expect((screen.getByTestId("external-provider-header-secret-0") as HTMLInputElement).checked).toBe(false);
		expect(screen.getByTestId("external-provider-template-hint").textContent).toContain(
			"Put the bearer token in the API key field",
		);
		expect(screen.getByTestId("external-provider-cloud-grants")).toBeTruthy();
	});

	it("shows the Azure OpenAI address shape as a placeholder for that template", async () => {
		renderPage();

		fireEvent.click(await openTemplate("azureOpenAi"));

		const baseUrl = (await screen.findByTestId("external-provider-base-url")) as HTMLInputElement;
		expect(baseUrl.placeholder).toBe("https://<resource>.openai.azure.com/openai/v1");
		expect(screen.queryByTestId("external-provider-header-name-0")).toBeNull();
	});
});
