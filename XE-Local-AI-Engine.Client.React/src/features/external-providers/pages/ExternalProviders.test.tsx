// @vitest-environment jsdom

import { QueryClient } from "@tanstack/react-query";
import { cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ApiError } from "@/core/api/errors/ApiError";
import type { ProblemDetails } from "@/core/api/models/ProblemDetails";
import { toast } from "@/core/ui/notifications/Toast";
import type { ExternalProviderConnectionDto } from "@/features/external-providers/models/ExternalProviderFormState";

// Mock generated query/mutation factories to isolate the hook while retaining validation and mapping.
const { generatedMock, confirmMock } = vi.hoisted(() => ({
	generatedMock: {
		listOptions: vi.fn(),
		listQueryKey: vi.fn(() => [{ _id: "listExternalProviderConnections" }]),
		saveMutation: vi.fn(),
		deleteMutation: vi.fn(),
		probeMutation: vi.fn(),
		listFn: vi.fn(),
		saveFn: vi.fn(),
		deleteFn: vi.fn(),
		probeFn: vi.fn(),
	},
	confirmMock: { confirm: vi.fn() },
}));

vi.mock("@/core/api/generated/@tanstack/react-query.gen", async (importOriginal) => {
	const actual = await importOriginal<typeof import("@/core/api/generated/@tanstack/react-query.gen")>();
	return {
		...actual,
		listExternalProviderConnectionsOptions: generatedMock.listOptions,
		listExternalProviderConnectionsQueryKey: generatedMock.listQueryKey,
		saveExternalProviderConnectionMutation: generatedMock.saveMutation,
		deleteExternalProviderConnectionMutation: generatedMock.deleteMutation,
		probeExternalProviderMutation: generatedMock.probeMutation,
	};
});

vi.mock("@/core/ui/hooks/useConfirm", () => ({ useConfirm: () => confirmMock }));

import { ExternalProviders } from "@/features/external-providers/pages/ExternalProviders";
import { renderWithProviders } from "@/test/RenderWithProviders";

function connection(overrides: Partial<ExternalProviderConnectionDto> = {}): ExternalProviderConnectionDto {
	return {
		id: "unsloth-box",
		displayName: "Unsloth box",
		baseUrl: "http://127.0.0.1:8080/v1",
		locality: "Local",
		cloudGrants: { localData: false, unattendedRuns: false, webTools: false, mcpTools: false, subAgents: false },
		hasApiKey: true,
		timeoutSeconds: 120,
		allowInsecureHttp: false,
		insecureTransport: false,
		models: [
			{
				wireId: "qwen3-27b",
				modelId: "ext:unsloth-box/qwen3-27b",
				displayName: "Qwen3 27B",
				contextLength: 32_768,
				supportsTools: true,
				supportsVision: false,
				supportsReasoning: false,
				supportsReasoningEffort: false,
				defaultReasoningEffort: null,
			},
		],
		...overrides,
	};
}

function renderPage(): void {
	// This file's own client rather than `createTestQueryClient()`: caching stays on (no `gcTime: 0`), which the
	// save/delete/probe mutations' invalidate-and-refetch assertions rely on.
	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
	renderWithProviders(<ExternalProviders />, { queryClient });
}

async function openStoredEditor(): Promise<void> {
	renderPage();
	await waitFor(() => expect(screen.getByTestId("external-provider-edit-unsloth-box")).toBeTruthy());
	fireEvent.click(screen.getByTestId("external-provider-edit-unsloth-box"));
	await waitFor(() => expect(screen.getByTestId("external-provider-editor")).toBeTruthy());
}

describe("ExternalProviders page", () => {
	beforeEach(() => {
		generatedMock.listFn.mockResolvedValue({ revision: "rev-1", connections: [connection()] });
		generatedMock.saveFn.mockResolvedValue({ revision: "rev-2", connections: [connection()] });
		generatedMock.deleteFn.mockResolvedValue({ revision: "rev-2", connections: [] });
		generatedMock.probeFn.mockResolvedValue({ reachable: true, models: [] });
		generatedMock.listOptions.mockReturnValue({
			queryKey: [{ _id: "listExternalProviderConnections" }],
			queryFn: generatedMock.listFn,
		});
		generatedMock.saveMutation.mockReturnValue({ mutationFn: generatedMock.saveFn });
		generatedMock.deleteMutation.mockReturnValue({ mutationFn: generatedMock.deleteFn });
		generatedMock.probeMutation.mockReturnValue({ mutationFn: generatedMock.probeFn });
		confirmMock.confirm.mockResolvedValue(true);
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it("lists the stored connections with their declared trust", async () => {
		renderPage();

		await waitFor(() => expect(screen.getByTestId("external-provider-connection-unsloth-box")).toBeTruthy());
		expect(screen.getByText("Unsloth box")).toBeTruthy();
		expect(screen.getAllByText("Declared local").length).toBeGreaterThan(0);
		expect(screen.getByText("Key stored")).toBeTruthy();
	});

	it("saves an untouched key by omitting apiKey, preserving the stored key", async () => {
		await openStoredEditor();

		fireEvent.click(screen.getByTestId("external-provider-save"));

		await waitFor(() => expect(generatedMock.saveFn).toHaveBeenCalledTimes(1));
		const [call] = generatedMock.saveFn.mock.calls[0] as [{ path: { connectionId: string }; body: Record<string, unknown> }];
		expect(call.path.connectionId).toBe("unsloth-box");
		expect("apiKey" in call.body).toBe(false);
		expect(call.body["clearApiKey"]).toBeUndefined();
		expect(call.body["expectedRevision"]).toBe("rev-1");
	});

	it("answers each capability Yes, No or Unknown, and sends Unknown as null", async () => {
		await openStoredEditor();

		const tools = within(screen.getByTestId("external-provider-model-tools-0"));
		expect((tools.getByLabelText("Yes") as HTMLInputElement).checked).toBe(true);
		// Reasoning is No, so the effort answer and the default effort cannot be set.
		const effort = within(screen.getByTestId("external-provider-model-effort-0"));
		expect((effort.getByLabelText("Yes") as HTMLInputElement).disabled).toBe(true);
		expect((screen.getByTestId("external-provider-model-default-effort-0") as HTMLInputElement).disabled).toBe(true);

		fireEvent.click(tools.getByLabelText("Unknown"));
		fireEvent.click(screen.getByTestId("external-provider-save"));

		await waitFor(() => expect(generatedMock.saveFn).toHaveBeenCalledTimes(1));
		const [call] = generatedMock.saveFn.mock.calls[0] as [{ body: { models: Record<string, unknown>[] } }];
		expect(call.body.models[0]).toMatchObject({ supportsTools: null, supportsVision: false, supportsReasoning: false });
	});

	it("sends clearApiKey only after the explicit Remove key action", async () => {
		await openStoredEditor();

		fireEvent.click(screen.getByTestId("external-provider-remove-key"));
		expect(screen.getByTestId("external-provider-key-removal")).toBeTruthy();
		fireEvent.click(screen.getByTestId("external-provider-save"));

		await waitFor(() => expect(generatedMock.saveFn).toHaveBeenCalledTimes(1));
		const [call] = generatedMock.saveFn.mock.calls[0] as [{ body: Record<string, unknown> }];
		expect(call.body["clearApiKey"]).toBe(true);
		expect("apiKey" in call.body).toBe(false);
	});

	it("sends a freshly typed key in place of the stored one", async () => {
		await openStoredEditor();

		fireEvent.change(screen.getByTestId("external-provider-api-key"), { target: { value: "sk-new" } });
		fireEvent.click(screen.getByTestId("external-provider-save"));

		await waitFor(() => expect(generatedMock.saveFn).toHaveBeenCalledTimes(1));
		const [call] = generatedMock.saveFn.mock.calls[0] as [{ body: Record<string, unknown> }];
		expect(call.body["apiKey"]).toBe("sk-new");
	});

	it("warns when Local is declared for a host that is not loopback or private, and not otherwise", async () => {
		await openStoredEditor();

		expect(screen.queryByTestId("external-provider-locality-warning")).toBeNull();

		fireEvent.change(screen.getByTestId("external-provider-base-url"), {
			target: { value: "https://api.example.com/v1" },
		});

		expect(screen.getByTestId("external-provider-locality-warning")).toBeTruthy();
	});

	it("shows the cloud permissions block only while Trust is Cloud, and a Local save sends no grants", async () => {
		await openStoredEditor();

		expect(screen.queryByTestId("external-provider-cloud-grants")).toBeNull();
		fireEvent.click(screen.getByLabelText("Cloud (hosted)"));
		expect(screen.getByText("Cloud permissions for this connection")).toBeTruthy();
		expect(screen.getByText("Let this connection's models use MCP tools")).toBeTruthy();
		expect(
			screen.getByText(
				"Applies to this connection only; the node-wide “Let cloud models use MCP tools” switch is not needed for it.",
			),
		).toBeTruthy();

		fireEvent.click(screen.getByTestId("external-provider-cloud-grant-mcpTools"));
		fireEvent.click(screen.getByLabelText("Local (self-hosted)"));
		expect(screen.queryByTestId("external-provider-cloud-grants")).toBeNull();
		fireEvent.click(screen.getByTestId("external-provider-save"));

		await waitFor(() => expect(generatedMock.saveFn).toHaveBeenCalledTimes(1));
		const [call] = generatedMock.saveFn.mock.calls[0] as [{ body: Record<string, unknown> }];
		expect(call.body["locality"]).toBe("Local");
		expect("cloudGrants" in call.body).toBe(false);
	});

	it("loads a Cloud connection's stored grants into the switches and saves the edited set", async () => {
		generatedMock.listFn.mockResolvedValue({
			revision: "rev-1",
			connections: [
				connection({
					locality: "Cloud",
					cloudGrants: { localData: false, unattendedRuns: false, webTools: true, mcpTools: false, subAgents: false },
				}),
			],
		});
		await openStoredEditor();

		expect((screen.getByTestId("external-provider-cloud-grant-webTools") as HTMLInputElement).checked).toBe(true);
		expect((screen.getByTestId("external-provider-cloud-grant-localData") as HTMLInputElement).checked).toBe(false);
		fireEvent.click(screen.getByTestId("external-provider-cloud-grant-localData"));
		fireEvent.click(screen.getByTestId("external-provider-cloud-grant-webTools"));
		fireEvent.click(screen.getByTestId("external-provider-save"));

		await waitFor(() => expect(generatedMock.saveFn).toHaveBeenCalledTimes(1));
		const [call] = generatedMock.saveFn.mock.calls[0] as [{ body: Record<string, unknown> }];
		expect(call.body["cloudGrants"]).toEqual({
			localData: true,
			unattendedRuns: false,
			webTools: false,
			mcpTools: false,
			subAgents: false,
		});
	});

	it("counts a connection's grants in a list badge, and shows none without a grant", async () => {
		generatedMock.listFn.mockResolvedValue({
			revision: "rev-1",
			connections: [
				connection({
					id: "gateway",
					locality: "Cloud",
					cloudGrants: { localData: false, unattendedRuns: true, webTools: true, mcpTools: false, subAgents: false },
				}),
				connection({ id: "plain-cloud", locality: "Cloud" }),
			],
		});
		renderPage();

		await waitFor(() => expect(screen.getByTestId("external-provider-grants-gateway")).toBeTruthy());
		expect(screen.getByTestId("external-provider-grants-gateway").textContent).toBe("2 grants");
		expect(screen.queryByTestId("external-provider-grants-plain-cloud")).toBeNull();
	});

	it("offers the insecure-HTTP opt-in only for an http address, and sends it on save and probe once ticked", async () => {
		generatedMock.listFn.mockResolvedValue({
			revision: "rev-1",
			connections: [connection({ baseUrl: "http://192.168.1.40:8080/v1/", hasApiKey: false, insecureTransport: true })],
		});
		await openStoredEditor();

		fireEvent.change(screen.getByTestId("external-provider-base-url"), { target: { value: "https://box.example.com/v1" } });
		expect(screen.queryByTestId("external-provider-allow-insecure-http")).toBeNull();
		fireEvent.change(screen.getByTestId("external-provider-base-url"), { target: { value: "http://192.168.1.40:8080/v1/" } });
		expect(screen.getByText("Allow insecure HTTP")).toBeTruthy();

		fireEvent.click(screen.getByTestId("external-provider-allow-insecure-http"));
		fireEvent.click(screen.getByTestId("external-provider-probe"));
		fireEvent.click(screen.getByTestId("external-provider-save"));

		await waitFor(() => expect(generatedMock.saveFn).toHaveBeenCalledTimes(1));
		const [save] = generatedMock.saveFn.mock.calls[0] as [{ body: Record<string, unknown> }];
		expect(save.body["allowInsecureHttp"]).toBe(true);
		await waitFor(() => expect(generatedMock.probeFn).toHaveBeenCalledTimes(1));
		const [probe] = generatedMock.probeFn.mock.calls[0] as [{ body: Record<string, unknown> }];
		expect(probe.body["allowInsecureHttp"]).toBe(true);
	});

	it("flags a plain-http remote row saved before HTTPS became the default, and not one that opted in", async () => {
		generatedMock.listFn.mockResolvedValue({
			revision: "rev-1",
			connections: [
				connection({ id: "legacy-lan", baseUrl: "http://192.168.1.40:8080/v1/", insecureTransport: true }),
				connection({
					id: "opted-in-lan",
					baseUrl: "http://192.168.1.41:8080/v1/",
					insecureTransport: true,
					allowInsecureHttp: true,
				}),
			],
		});
		renderPage();

		await waitFor(() => expect(screen.getByTestId("external-provider-insecure-legacy-lan")).toBeTruthy());
		expect(screen.getByText("Unencrypted HTTP")).toBeTruthy();
		expect(screen.queryByTestId("external-provider-insecure-opted-in-lan")).toBeNull();
	});

	it("renders the configuration a 409 returns instead of refetching, with a conflict notice", async () => {
		const conflictBody = { revision: "rev-9", connections: [connection({ displayName: "Renamed by someone else" })] };
		generatedMock.saveFn.mockRejectedValue(new ApiError(409, conflictBody as unknown as ProblemDetails));

		await openStoredEditor();
		const listCallsBefore = generatedMock.listFn.mock.calls.length;
		fireEvent.click(screen.getByTestId("external-provider-save"));

		await waitFor(() => expect(screen.getByTestId("external-provider-conflict")).toBeTruthy());
		expect(screen.getByText("Renamed by someone else")).toBeTruthy();
		// The stored state came from the conflict body itself — nothing was re-fetched to learn it.
		expect(generatedMock.listFn.mock.calls.length).toBe(listCallsBefore);
	});

	it("deletes with the expected revision after the operator confirms", async () => {
		await openStoredEditor();

		fireEvent.click(screen.getByTestId("external-provider-delete"));

		await waitFor(() => expect(generatedMock.deleteFn).toHaveBeenCalledTimes(1));
		const [call] = generatedMock.deleteFn.mock.calls[0] as [
			{ path: { connectionId: string }; query: { expectedRevision?: string } },
		];
		expect(call.path.connectionId).toBe("unsloth-box");
		expect(call.query.expectedRevision).toBe("rev-1");
	});

	it("does not delete when the operator cancels the confirmation", async () => {
		confirmMock.confirm.mockResolvedValue(false);
		await openStoredEditor();

		fireEvent.click(screen.getByTestId("external-provider-delete"));

		await waitFor(() => expect(confirmMock.confirm).toHaveBeenCalledTimes(1));
		expect(generatedMock.deleteFn).not.toHaveBeenCalled();
	});

	it("adds a probed model as a row, pre-filling the reported context length", async () => {
		generatedMock.probeFn.mockResolvedValue({ reachable: true, models: [{ id: "llama-3.1-8b", contextLength: 8192 }] });
		await openStoredEditor();

		fireEvent.click(screen.getByTestId("external-provider-probe"));
		await waitFor(() => expect(screen.getByTestId("external-provider-probe-add-llama-3.1-8b")).toBeTruthy());
		fireEvent.click(screen.getByTestId("external-provider-probe-add-llama-3.1-8b"));

		const wireIds = screen.getAllByLabelText(/Backing model id/i) as HTMLInputElement[];
		expect(wireIds.map((input) => input.value)).toContain("llama-3.1-8b");
		const contextLengths = screen.getAllByLabelText(/Context length/i) as HTMLInputElement[];
		expect(contextLengths.map((input) => input.value)).toContain("8192");
	});

	it("explains a reachable endpoint that lists no models rather than reporting a failure", async () => {
		generatedMock.probeFn.mockResolvedValue({ reachable: true, models: [], error: null });
		await openStoredEditor();

		fireEvent.click(screen.getByTestId("external-provider-probe"));

		await waitFor(() => expect(screen.getByTestId("external-provider-probe-no-models")).toBeTruthy());
		expect(screen.getByText("Reachable")).toBeTruthy();
	});

	// The connection id rides ALONGSIDE the draft address, never instead of it: the backend applies the stored key only
	// when the draft address is on the stored connection's own origin, and sending the id alone would probe the
	// endpoint the operator has just edited away from.
	it("probes the draft address, naming the stored connection so an unseen stored key can still apply", async () => {
		await openStoredEditor();

		fireEvent.click(screen.getByTestId("external-provider-probe"));

		await waitFor(() => expect(generatedMock.probeFn).toHaveBeenCalledTimes(1));
		const [call] = generatedMock.probeFn.mock.calls[0] as [{ body: Record<string, unknown> }];
		expect(call.body).toEqual({ connectionId: "unsloth-box", baseUrl: "http://127.0.0.1:8080/v1", headers: [] });
	});

	it("probes the EDITED address rather than the stored one", async () => {
		await openStoredEditor();

		fireEvent.change(screen.getByTestId("external-provider-base-url"), {
			target: { value: "https://elsewhere.example.com/v1" },
		});
		fireEvent.click(screen.getByTestId("external-provider-probe"));

		await waitFor(() => expect(generatedMock.probeFn).toHaveBeenCalledTimes(1));
		const [call] = generatedMock.probeFn.mock.calls[0] as [{ body: Record<string, unknown> }];
		expect(call.body["baseUrl"]).toBe("https://elsewhere.example.com/v1");
	});

	it("probes the draft address once a key is typed, so the test matches what is on screen", async () => {
		await openStoredEditor();

		fireEvent.change(screen.getByTestId("external-provider-api-key"), { target: { value: "sk-new" } });
		fireEvent.click(screen.getByTestId("external-provider-probe"));

		await waitFor(() => expect(generatedMock.probeFn).toHaveBeenCalledTimes(1));
		const [call] = generatedMock.probeFn.mock.calls[0] as [{ body: Record<string, unknown> }];
		expect(call.body).toEqual({
			connectionId: "unsloth-box",
			baseUrl: "http://127.0.0.1:8080/v1",
			apiKey: "sk-new",
			headers: [],
		});
	});

	it("drops a pending key removal from the probe, so the test is keyless like the save will be", async () => {
		await openStoredEditor();

		fireEvent.click(screen.getByTestId("external-provider-remove-key"));
		fireEvent.click(screen.getByTestId("external-provider-probe"));

		await waitFor(() => expect(generatedMock.probeFn).toHaveBeenCalledTimes(1));
		const [call] = generatedMock.probeFn.mock.calls[0] as [{ body: Record<string, unknown> }];
		expect(call.body).toEqual({ baseUrl: "http://127.0.0.1:8080/v1", headers: [] });
	});

	it("clears the probe result when the address changes, so one endpoint's models cannot be added to another", async () => {
		generatedMock.probeFn.mockResolvedValue({ reachable: true, models: [{ id: "llama-3.1-8b", contextLength: 8192 }] });
		await openStoredEditor();

		fireEvent.click(screen.getByTestId("external-provider-probe"));
		await waitFor(() => expect(screen.getByTestId("external-provider-probe-add-llama-3.1-8b")).toBeTruthy());

		fireEvent.change(screen.getByTestId("external-provider-base-url"), {
			target: { value: "https://elsewhere.example.com/v1" },
		});

		expect(screen.queryByTestId("external-provider-probe-result")).toBeNull();
		expect(screen.queryByTestId("external-provider-probe-add-llama-3.1-8b")).toBeNull();
	});

	it("blocks a save that moves a key-bearing connection to another origin, and says so on the key field", async () => {
		await openStoredEditor();

		fireEvent.change(screen.getByTestId("external-provider-base-url"), {
			target: { value: "https://elsewhere.example.com/v1" },
		});

		expect(screen.getByText(/stored key was issued for a different address/i)).toBeTruthy();
		expect((screen.getByTestId("external-provider-save") as HTMLButtonElement).disabled).toBe(true);

		// Typing the key for the new endpoint answers the question and re-enables the save.
		fireEvent.change(screen.getByTestId("external-provider-api-key"), { target: { value: "sk-new" } });

		expect((screen.getByTestId("external-provider-save") as HTMLButtonElement).disabled).toBe(false);
	});

	it("keeps saving a path-only change without re-entering the key — the endpoint is unchanged", async () => {
		await openStoredEditor();

		fireEvent.change(screen.getByTestId("external-provider-base-url"), {
			target: { value: "http://127.0.0.1:8080/openai/v1" },
		});

		expect((screen.getByTestId("external-provider-save") as HTMLButtonElement).disabled).toBe(false);
	});

	it("switches to editing the stored connection when a create conflicts on an id that already exists", async () => {
		const conflictBody = { revision: "rev-9", connections: [connection({ id: "gateway", displayName: "Taken already" })] };
		generatedMock.saveFn.mockRejectedValue(new ApiError(409, conflictBody as unknown as ProblemDetails));

		renderPage();
		await waitFor(() => expect(screen.getByTestId("external-provider-add-connection")).toBeTruthy());
		fireEvent.click(screen.getByTestId("external-provider-add-connection"));
		fireEvent.change(screen.getByTestId("external-provider-id"), { target: { value: "gateway" } });
		fireEvent.change(screen.getByTestId("external-provider-display-name"), { target: { value: "Gateway" } });
		fireEvent.change(screen.getByTestId("external-provider-base-url"), { target: { value: "https://gw.example.com/v1" } });
		fireEvent.click(screen.getByTestId("external-provider-save"));

		await waitFor(() => expect(screen.getByTestId("external-provider-conflict")).toBeTruthy());
		// The editor now points at the resource that exists: its id is fixed, Delete is available, and the fields show
		// what is stored rather than the draft that lost.
		expect((screen.getByTestId("external-provider-id") as HTMLInputElement).disabled).toBe(true);
		expect(screen.getByTestId("external-provider-delete")).toBeTruthy();
		expect((screen.getByTestId("external-provider-display-name") as HTMLInputElement).value).toBe("Taken already");
	});

	it("holds a blank new connection back from saving until it is valid", async () => {
		renderPage();
		await waitFor(() => expect(screen.getByTestId("external-provider-add-connection")).toBeTruthy());
		fireEvent.click(screen.getByTestId("external-provider-add-connection"));

		expect((screen.getByTestId("external-provider-save") as HTMLButtonElement).disabled).toBe(true);
		fireEvent.click(screen.getByTestId("external-provider-save"));
		expect(generatedMock.saveFn).not.toHaveBeenCalled();
	});

	it("shows a field's error only once the operator has been in that field", async () => {
		renderPage();
		await waitFor(() => expect(screen.getByTestId("external-provider-add-connection")).toBeTruthy());
		fireEvent.click(screen.getByTestId("external-provider-add-connection"));

		// A pristine form is not pre-reddened, even though it is invalid.
		expect(screen.queryByText("Enter an absolute http(s) base URL, e.g. http://127.0.0.1:8080/v1.")).toBeNull();

		fireEvent.blur(screen.getByTestId("external-provider-base-url"));

		expect(screen.getByText("Enter an absolute http(s) base URL, e.g. http://127.0.0.1:8080/v1.")).toBeTruthy();
		expect(screen.queryByText("Enter an id for this connection, e.g. unsloth-box.")).toBeNull();
	});

	it("enables the save once the required fields are filled in", async () => {
		renderPage();
		await waitFor(() => expect(screen.getByTestId("external-provider-add-connection")).toBeTruthy());
		fireEvent.click(screen.getByTestId("external-provider-add-connection"));

		fireEvent.change(screen.getByTestId("external-provider-id"), { target: { value: "gateway" } });
		fireEvent.change(screen.getByTestId("external-provider-display-name"), { target: { value: "Gateway" } });
		fireEvent.change(screen.getByTestId("external-provider-base-url"), { target: { value: "https://gw.example.com/v1" } });

		expect((screen.getByTestId("external-provider-save") as HTMLButtonElement).disabled).toBe(false);
		fireEvent.click(screen.getByTestId("external-provider-save"));

		await waitFor(() => expect(generatedMock.saveFn).toHaveBeenCalledTimes(1));
		const [call] = generatedMock.saveFn.mock.calls[0] as [{ path: { connectionId: string }; body: Record<string, unknown> }];
		expect(call.path.connectionId).toBe("gateway");
		// A new connection defaults to the restrictive half of the trust flag.
		expect(call.body["locality"]).toBe("Cloud");
	});

	describe("custom headers", () => {
		const gatewayHeaders = [
			{ name: "X-Example-Project", value: "demo", isSecret: false, hasStoredValue: true },
			{ name: "X-Example-Token", value: null, isSecret: true, hasStoredValue: true },
		];

		beforeEach(() => {
			generatedMock.listFn.mockResolvedValue({ revision: "rev-1", connections: [connection({ headers: gatewayHeaders })] });
		});

		it("labels the key field as a key or bearer token", async () => {
			await openStoredEditor();

			expect(screen.getByLabelText("API key or bearer token (optional)")).toBeTruthy();
		});

		it("saves the edited rows and keeps a stored secret value by omitting it", async () => {
			await openStoredEditor();

			expect((screen.getByTestId("external-provider-header-value-1") as HTMLInputElement).value).toBe("");
			fireEvent.change(screen.getByTestId("external-provider-header-value-0"), { target: { value: "demo-2" } });
			fireEvent.click(screen.getByTestId("external-provider-save"));

			await waitFor(() => expect(generatedMock.saveFn).toHaveBeenCalledTimes(1));
			const [call] = generatedMock.saveFn.mock.calls[0] as [{ body: Record<string, unknown> }];
			expect(call.body["headers"]).toEqual([
				{ name: "X-Example-Project", isSecret: false, value: "demo-2" },
				{ name: "X-Example-Token", isSecret: true },
			]);
		});

		it("sends the rows on screen with every probe", async () => {
			await openStoredEditor();

			fireEvent.click(screen.getByTestId("external-provider-probe"));

			await waitFor(() => expect(generatedMock.probeFn).toHaveBeenCalledTimes(1));
			const [call] = generatedMock.probeFn.mock.calls[0] as [{ body: Record<string, unknown> }];
			expect(call.body).toEqual({
				connectionId: "unsloth-box",
				baseUrl: "http://127.0.0.1:8080/v1",
				headers: [
					{ name: "X-Example-Project", isSecret: false, value: "demo" },
					{ name: "X-Example-Token", isSecret: true },
				],
			});
		});

		it("clears the probe result when a header value changes", async () => {
			generatedMock.probeFn.mockResolvedValue({ reachable: true, models: [{ id: "llama-3.1-8b", contextLength: 8192 }] });
			await openStoredEditor();

			fireEvent.click(screen.getByTestId("external-provider-probe"));
			await waitFor(() => expect(screen.getByTestId("external-provider-probe-result")).toBeTruthy());

			fireEvent.change(screen.getByTestId("external-provider-header-value-0"), { target: { value: "other" } });

			expect(screen.queryByTestId("external-provider-probe-result")).toBeNull();
		});

		it("asks for a fresh value when a plain stored row is turned secret and blanked", async () => {
			await openStoredEditor();

			fireEvent.click(screen.getByTestId("external-provider-header-secret-0"));
			fireEvent.change(screen.getByTestId("external-provider-header-value-0"), { target: { value: "" } });

			expect(screen.getByTestId("external-provider-header-error-0").textContent).toBe(
				'Enter a value for the secret header "X-Example-Project".',
			);
			expect((screen.getByTestId("external-provider-save") as HTMLButtonElement).disabled).toBe(true);
		});

		it("shows a rule violation under its row and holds the save back", async () => {
			await openStoredEditor();

			fireEvent.click(screen.getByTestId("external-provider-add-header"));
			fireEvent.change(screen.getByTestId("external-provider-header-name-2"), { target: { value: "Cookie" } });

			expect(screen.getByTestId("external-provider-header-error-2").textContent).toBe(
				'Header name "Cookie" is reserved and cannot be set.',
			);
			expect((screen.getByTestId("external-provider-save") as HTMLButtonElement).disabled).toBe(true);
		});

		it("asks for a stored secret value again when the address moves to another origin", async () => {
			await openStoredEditor();

			fireEvent.change(screen.getByTestId("external-provider-base-url"), {
				target: { value: "https://elsewhere.example.com/v1" },
			});
			fireEvent.change(screen.getByTestId("external-provider-api-key"), { target: { value: "sk-new" } });

			expect(screen.getByTestId("external-provider-header-error-1").textContent).toContain("X-Example-Token");
			expect((screen.getByTestId("external-provider-save") as HTMLButtonElement).disabled).toBe(true);

			fireEvent.change(screen.getByTestId("external-provider-header-value-1"), { target: { value: "token-2" } });

			expect(screen.queryByTestId("external-provider-header-error-1")).toBeNull();
			expect((screen.getByTestId("external-provider-save") as HTMLButtonElement).disabled).toBe(false);
		});

		it("shows the backend's message when it refuses the headers with a 400", async () => {
			const errorToast = vi.spyOn(toast, "error");
			const detail = "Secret custom header 'X-Example-Token' requires a value.";
			generatedMock.saveFn.mockRejectedValue(new ApiError(400, { detail } as ProblemDetails));
			await openStoredEditor();

			fireEvent.click(screen.getByTestId("external-provider-save"));

			await waitFor(() => expect(errorToast).toHaveBeenCalledWith(detail));
			errorToast.mockRestore();
		});
	});
});
