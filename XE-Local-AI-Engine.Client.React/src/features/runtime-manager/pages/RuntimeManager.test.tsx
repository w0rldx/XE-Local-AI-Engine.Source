// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { XeLocalAiEngineClientEndpointsRuntimeManagerV1RuntimeManagerStatusResponse } from "@/core/api/generated";

const { generatedMock, logStreamMock } = vi.hoisted(() => ({
	generatedMock: {
		getRuntimeManagerStatusOptions: vi.fn(),
		getRuntimeManagerStatusQueryKey: vi.fn(() => ["getRuntimeManagerStatus"]),
		executeRuntimeContainerActionMutation: vi.fn(),
		statusFn: vi.fn(),
		actionFn: vi.fn(),
	},
	logStreamMock: {
		streamRuntimeLogs: vi.fn(),
	},
}));

vi.mock("@/core/api/generated/@tanstack/react-query.gen", () => ({
	getRuntimeManagerStatusOptions: generatedMock.getRuntimeManagerStatusOptions,
	getRuntimeManagerStatusQueryKey: generatedMock.getRuntimeManagerStatusQueryKey,
	executeRuntimeContainerActionMutation: generatedMock.executeRuntimeContainerActionMutation,
}));
vi.mock("@/features/runtime-manager/api/RuntimeLogStream", () => logStreamMock);

import { RuntimeManager } from "@/features/runtime-manager/pages/RuntimeManager";

function renderWithProviders(ui: ReactElement) {
	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
	return render(
		<MantineProvider>
			<QueryClientProvider client={queryClient}>{ui}</QueryClientProvider>
		</MantineProvider>,
	);
}

describe("RuntimeManager", () => {
	afterEach(() => {
		cleanup();
	});

	beforeEach(() => {
		vi.clearAllMocks();
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
		generatedMock.statusFn.mockResolvedValue(createSnapshot());
		generatedMock.getRuntimeManagerStatusOptions.mockReturnValue({
			queryKey: ["getRuntimeManagerStatus"],
			queryFn: generatedMock.statusFn,
		});
		generatedMock.actionFn.mockResolvedValue({
			containerName: "ollama",
			action: "restart",
			succeeded: true,
			startedAt: "2026-05-24T12:00:00Z",
			completedAt: "2026-05-24T12:00:02Z",
			components: [],
			diagnostics: ["restart:ok"],
		});
		generatedMock.executeRuntimeContainerActionMutation.mockReturnValue({ mutationFn: generatedMock.actionFn });
		logStreamMock.streamRuntimeLogs.mockImplementation(async function* () {
			yield {
				containerName: "ollama",
				stream: "stdout",
				line: "ready",
				observedAt: "2026-05-24T12:00:00Z",
			};
		});
	});

	it("renders status and capabilities", async () => {
		renderWithProviders(<RuntimeManager />);

		expect(await screen.findByRole("heading", { name: "Runtime manager", level: 2 })).toBeTruthy();
		expect((await screen.findAllByText("running")).length).toBeGreaterThan(0);
		expect(screen.getByText("Runtime lifecycle")).toBeTruthy();
		expect(screen.getByText("1.0 GB")).toBeTruthy();
	});

	it("renders component and manifest tabs", async () => {
		renderWithProviders(<RuntimeManager />);
		await screen.findByRole("heading", { name: "Runtime manager", level: 2 });

		fireEvent.click(await screen.findByRole("tab", { name: /Components/i }));
		expect(await screen.findByText("Runtime components")).toBeTruthy();
		expect(screen.getByText("ollama/ollama:0.11.10@sha256:test")).toBeTruthy();

		fireEvent.click(screen.getByRole("tab", { name: /Manifest/i }));
		expect(await screen.findByText("managed runtime · 1 container")).toBeTruthy();
		expect(screen.getByText("XE_HOST_AGENT_HMAC_SECRET_FILE: <redacted>")).toBeTruthy();
	});

	it("executes container actions and refreshes status", async () => {
		renderWithProviders(<RuntimeManager />);
		await screen.findByRole("heading", { name: "Runtime manager", level: 2 });

		fireEvent.click(await screen.findByRole("tab", { name: /Components/i }));
		fireEvent.click(await screen.findByRole("button", { name: "Restart" }));

		await waitFor(() =>
			expect(generatedMock.actionFn.mock.calls[0]?.[0]).toEqual({
				body: { containerName: "ollama", action: "restart", drainTimeoutSeconds: 30 },
			}),
		);
		expect(await screen.findByText("restart requested for ollama.")).toBeTruthy();
		await waitFor(() => expect(generatedMock.statusFn).toHaveBeenCalledTimes(2));
	});

	it("starts a cancellable log follow stream", async () => {
		renderWithProviders(<RuntimeManager />);
		await screen.findByRole("heading", { name: "Runtime manager", level: 2 });

		fireEvent.click(await screen.findByRole("tab", { name: /Logs/i }));
		fireEvent.click(await screen.findByRole("button", { name: "Follow logs" }));

		await waitFor(() =>
			expect(logStreamMock.streamRuntimeLogs).toHaveBeenCalledWith(
				{ containerName: "ollama", tailLines: 200, follow: true },
				expect.any(AbortSignal),
			),
		);
		expect(await screen.findByText(/ollama\/stdout: ready/i)).toBeTruthy();
	});
});

function createSnapshot(): XeLocalAiEngineClientEndpointsRuntimeManagerV1RuntimeManagerStatusResponse {
	return {
		status: {
			state: "running",
			desiredState: "running",
			runtimeLifecycle: "managed",
			bootstrapModelReady: true,
			webUiUrl: "http://127.0.0.1:8080",
			observedAt: "2026-05-24T12:00:00Z",
			components: [],
			diagnostics: ["ok"],
		},
		capabilities: {
			cpuAvailable: true,
			nvidiaGpuInference: false,
			gpuRuntimeConfigured: false,
			amdGpuStatus: "not-detected",
			runtimeDiskBytes: 1_073_741_824,
			observedAt: "2026-05-24T12:00:00Z",
			diagnostics: [],
		},
		components: [
			{
				name: "ollama",
				desiredState: "running",
				health: "healthy",
				imageReference: "ollama/ollama:0.11.10@sha256:test",
				digestVerified: true,
				observedAt: "2026-05-24T12:00:00Z",
				diagnostics: [],
			},
		],
		modelProviderHealth: {
			providerName: "ollama",
			isHealthy: true,
			observedAt: "2026-05-24T12:00:00Z",
			diagnostics: [],
		},
		models: [],
		manifest: {
			available: true,
			schemaVersion: 1,
			runtimeMode: "managed",
			bootstrapModel: "qwen3:0.6b",
			defaultChatModel: "qwen3:8b",
			maxRuntimeDiskGb: 128,
			stopDrainTimeoutSeconds: 30,
			containers: [
				{
					name: "xe-node-web-server",
					image: "ghcr.io/c0re/xe-local-ai-engine:0.1.0@sha256:test",
					network: "xe-engine-net",
					environment: [{ name: "XE_HOST_AGENT_HMAC_SECRET_FILE", value: "<redacted>" }],
					volumes: [{ source: "/etc/xe-host-agent/hmac-secret", target: "/etc/host-agent/hmac-secret", readOnly: true }],
				},
			],
			diagnostics: [],
		},
	};
}
