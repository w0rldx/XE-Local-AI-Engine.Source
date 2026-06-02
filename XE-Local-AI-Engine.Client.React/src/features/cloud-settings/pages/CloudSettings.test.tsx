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

vi.mock("@/core/api/generated/@tanstack/react-query.gen", () => ({
	getCloudSettingsOptions: generatedMock.getCloudSettingsOptions,
	getCloudSettingsQueryKey: generatedMock.getCloudSettingsQueryKey,
	saveCloudSettingsMutation: generatedMock.saveCloudSettingsMutation,
	clearCloudSettingsMutation: generatedMock.clearCloudSettingsMutation,
}));

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
}

function makeSettings(overrides: Partial<GetCloudSettingsResponse> = {}): GetCloudSettingsResponse {
	return {
		providerName: "AzureFoundry",
		endpoint: null,
		deploymentName: null,
		hasStoredApiKey: false,
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

describe("CloudSettings — lazy validation (generated hey-api data layer)", () => {
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

		// Wait for the query to settle (loading state gone).
		await waitFor(() => expect(screen.queryByText(/Loading cloud settings/i)).toBeNull());

		// No validation error text should be visible.
		// Use exact error message strings so we don't match the PasswordInput description text.
		expect(screen.queryByText("Enter an absolute HTTPS Azure OpenAI endpoint.")).toBeNull();
		expect(screen.queryByText("Enter the Azure OpenAI deployment name.")).toBeNull();
		expect(screen.queryByText("Enter the API key. Saved keys are never returned to this page.")).toBeNull();
	});

	it("shows an error for endpoint only after blurring that field", async () => {
		renderCloudSettings();
		await waitFor(() => expect(screen.queryByText(/Loading cloud settings/i)).toBeNull());

		const endpointInput = screen.getByLabelText(/Azure OpenAI endpoint/i);
		fireEvent.blur(endpointInput);

		expect(screen.getByText("Enter an absolute HTTPS Azure OpenAI endpoint.")).toBeTruthy();
		// Other fields not yet touched — still no errors.
		expect(screen.queryByText("Enter the Azure OpenAI deployment name.")).toBeNull();
		expect(screen.queryByText("Enter the API key. Saved keys are never returned to this page.")).toBeNull();
	});

	it("shows all field errors after blurring all fields", async () => {
		renderCloudSettings();
		await waitFor(() => expect(screen.queryByText(/Loading cloud settings/i)).toBeNull());

		// Blur all three fields to mark them touched — this surfaces all validation errors.
		fireEvent.blur(screen.getByLabelText(/Azure OpenAI endpoint/i));
		fireEvent.blur(screen.getByLabelText(/Deployment name/i));
		const apiKeyInput = screen.getByLabelText(/API key/i);
		fireEvent.blur(apiKeyInput);

		expect(screen.getByText("Enter an absolute HTTPS Azure OpenAI endpoint.")).toBeTruthy();
		expect(screen.getByText("Enter the Azure OpenAI deployment name.")).toBeTruthy();
		expect(screen.getByText("Enter the API key. Saved keys are never returned to this page.")).toBeTruthy();
	});

	it("clears an error once the field has a valid value", async () => {
		renderCloudSettings();
		await waitFor(() => expect(screen.queryByText(/Loading cloud settings/i)).toBeNull());

		const endpointInput = screen.getByLabelText(/Azure OpenAI endpoint/i);
		fireEvent.blur(endpointInput);
		expect(screen.getByText(/Enter an absolute HTTPS/i)).toBeTruthy();

		// Type a valid HTTPS URL then blur again.
		fireEvent.change(endpointInput, { target: { value: "https://example.openai.azure.com/" } });
		fireEvent.blur(endpointInput);

		expect(screen.queryByText(/Enter an absolute HTTPS/i)).toBeNull();
	});

	it("saves through the generated mutation with the cloud settings body", async () => {
		generatedMock.getFn.mockResolvedValue(
			makeSettings({ endpoint: "https://example.openai.azure.com/", deploymentName: "gpt-4o", hasStoredApiKey: true }),
		);
		renderCloudSettings();
		await waitFor(() => expect(screen.queryByText(/Loading cloud settings/i)).toBeNull());

		// Provide a valid API key so the save button enables (endpoint + deployment come from the loaded settings).
		fireEvent.change(screen.getByLabelText(/API key/i), { target: { value: "secret-key" } });

		fireEvent.click(screen.getByRole("button", { name: /save cloud settings/i }));

		await waitFor(() => {
			// TanStack passes a second context arg to mutationFn; assert only the request variables.
			expect(generatedMock.saveFn.mock.calls[0]?.[0]).toEqual({
				body: {
					providerName: "AzureFoundry",
					endpoint: "https://example.openai.azure.com/",
					apiKey: "secret-key",
					deploymentName: "gpt-4o",
				},
			});
		});
	});
});
