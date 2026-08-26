// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { GetCloudSettingsResponse } from "@/core/api/generated";

// Mock the generated TanStack data layer so tests never hit the network.
const { generatedMock } = vi.hoisted(() => ({
	generatedMock: {
		getCloudSettingsOptions: vi.fn(),
		saveCloudSettingsMutation: vi.fn(),
		clearCloudSettingsMutation: vi.fn(),
		// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
		getCloudSettingsQueryKey: vi.fn(() => [{ _id: "getCloudSettings" }]),
		getFn: vi.fn(),
		saveFn: vi.fn(),
		clearFn: vi.fn(),
	},
}));

vi.mock("@/core/api/generated/@tanstack/react-query.gen", async (importOriginal) => {
	// Spread the real module so codex symbols (used by CodexSignInCard via useCodexAuth)
	// are present; override only the cloud-settings symbols this test controls.
	const actual = await importOriginal<typeof import("@/core/api/generated/@tanstack/react-query.gen")>();
	return {
		...actual,
		getCloudSettingsOptions: generatedMock.getCloudSettingsOptions,
		getCloudSettingsQueryKey: generatedMock.getCloudSettingsQueryKey,
		saveCloudSettingsMutation: generatedMock.saveCloudSettingsMutation,
		clearCloudSettingsMutation: generatedMock.clearCloudSettingsMutation,
	};
});

import { CloudSettings } from "@/features/cloud-settings/pages/CloudSettings";

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
	// Mantine's SegmentedControl uses FloatingIndicator, which depends on ResizeObserver.
	Object.defineProperty(window, "ResizeObserver", {
		writable: true,
		value: class ResizeObserverMock {
			observe = vi.fn();

			unobserve = vi.fn();

			disconnect = vi.fn();
		},
	});
}

function makeSettings(overrides: Partial<GetCloudSettingsResponse> = {}): GetCloudSettingsResponse {
	return {
		providerName: "AzureFoundry",
		azureFoundry: { endpoint: null, authMode: "ApiKey", hasStoredApiKey: false, models: [] },
		...overrides,
	};
}

function renderCloudSettings(): void {
	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
	const ui: ReactElement = (
		<QueryClientProvider client={queryClient}>
			<MantineProvider>
				<CloudSettings />
			</MantineProvider>
		</QueryClientProvider>
	);
	render(ui);
}

describe("CloudSettings — Azure Foundry connection form (generated hey-api data layer)", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		generatedMock.getFn.mockResolvedValue(makeSettings());
		generatedMock.saveFn.mockResolvedValue(makeSettings());
		generatedMock.clearFn.mockResolvedValue(makeSettings());
		generatedMock.getCloudSettingsOptions.mockReturnValue({
			// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
			queryKey: [{ _id: "getCloudSettings" }],
			queryFn: generatedMock.getFn,
		});
		generatedMock.saveCloudSettingsMutation.mockReturnValue({ mutationFn: generatedMock.saveFn });
		generatedMock.clearCloudSettingsMutation.mockReturnValue({ mutationFn: generatedMock.clearFn });
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it("shows no field errors on pristine first load", async () => {
		renderCloudSettings();

		await waitFor(() => expect(screen.queryByText(/Loading cloud settings/i)).toBeNull());

		expect(screen.queryByText("Enter an absolute HTTPS Azure OpenAI endpoint.")).toBeNull();
		expect(screen.queryByText("Add at least one deployment name.")).toBeNull();
		expect(screen.queryByText("Enter the API key. Saved keys are never returned to this page.")).toBeNull();
	});

	it("shows an error for endpoint only after blurring that field", async () => {
		renderCloudSettings();
		await waitFor(() => expect(screen.queryByText(/Loading cloud settings/i)).toBeNull());

		fireEvent.blur(screen.getByLabelText(/Azure OpenAI endpoint/i));

		expect(screen.getByText("Enter an absolute HTTPS Azure OpenAI endpoint.")).toBeTruthy();
		expect(screen.queryByText("Add at least one deployment name.")).toBeNull();
		expect(screen.queryByText("Enter the API key. Saved keys are never returned to this page.")).toBeNull();
	});

	it("hides the API-key field and drops its requirement when managed identity is selected", async () => {
		renderCloudSettings();
		await waitFor(() => expect(screen.queryByText(/Loading cloud settings/i)).toBeNull());

		// The API key input is present in API-key mode (the default). Scope to the password input so the
		// segmented control's "API key" option label does not also match.
		expect(screen.getByLabelText("API key", { selector: 'input[type="password"]' })).toBeTruthy();

		// Switch to managed identity via the segmented control label.
		fireEvent.click(screen.getByText("Managed identity"));

		// The key field disappears and its hint is shown instead.
		expect(screen.queryByLabelText("API key", { selector: 'input[type="password"]' })).toBeNull();
		expect(screen.queryByText("Enter the API key. Saved keys are never returned to this page.")).toBeNull();
	});

	it("adds and removes deployment rows", async () => {
		renderCloudSettings();
		await waitFor(() => expect(screen.queryByText(/Loading cloud settings/i)).toBeNull());

		// Fresh connection renders exactly one blank deployment row.
		expect(screen.getAllByLabelText(/Deployment name/i)).toHaveLength(1);

		fireEvent.click(screen.getByTestId("cloud-settings-add-model"));
		expect(screen.getAllByLabelText(/Deployment name/i)).toHaveLength(2);

		const secondDeployment = screen.getAllByLabelText(/Deployment name/i)[1] as HTMLInputElement;
		secondDeployment.focus();
		fireEvent.click(screen.getByTestId("cloud-settings-remove-model-0"));
		expect(screen.getAllByLabelText(/Deployment name/i)).toHaveLength(1);
		expect(document.activeElement).toBe(secondDeployment);
	});

	it("saves the nested Azure Foundry connection body", async () => {
		generatedMock.getFn.mockResolvedValue(
			makeSettings({
				azureFoundry: {
					endpoint: "https://example.openai.azure.com/",
					authMode: "ApiKey",
					hasStoredApiKey: true,
					models: [{ deploymentName: "gpt-4o", displayLabel: null }],
				},
			}),
		);
		renderCloudSettings();
		await waitFor(() => expect(screen.queryByText(/Loading cloud settings/i)).toBeNull());

		// Provide a valid API key so the save enables (endpoint + model come from the loaded settings).
		fireEvent.change(screen.getByLabelText("API key", { selector: 'input[type="password"]' }), {
			target: { value: "secret-key" },
		});

		fireEvent.click(screen.getByRole("button", { name: /save cloud settings/i }));

		await waitFor(() => {
			expect(generatedMock.saveFn.mock.calls[0]?.[0]).toEqual({
				body: {
					providerName: "AzureFoundry",
					endpoint: "https://example.openai.azure.com/",
					authMode: "ApiKey",
					apiSurface: "AzureDeployments",
					apiKey: "secret-key",
					models: [{ deploymentName: "gpt-4o" }],
					headers: [],
					additionalAllowedHostSuffixes: [],
					// Always sent; the backend ignores these outside EntraId mode.
					entraTenantId: "",
					entraClientId: "",
					entraClientSecret: undefined,
					entraTokenScope: "",
					entraSignInMethod: "DeviceCode",
				},
			});
		});
	});

	it("adds, edits, toggles secret on, and removes custom header rows", async () => {
		renderCloudSettings();
		await waitFor(() => expect(screen.queryByText(/Loading cloud settings/i)).toBeNull());

		// No header rows on a fresh connection.
		expect(screen.queryAllByLabelText("Header name")).toHaveLength(0);

		fireEvent.click(screen.getByTestId("cloud-settings-add-header"));
		expect(screen.getAllByLabelText("Header name")).toHaveLength(1);

		// The value input starts as a text input; toggling Secret swaps it to a password input.
		expect(screen.getByLabelText("Value", { selector: 'input:not([type="password"])' })).toBeTruthy();
		fireEvent.click(screen.getByTestId("cloud-settings-header-secret-0"));
		expect(screen.getByLabelText("Value", { selector: 'input[type="password"]' })).toBeTruthy();

		fireEvent.click(screen.getByTestId("cloud-settings-remove-header-0"));
		expect(screen.queryAllByLabelText("Header name")).toHaveLength(0);
	});

	it("adds and removes allowed host suffix rows", async () => {
		renderCloudSettings();
		await waitFor(() => expect(screen.queryByText(/Loading cloud settings/i)).toBeNull());

		expect(screen.queryAllByLabelText("Host suffix")).toHaveLength(0);
		fireEvent.click(screen.getByTestId("cloud-settings-add-host"));
		expect(screen.getAllByLabelText("Host suffix")).toHaveLength(1);
		fireEvent.click(screen.getByTestId("cloud-settings-remove-host-0"));
		expect(screen.queryAllByLabelText("Host suffix")).toHaveLength(0);
	});

	it("shows the managed-identity egress warning for a non-Azure host matched by an operator suffix", async () => {
		generatedMock.getFn.mockResolvedValue(
			makeSettings({
				azureFoundry: {
					endpoint: "https://gw.azure-api.net/",
					authMode: "ManagedIdentity",
					hasStoredApiKey: false,
					models: [{ deploymentName: "gpt-4o", displayLabel: null }],
					additionalAllowedHostSuffixes: [".azure-api.net"],
				},
			}),
		);
		renderCloudSettings();
		await waitFor(() => expect(screen.queryByText(/Loading cloud settings/i)).toBeNull());

		expect(screen.getByTestId("cloud-settings-mi-egress-warning")).toBeTruthy();
	});

	it("does not call the save mutation when a validation error is present", async () => {
		renderCloudSettings();
		await waitFor(() => expect(screen.queryByText(/Loading cloud settings/i)).toBeNull());

		// Fresh connection has no endpoint/deployment/API key, so validateCloudSettingsForm reports errors, the Save
		// button is disabled (disabled={hasErrors || isActionPending}), and handleSave's own `if (hasErrors) return;`
		// guard is what actually keeps the mutation from firing — the button's disabled attribute mirrors the same
		// `hasErrors` check, so this asserts the end-to-end guarantee that an invalid form never reaches saveFn.
		const saveButton = screen.getByRole("button", { name: /save cloud settings/i }) as HTMLButtonElement;
		expect(saveButton.disabled).toBe(true);
		fireEvent.click(saveButton);

		expect(generatedMock.saveFn).not.toHaveBeenCalled();
	});

	it("omits the API key from the save body in managed-identity mode", async () => {
		generatedMock.getFn.mockResolvedValue(
			makeSettings({
				azureFoundry: {
					endpoint: "https://example.openai.azure.com/",
					authMode: "ManagedIdentity",
					hasStoredApiKey: false,
					models: [{ deploymentName: "gpt-4o", displayLabel: null }],
				},
			}),
		);
		renderCloudSettings();
		await waitFor(() => expect(screen.queryByText(/Loading cloud settings/i)).toBeNull());

		// No key field in MI mode — the save button is already enabled.
		fireEvent.click(screen.getByRole("button", { name: /save cloud settings/i }));

		await waitFor(() => {
			const body = generatedMock.saveFn.mock.calls[0]?.[0] as { body: Record<string, unknown> };
			expect(body.body["authMode"]).toBe("ManagedIdentity");
			expect(body.body["apiKey"]).toBeUndefined();
		});
	});

	it("shows the EntraId fields (and hides the API key field) once EntraId is selected", async () => {
		renderCloudSettings();
		await waitFor(() => expect(screen.queryByText(/Loading cloud settings/i)).toBeNull());

		fireEvent.click(screen.getByTestId("cloud-settings-auth-mode").querySelector('input[value="EntraId"]') as HTMLElement);

		expect(screen.getByLabelText("Tenant ID")).toBeTruthy();
		expect(screen.getByLabelText("Client ID")).toBeTruthy();
		expect(screen.getByLabelText("Token scope")).toBeTruthy();
		// "API key" also names the SegmentedControl's ApiKey option (each segment is itself a <label>), so scope
		// the query to the password-style API key field specifically.
		expect(screen.queryByLabelText("API key", { selector: 'input[type="password"]' })).toBeNull();
		// No stored secret yet, so the sign-in method picker is shown.
		expect(screen.getByTestId("cloud-settings-entra-sign-in-method")).toBeTruthy();
	});

	it("requires tenant, client id, and token scope before saving in EntraId mode", async () => {
		renderCloudSettings();
		await waitFor(() => expect(screen.queryByText(/Loading cloud settings/i)).toBeNull());

		fireEvent.click(screen.getByTestId("cloud-settings-auth-mode").querySelector('input[value="EntraId"]') as HTMLElement);
		const saveButton = screen.getByRole("button", { name: /save cloud settings/i }) as HTMLButtonElement;
		expect(saveButton.disabled).toBe(true);

		fireEvent.click(saveButton);
		expect(generatedMock.saveFn).not.toHaveBeenCalled();
	});

	it("saves the EntraId connection fields in the save body", async () => {
		// Pre-load a valid endpoint + model in EntraId mode so only the EntraId fields need filling — mirrors the
		// "omits the API key..." managed-identity test's approach of loading the target auth mode directly.
		generatedMock.getFn.mockResolvedValue(
			makeSettings({
				azureFoundry: {
					endpoint: "https://gw.azure-api.net/",
					authMode: "EntraId",
					hasStoredApiKey: false,
					models: [{ deploymentName: "gpt-4o", displayLabel: null }],
				},
			}),
		);
		renderCloudSettings();
		await waitFor(() => expect(screen.queryByText(/Loading cloud settings/i)).toBeNull());

		fireEvent.change(screen.getByLabelText("Tenant ID"), { target: { value: "tenant-id" } });
		fireEvent.change(screen.getByLabelText("Client ID"), { target: { value: "client-id" } });
		fireEvent.change(screen.getByLabelText("Token scope"), { target: { value: "api://backend-app-id/.default" } });

		fireEvent.click(screen.getByRole("button", { name: /save cloud settings/i }));

		await waitFor(() => {
			const body = generatedMock.saveFn.mock.calls[0]?.[0] as { body: Record<string, unknown> };
			expect(body.body["authMode"]).toBe("EntraId");
			expect(body.body["entraTenantId"]).toBe("tenant-id");
			expect(body.body["entraClientId"]).toBe("client-id");
			expect(body.body["entraTokenScope"]).toBe("api://backend-app-id/.default");
			expect(body.body["entraSignInMethod"]).toBe("DeviceCode");
		});
	});

	it("hides the sign-in method picker once a client secret is present", async () => {
		renderCloudSettings();
		await waitFor(() => expect(screen.queryByText(/Loading cloud settings/i)).toBeNull());

		fireEvent.click(screen.getByTestId("cloud-settings-auth-mode").querySelector('input[value="EntraId"]') as HTMLElement);
		expect(screen.getByTestId("cloud-settings-entra-sign-in-method")).toBeTruthy();

		fireEvent.change(screen.getByLabelText("Client secret"), { target: { value: "a-secret" } });
		expect(screen.queryByTestId("cloud-settings-entra-sign-in-method")).toBeNull();
	});

	it("shows the device-code sign-in card only for a saved EntraId+DeviceCode connection", async () => {
		generatedMock.getFn.mockResolvedValue(
			makeSettings({
				azureFoundry: {
					endpoint: "https://gw.azure-api.net/",
					authMode: "EntraId",
					hasStoredApiKey: false,
					models: [{ deploymentName: "gpt-4o", displayLabel: null }],
					entraTenantId: "tenant-id",
					entraClientId: "client-id",
					entraSignInMethod: "DeviceCode",
				},
			}),
		);
		renderCloudSettings();
		await waitFor(() => expect(screen.queryByText(/Loading cloud settings/i)).toBeNull());

		expect(screen.getByTestId("entra-device-code-sign-in-card")).toBeTruthy();
	});

	it("does not show the device-code sign-in card when the sign-in method is InteractiveBrowser", async () => {
		generatedMock.getFn.mockResolvedValue(
			makeSettings({
				azureFoundry: {
					endpoint: "https://gw.azure-api.net/",
					authMode: "EntraId",
					hasStoredApiKey: false,
					models: [{ deploymentName: "gpt-4o", displayLabel: null }],
					entraTenantId: "tenant-id",
					entraClientId: "client-id",
					entraSignInMethod: "InteractiveBrowser",
				},
			}),
		);
		renderCloudSettings();
		await waitFor(() => expect(screen.queryByText(/Loading cloud settings/i)).toBeNull());

		expect(screen.queryByTestId("entra-device-code-sign-in-card")).toBeNull();
	});
});
