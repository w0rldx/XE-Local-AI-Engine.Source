// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// Mock the API module so tests never hit the network.
const { getCloudSettingsMock, saveCloudSettingsMock } = vi.hoisted(() => ({
	getCloudSettingsMock: vi.fn(),
	saveCloudSettingsMock: vi.fn(),
}));

vi.mock("@/features/cloud-settings/api/CloudSettingsApi", () => ({
	getCloudSettings: getCloudSettingsMock,
	saveCloudSettings: saveCloudSettingsMock,
	clearCloudSettings: vi.fn(),
}));

import { CloudSettings } from "@/features/cloud-settings/pages/CloudSettings";
import type { CloudSettingsDto } from "@/features/cloud-settings/api/CloudSettingsApi";

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

function makeSettings(overrides: Partial<CloudSettingsDto> = {}): CloudSettingsDto {
	return {
		providerName: "AzureFoundry",
		endpoint: null,
		deploymentName: null,
		hasStoredApiKey: false,
		...overrides,
	};
}

function renderCloudSettings(): void {
	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
	const ui: ReactElement = (
		<QueryClientProvider client={queryClient}>
			<MantineProvider>
				<CloudSettings />
			</MantineProvider>
		</QueryClientProvider>
	);
	render(ui);
}

describe("CloudSettings — lazy validation", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		getCloudSettingsMock.mockResolvedValue(makeSettings());
		saveCloudSettingsMock.mockResolvedValue(makeSettings());
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
});
