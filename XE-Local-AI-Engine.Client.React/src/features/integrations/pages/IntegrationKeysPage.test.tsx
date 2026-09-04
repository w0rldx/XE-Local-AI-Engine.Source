// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ConfirmProvider } from "@/core/ui/components/ConfirmProvider/ConfirmProvider";
import type { IntegrationApiKey, IntegrationTrigger } from "@/features/integrations/models/IntegrationModels";
import { useIntegrationsUiStore } from "@/features/integrations/stores/IntegrationsUiStore";

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

const { keyHooksMock, triggerHooksMock, confirmMock } = vi.hoisted(() => ({
	keyHooksMock: {
		useIntegrationKeys: vi.fn(),
		useGenerateIntegrationApiKey: vi.fn(),
		useRevokeIntegrationApiKey: vi.fn(),
	},
	triggerHooksMock: { useIntegrationTriggers: vi.fn() },
	confirmMock: vi.fn(),
}));

vi.mock("@/features/integrations/queries/useIntegrationKeys", () => keyHooksMock);
vi.mock("@/features/integrations/queries/useIntegrationTriggers", () => triggerHooksMock);
vi.mock("@/core/ui/hooks/useConfirm", () => ({ useConfirm: () => ({ confirm: confirmMock }) }));

import { IntegrationKeysPage } from "@/features/integrations/pages/IntegrationKeysPage";

const PRINCIPAL_A = "3f9c1a2b-0000-0000-0000-000000000001";
const PRINCIPAL_B = "aa11bb22-0000-0000-0000-000000000002";

// Two keys on PRINCIPAL_A (one of them revoked) plus one on PRINCIPAL_B: the identity select must collapse the pair
// into ONE option and still offer the revoked key's principal, because rotating after a revocation is the case the
// control exists for.
const keys: IntegrationApiKey[] = [
	{
		id: "key-1",
		principalId: PRINCIPAL_A,
		keyPrefix: "xeint_aaaa",
		label: "sensor-hub",
		allowedTriggerIds: ["trigger-1"],
		createdAtUtc: 1000,
		lastUsedAtUtc: null,
		revokedAtUtc: null,
	},
	{
		id: "key-2",
		principalId: PRINCIPAL_A,
		keyPrefix: "xeint_bbbb",
		label: "sensor-hub-rotation",
		allowedTriggerIds: null,
		createdAtUtc: 2000,
		lastUsedAtUtc: 3000,
		revokedAtUtc: 4000,
	},
	{
		id: "key-3",
		principalId: PRINCIPAL_B,
		keyPrefix: "xeint_cccc",
		label: "billing-bridge",
		allowedTriggerIds: ["trigger-1"],
		createdAtUtc: 5000,
		lastUsedAtUtc: null,
		revokedAtUtc: null,
	},
];

const triggers: IntegrationTrigger[] = [
	{
		id: "trigger-1",
		name: "sensor-hub",
		displayName: "Sensor hub",
		description: "",
		enabled: true,
		targetAgentDefinitionId: "agent-read",
		sessionPolicy: "PerInvocation",
		acceptedInputKinds: ["text"],
		createdAtUtc: 1000,
		updatedAtUtc: 1000,
		version: 1,
	},
];

function makeMutation() {
	// `reset` is part of the surface the page uses: it drops the mutation cache entry the show-once plaintext arrived
	// in, right after capturing it. Real behaviour is pinned in IntegrationKeysPage.cache.test.tsx against a real client.
	return { mutate: vi.fn(), reset: vi.fn(), isPending: false, error: null };
}

function makeQuery<T>(data: T) {
	return { data, isLoading: false, error: null };
}

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
	window.HTMLElement.prototype.scrollIntoView = vi.fn();
}

function renderPage() {
	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
	return render(
		<MantineProvider>
			<ConfirmProvider>
				<QueryClientProvider client={queryClient}>
					<IntegrationKeysPage />
				</QueryClientProvider>
			</ConfirmProvider>
		</MantineProvider>,
	);
}

async function openGenerateDialog(): Promise<void> {
	fireEvent.click(screen.getByTestId("integration-key-generate-button"));
	await waitFor(() => {
		expect(screen.getByTestId("integration-key-generate-label")).toBeTruthy();
	});
	fireEvent.change(screen.getByTestId("integration-key-generate-label"), { target: { value: "new-key" } });
}

describe("IntegrationKeysPage", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		useIntegrationsUiStore.setState({ editorTarget: null, keyDialogOpen: false });
		keyHooksMock.useIntegrationKeys.mockReturnValue(makeQuery(keys));
		keyHooksMock.useGenerateIntegrationApiKey.mockReturnValue(makeMutation());
		keyHooksMock.useRevokeIntegrationApiKey.mockReturnValue(makeMutation());
		triggerHooksMock.useIntegrationTriggers.mockReturnValue(makeQuery(triggers));
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it("renders prefix, label, last-used and the revoked badge", () => {
		renderPage();

		const row = screen.getByTestId("integration-key-row-key-1");
		expect(within(row).getByText("xeint_aaaa…")).toBeTruthy();
		expect(within(row).getByText("sensor-hub")).toBeTruthy();
		expect(within(row).getByText("Never used yet")).toBeTruthy();
		expect(screen.getByTestId("integration-key-revoked-key-2")).toBeTruthy();
	});

	it("renders the same short principal for two keys sharing an identity", () => {
		renderPage();

		expect(screen.getByTestId("integration-key-principal-key-1").textContent).toBe("3f9c1a2b");
		expect(screen.getByTestId("integration-key-principal-key-2").textContent).toBe("3f9c1a2b");
		expect(screen.getByTestId("integration-key-principal-key-3").textContent).toBe("aa11bb22");
	});

	it("renders the all-triggers wildcard and a named trigger allowlist", () => {
		renderPage();

		expect(within(screen.getByTestId("integration-key-row-key-2")).getByText("All triggers")).toBeTruthy();
		expect(within(screen.getByTestId("integration-key-row-key-1")).getByText("Sensor hub")).toBeTruthy();
	});

	it("offers New identity plus one option per distinct principal", async () => {
		renderPage();
		await openGenerateDialog();

		fireEvent.click(screen.getByTestId("integration-key-generate-principal"));

		expect(await screen.findByRole("option", { name: "New identity", hidden: true })).toBeTruthy();
		// The trigger MultiSelect renders options too, so filter to the identity ones: the two keys on PRINCIPAL_A
		// must collapse into a single option, and the revoked key's principal must still be offered.
		const identityOptions = screen
			.getAllByRole("option", { hidden: true })
			.map((option) => option.textContent ?? "")
			.filter((text) => text.includes("—") || text === "New identity");
		expect(identityOptions).toEqual([
			"New identity",
			"3f9c1a2b — sensor-hub, sensor-hub-rotation",
			"aa11bb22 — billing-bridge",
		]);
	});

	it("sends no principalId for the default New identity", async () => {
		const generateMutation = makeMutation();
		keyHooksMock.useGenerateIntegrationApiKey.mockReturnValue(generateMutation);
		renderPage();
		await openGenerateDialog();

		fireEvent.click(screen.getByTestId("integration-key-generate-triggers"));
		fireEvent.click(await screen.findByRole("option", { name: "Sensor hub", hidden: true }));
		fireEvent.click(screen.getByTestId("integration-key-generate-submit"));

		expect(generateMutation.mutate).toHaveBeenCalledWith(
			{ body: { label: "new-key", allowedTriggerIds: ["trigger-1"] } },
			{ onSuccess: expect.any(Function) },
		);
	});

	it("sends the chosen principalId when an existing identity is reused", async () => {
		const generateMutation = makeMutation();
		keyHooksMock.useGenerateIntegrationApiKey.mockReturnValue(generateMutation);
		renderPage();
		await openGenerateDialog();

		fireEvent.click(screen.getByTestId("integration-key-generate-principal"));
		fireEvent.click(await screen.findByRole("option", { name: "3f9c1a2b — sensor-hub, sensor-hub-rotation", hidden: true }));
		fireEvent.click(screen.getByTestId("integration-key-generate-all-triggers"));
		fireEvent.click(screen.getByTestId("integration-key-generate-submit"));

		expect(generateMutation.mutate).toHaveBeenCalledWith(
			{ body: { label: "new-key", allowedTriggerIds: null, principalId: PRINCIPAL_A } },
			{ onSuccess: expect.any(Function) },
		);
	});

	it("blocks Generate when the allowlist is empty and the wildcard switch is off", async () => {
		// The point of this case: an empty selection must NEVER become the all-triggers wildcard.
		const generateMutation = makeMutation();
		keyHooksMock.useGenerateIntegrationApiKey.mockReturnValue(generateMutation);
		renderPage();
		await openGenerateDialog();

		fireEvent.click(screen.getByTestId("integration-key-generate-submit"));

		expect(screen.getByText("Select at least one trigger, or turn on Allow all triggers.")).toBeTruthy();
		expect(generateMutation.mutate).not.toHaveBeenCalled();
	});

	it("hides the allowlist and sends null once the wildcard switch is on", async () => {
		const generateMutation = makeMutation();
		keyHooksMock.useGenerateIntegrationApiKey.mockReturnValue(generateMutation);
		renderPage();
		await openGenerateDialog();

		fireEvent.click(screen.getByTestId("integration-key-generate-all-triggers"));

		expect(screen.queryByTestId("integration-key-generate-triggers")).toBeNull();

		fireEvent.click(screen.getByTestId("integration-key-generate-submit"));

		expect(generateMutation.mutate).toHaveBeenCalledWith(
			{ body: { label: "new-key", allowedTriggerIds: null } },
			{ onSuccess: expect.any(Function) },
		);
	});

	it("reveals the plaintext once, keeps it across a re-render, and drops it on dismiss and on remount", async () => {
		const generateMutation = makeMutation();
		keyHooksMock.useGenerateIntegrationApiKey.mockReturnValue(generateMutation);
		const view = renderPage();
		await openGenerateDialog();

		fireEvent.click(screen.getByTestId("integration-key-generate-all-triggers"));
		fireEvent.click(screen.getByTestId("integration-key-generate-submit"));

		const onSuccess = generateMutation.mutate.mock.calls[0]?.[1]?.onSuccess as (data: { key: string }) => void;
		onSuccess({ key: "xeint_plaintext_value" });

		await waitFor(() => {
			expect(screen.getAllByTestId("integration-key-reveal-value")).toHaveLength(1);
		});

		// A post-generate list refetch re-renders the page. The plaintext must survive it — the operator is still
		// reading it, and clearing here would destroy their only copy.
		keyHooksMock.useIntegrationKeys.mockReturnValue(makeQuery([...keys]));
		view.rerender(
			<MantineProvider>
				<ConfirmProvider>
					<QueryClientProvider client={new QueryClient()}>
						<IntegrationKeysPage />
					</QueryClientProvider>
				</ConfirmProvider>
			</MantineProvider>,
		);
		expect(screen.getByTestId("integration-key-reveal-value").textContent).toBe("xeint_plaintext_value");

		fireEvent.click(screen.getByTestId("integration-key-reveal-dismiss"));
		await waitFor(() => {
			expect(screen.queryByTestId("integration-key-reveal-value")).toBeNull();
		});
	});

	it("does not restore the plaintext after unmounting and remounting the page", async () => {
		const generateMutation = makeMutation();
		keyHooksMock.useGenerateIntegrationApiKey.mockReturnValue(generateMutation);
		const view = renderPage();
		await openGenerateDialog();

		fireEvent.click(screen.getByTestId("integration-key-generate-all-triggers"));
		fireEvent.click(screen.getByTestId("integration-key-generate-submit"));
		const onSuccess = generateMutation.mutate.mock.calls[0]?.[1]?.onSuccess as (data: { key: string }) => void;
		onSuccess({ key: "xeint_plaintext_value" });
		await waitFor(() => {
			expect(screen.getByTestId("integration-key-reveal-value")).toBeTruthy();
		});

		view.unmount();
		renderPage();

		expect(screen.queryByTestId("integration-key-reveal-value")).toBeNull();
	});

	it("clears the generate form after a key is issued", async () => {
		// The point of this case: the wide "all triggers" grant must be a deliberate switch for EVERY key, never a
		// leftover from the previous one.
		const generateMutation = makeMutation();
		keyHooksMock.useGenerateIntegrationApiKey.mockReturnValue(generateMutation);
		renderPage();
		await openGenerateDialog();

		fireEvent.click(screen.getByTestId("integration-key-generate-all-triggers"));
		fireEvent.click(screen.getByTestId("integration-key-generate-submit"));
		const onSuccess = generateMutation.mutate.mock.calls[0]?.[1]?.onSuccess as (data: { key: string }) => void;
		onSuccess({ key: "xeint_plaintext_value" });

		await waitFor(() => {
			expect(screen.queryByTestId("integration-key-generate-submit")).toBeNull();
		});

		fireEvent.click(screen.getByTestId("integration-key-generate-button"));
		await waitFor(() => {
			expect(screen.getByTestId("integration-key-generate-label")).toBeTruthy();
		});

		expect((screen.getByTestId("integration-key-generate-label") as HTMLInputElement).value).toBe("");
		expect((screen.getByTestId("integration-key-generate-all-triggers") as HTMLInputElement).checked).toBe(false);
		expect(screen.getByTestId("integration-key-generate-triggers")).toBeTruthy();
	});

	it("asks for confirmation before revoking and offers no action on a revoked row", async () => {
		confirmMock.mockResolvedValueOnce(false);
		renderPage();

		expect(screen.queryByTestId("integration-key-revoke-key-2")).toBeNull();

		fireEvent.click(screen.getByTestId("integration-key-revoke-key-1"));

		await waitFor(() => {
			expect(confirmMock).toHaveBeenCalledTimes(1);
		});
	});

	it("surfaces a load error", () => {
		keyHooksMock.useIntegrationKeys.mockReturnValue({ data: undefined, isLoading: false, error: new Error("boom") });

		renderPage();

		expect(screen.getByTestId("integration-keys-error")).toBeTruthy();
	});
});
