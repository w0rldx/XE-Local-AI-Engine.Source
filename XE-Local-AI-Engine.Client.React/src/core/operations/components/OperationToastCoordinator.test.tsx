// @vitest-environment jsdom

import { focusManager, QueryClient } from "@tanstack/react-query";
import { act, cleanup, render } from "@testing-library/react";
import { StrictMode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import {
	getNodeSettingsOptions,
	listBaseArtifactsOptions,
	listGraphWorkflowRunsOptions,
	listModelLabExperimentsOptions,
	listWorkSessionsOptions,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { alertedWaits } from "@/core/operations/models/JobAlerts";
import type { GgufAcquisitionStatus } from "@/core/model-downloads/models/GgufAcquisitionModels";
import { OperationToastCoordinator } from "@/core/operations/components/OperationToastCoordinator";
import { useJobNotificationStore } from "@/core/operations/stores/JobNotificationStore";
import type { RuntimeAcquisitionStatus } from "@/core/runtime-acquisition/queries/useRuntimeAcquisitionStatus";
import { toast } from "@/core/ui/notifications/Toast";
import { createProvidersWrapper, createTestQueryClient, renderWithProviders } from "@/test/RenderWithProviders";

// The two source feeds are hub-backed hooks with their own tests; here they are the values the coordinator watches.
// Everything between them and the toast (merge, normalization, transition rule) runs for real.
const sources = vi.hoisted(() => ({
	gguf: new Map<string, unknown>(),
	runtime: undefined as unknown,
	// The parked-run list's own read, for the one test that lets a push re-read it.
	graphRunsRead: vi.fn<() => Promise<unknown>>(),
}));

// Every hub lease is a connected fake; a test broadcasts to the handlers a path's sources registered.
const hubs = vi.hoisted(() => {
	const handlers = new Map<string, Set<(payload: unknown) => void>>();
	const acquire = (path: string) => ({
		connection: {
			state: "Connected",
			on: (name: string, handler: (payload: unknown) => void) => {
				const key = `${path} ${name}`;
				handlers.set(key, (handlers.get(key) ?? new Set()).add(handler));
			},
			off: (name: string, handler: (payload: unknown) => void) => handlers.get(`${path} ${name}`)?.delete(handler),
			invoke: async () => undefined,
		},
		whenStarted: Promise.resolve(),
		onReconnected: () => () => undefined,
		onReconnecting: () => () => undefined,
		onClosed: () => () => undefined,
		release: () => undefined,
	});
	const broadcast = (path: string, name: string, payload: unknown): number => {
		const registered = [...(handlers.get(`${path} ${name}`) ?? [])];
		for (const handler of registered) {
			handler(payload);
		}
		return registered.length;
	};
	return { acquire, broadcast };
});
vi.mock("@/core/api/signalr/SharedHubConnection", () => ({ acquireHubConnection: hubs.acquire }));

vi.mock("@/core/api/generated/@tanstack/react-query.gen", async (importOriginal) => {
	const actual = await importOriginal<typeof import("@/core/api/generated/@tanstack/react-query.gen")>();
	return {
		...actual,
		listGraphWorkflowRunsOptions: (options: Parameters<typeof actual.listGraphWorkflowRunsOptions>[0]) => ({
			...actual.listGraphWorkflowRunsOptions(options),
			queryFn: sources.graphRunsRead,
		}),
	};
});

vi.mock("@/core/model-downloads/queries/useGgufAcquisitions", () => ({
	useActiveGgufAcquisitions: () => sources.gguf,
}));
vi.mock("@/core/runtime-acquisition/hooks/useRuntimeAcquisitionHub", () => ({
	useRuntimeAcquisitionHub: () => sources.runtime,
}));
// The hub-backed job sources have their own tests; the broadcast-backed ones run for real on the lists a test seeds.
vi.mock("@/core/operations/hooks/useJobOperations", async (importOriginal) => {
	const actual = await importOriginal<typeof import("@/core/operations/hooks/useJobOperations")>();
	const none = () => [];
	return {
		...actual,
		useTrainingRunOperations: none,
		useDatasetBuildOperations: none,
		useImageJobOperations: none,
		useImageModelDownloadOperations: none,
		useTranscriptionModelDownloadOperations: none,
		useSchedulerRunOperations: none,
		useKnowledgeIndexingOperation: () => undefined,
	};
});

function download(phase: GgufAcquisitionStatus["phase"], overrides: Partial<GgufAcquisitionStatus> = {}) {
	sources.gguf = new Map([
		[
			"op-1",
			{
				operationId: "op-1",
				operationKind: "Download",
				modelName: "qwen3-8b",
				phase,
				pct: undefined,
				completedBytes: null,
				totalBytes: null,
				startedAtUtc: null,
				updatedAtUtc: null,
				errorCode: null,
				sanitizedMessage: null,
				...overrides,
			} satisfies GgufAcquisitionStatus,
		],
	]);
}

function runtimePhase(sequence: number, phase: string, sanitizedError?: string) {
	sources.runtime = {
		sequence,
		phase,
		variant: "Cpu",
		tag: "b9692",
		completedBytes: null,
		totalBytes: null,
		stepIndex: 1,
		stepCount: 1,
		sanitizedError,
	} satisfies RuntimeAcquisitionStatus;
}

let success: ReturnType<typeof vi.spyOn>;
let error: ReturnType<typeof vi.spyOn>;

beforeEach(() => {
	sources.gguf = new Map();
	sources.runtime = undefined;
	success = vi.spyOn(toast, "success").mockImplementation(() => undefined);
	error = vi.spyOn(toast, "error").mockImplementation(() => undefined);
});

afterEach(() => {
	cleanup();
	vi.restoreAllMocks();
});

// A source change re-renders the coordinator, exactly as a hub push or a re-hydration would.
function renderCoordinator(queryClient = createTestQueryClient()) {
	const { wrapper } = createProvidersWrapper({ queryClient });
	const result = render(<OperationToastCoordinator />, { wrapper });
	return { ...result, update: () => result.rerender(<OperationToastCoordinator />) };
}

describe("OperationToastCoordinator", () => {
	it("toasts success exactly once when a watched download completes, and not again on a replay", () => {
		download("Downloading");
		const { update } = renderCoordinator();

		download("Completed", { pct: 100 });
		update();
		// A reconnect re-hydration hands the same terminal row back as a new snapshot.
		download("Completed", { pct: 100 });
		update();

		expect(success).toHaveBeenCalledOnce();
		expect(success).toHaveBeenCalledWith("qwen3-8b is downloaded", { id: "operation:gguf-download:op-1:succeeded" });
		expect(error).not.toHaveBeenCalled();
	});

	it("toasts the failure exactly once, with the reason under the operation's title", () => {
		runtimePhase(1, "Downloading");
		const { update } = renderCoordinator();

		runtimePhase(2, "Failed", "Offline.");
		update();
		update();

		expect(error).toHaveBeenCalledOnce();
		expect(error).toHaveBeenCalledWith("Offline.", {
			id: "operation:runtime-acquisition:llamacpp:failed",
			title: "The local AI runtime could not be installed",
		});
	});

	it("stays silent for a terminal state already present at first hydration", () => {
		download("Completed");
		runtimePhase(4, "Failed");
		const { update } = renderCoordinator();
		update();

		expect(success).not.toHaveBeenCalled();
		expect(error).not.toHaveBeenCalled();
	});

	it("toasts once under StrictMode's doubled effects and stays silent after a remount", () => {
		const { wrapper } = createProvidersWrapper();
		const strict = render(<OperationToastCoordinator />, {
			wrapper: ({ children }) => <StrictMode>{wrapper({ children })}</StrictMode>,
		});
		download("Downloading");
		strict.rerender(<OperationToastCoordinator />);
		download("Completed");
		strict.rerender(<OperationToastCoordinator />);
		strict.unmount();

		renderWithProviders(<OperationToastCoordinator />);

		expect(success).toHaveBeenCalledOnce();
	});

	function runtimeStatusReads(invalidate: ReturnType<typeof vi.spyOn>) {
		return invalidate.mock.calls.filter(
			([filters]: unknown[]) => JSON.stringify(filters) === JSON.stringify({ queryKey: [{ _id: "getLlamaCppRuntime" }] }),
		).length;
	}

	it("re-reads the runtime status once and toasts once when the runtime acquisition completes", () => {
		const queryClient = createTestQueryClient();
		const invalidate = vi.spyOn(queryClient, "invalidateQueries");
		runtimePhase(1, "Extracting");
		const { update } = renderCoordinator(queryClient);

		runtimePhase(2, "Completed");
		update();

		expect(success).toHaveBeenCalledOnce();
		expect(success).toHaveBeenCalledWith("The local AI runtime is installed", {
			id: "operation:runtime-acquisition:llamacpp:succeeded",
		});
		expect(runtimeStatusReads(invalidate)).toBe(1);
	});

	it("re-reads the runtime status without a toast when the acquisition is first seen already completed", () => {
		const queryClient = createTestQueryClient();
		const invalidate = vi.spyOn(queryClient, "invalidateQueries");
		runtimePhase(3, "Completed");

		renderCoordinator(queryClient);

		expect(runtimeStatusReads(invalidate)).toBe(1);
		expect(success).not.toHaveBeenCalled();
	});

	describe("alerts for a user who is elsewhere", () => {
		let visibility: DocumentVisibilityState;
		const shown: { title: string; options?: NotificationOptions }[] = [];
		class FakeNotification {
			static permission: NotificationPermission = "granted";
			onclick: (() => void) | null = null;
			constructor(title: string, options?: NotificationOptions) {
				shown.push({ title, options });
			}
			close = vi.fn();
		}

		beforeEach(() => {
			visibility = "visible";
			vi.spyOn(document, "visibilityState", "get").mockImplementation(() => visibility);
			document.title = "XE Local AI Engine";
			shown.length = 0;
			FakeNotification.permission = "granted";
			vi.stubGlobal("Notification", FakeNotification);
			useJobNotificationStore.getState().actions.setSystemNotifications(false);
		});

		afterEach(() => {
			vi.unstubAllGlobals();
			window.history.replaceState(null, "", "/");
		});

		function finishDownload(): ReturnType<typeof renderCoordinator> {
			download("Downloading");
			const rendered = renderCoordinator();
			download("Completed");
			rendered.update();
			return rendered;
		}

		it("stays silent on the operation's own page while the tab is visible", () => {
			window.history.replaceState(null, "", "/models");

			finishDownload();

			expect(success).not.toHaveBeenCalled();
			expect(document.title).toBe("XE Local AI Engine");
		});

		it("toasts on another page without touching the title while the tab is visible", () => {
			finishDownload();

			expect(success).toHaveBeenCalledOnce();
			expect(document.title).toBe("XE Local AI Engine");
		});

		it("in a hidden tab toasts even on the own page, counts into the title and clears it once the tab is seen", () => {
			window.history.replaceState(null, "", "/models");
			visibility = "hidden";

			finishDownload();

			expect(success).toHaveBeenCalledOnce();
			expect(document.title).toBe("(1) XE Local AI Engine");

			visibility = "visible";
			document.dispatchEvent(new Event("visibilitychange"));
			expect(document.title).toBe("XE Local AI Engine");
		});

		it("shows no OS notification until the user turned them on", () => {
			visibility = "hidden";

			finishDownload();

			expect(shown).toEqual([]);
		});

		it("shows an OS notification for a hidden-tab finish once turned on and permitted", () => {
			useJobNotificationStore.getState().actions.setSystemNotifications(true);
			visibility = "hidden";

			finishDownload();

			expect(shown).toEqual([{ title: "qwen3-8b is downloaded", options: { body: undefined, tag: "gguf-download:op-1" } }]);
		});

		it("shows no OS notification when the browser denied the permission, or while the tab is visible", () => {
			useJobNotificationStore.getState().actions.setSystemNotifications(true);
			finishDownload();
			cleanup();
			FakeNotification.permission = "denied";
			visibility = "hidden";
			finishDownload();

			expect(shown).toEqual([]);
			expect(success).toHaveBeenCalledTimes(2);
		});
	});

	it("does not re-read the runtime status again while the acquisition stays completed", () => {
		const queryClient = createTestQueryClient();
		const invalidate = vi.spyOn(queryClient, "invalidateQueries");
		runtimePhase(3, "Completed");
		const { update } = renderCoordinator(queryClient);

		runtimePhase(3, "Completed");
		update();
		update();

		expect(runtimeStatusReads(invalidate)).toBe(1);
	});
});

describe("broadcast-backed sources", () => {
	const graphKey = listGraphWorkflowRunsOptions({ query: { status: "WaitingForApproval", limit: 200 } }).queryKey;
	const experimentsKey = listModelLabExperimentsOptions().queryKey;
	const artifactsKey = listBaseArtifactsOptions().queryKey;
	const waitingRun = (id: string) => ({ id, definitionId: "def-1", status: "WaitingForApproval" });
	let warning: ReturnType<typeof vi.spyOn>;

	beforeEach(() => {
		warning = vi.spyOn(toast, "warning").mockImplementation(() => undefined);
		globalThis.localStorage.clear();
		useNodeAuthStore.setState({ accessToken: "test-access-token" });
	});

	afterEach(() => {
		useNodeAuthStore.setState({ accessToken: undefined });
		globalThis.localStorage.clear();
		window.history.replaceState(null, "", "/");
	});

	// A re-read's answer: the query cache notifies its observers on the next tick, not inside the write.
	async function write(queryClient: QueryClient, queryKey: readonly unknown[], data: unknown): Promise<void> {
		await act(async () => {
			queryClient.setQueryData(queryKey, data);
			await new Promise<void>((resolve) => setTimeout(resolve, 0));
		});
	}

	// Every pushed list seeded and fresh, so the sources read the cache and nothing goes to the network.
	function seededClient(runs: unknown[] = []) {
		const queryClient = new QueryClient({
			defaultOptions: { queries: { retry: false, staleTime: Number.POSITIVE_INFINITY, gcTime: Number.POSITIVE_INFINITY } },
		});
		queryClient.setQueryData(getNodeSettingsOptions().queryKey, {
			graphWorkflowsEnabled: true,
			workSessionsEnabled: true,
		} as never);
		queryClient.setQueryData(listWorkSessionsOptions().queryKey, { items: [] } as never);
		queryClient.setQueryData(experimentsKey, { activeExperimentId: null, experiments: [] } as never);
		queryClient.setQueryData(artifactsKey, { items: [] } as never);
		queryClient.setQueryData(graphKey, { runs } as never);
		return queryClient;
	}

	const runs = (...ids: string[]) => ({ runs: ids.map(waitingRun) });

	it("alerts a parked graph run once: not again on a refetch, nor after a reload, nor once it is decided", async () => {
		const queryClient = seededClient();
		renderCoordinator(queryClient);
		expect(warning).not.toHaveBeenCalled();

		await write(queryClient, graphKey, runs("run-9"));
		// The next re-read answers the same run as a new response.
		await write(queryClient, graphKey, runs("run-9"));

		expect(warning).toHaveBeenCalledExactlyOnceWith("A workflow waits for your decision", {
			id: "operation:graph-run:run-9:waiting",
		});
		expect(success).not.toHaveBeenCalled();
		expect(error).not.toHaveBeenCalled();

		// A reload: a fresh page on the same browser storage whose first read still holds the parked run, then a new one.
		const reloaded = seededClient([waitingRun("run-9")]);
		renderCoordinator(reloaded);
		await act(async () => undefined);
		expect(warning).toHaveBeenCalledOnce();
		await write(reloaded, graphKey, runs("run-9", "run-10"));
		await write(reloaded, graphKey, runs());
		expect(warning).toHaveBeenCalledTimes(2);
		expect(warning).toHaveBeenLastCalledWith("A workflow waits for your decision", { id: "operation:graph-run:run-10:waiting" });
	});

	it("alerts the next wait of the same run or session once the first one ended", async () => {
		const queryClient = seededClient();
		const sessions = listWorkSessionsOptions().queryKey;
		const session = (status: string) => ({ items: [{ id: "s-1", title: "Docs", status }] });
		renderCoordinator(queryClient);

		await write(queryClient, graphKey, runs("run-9"));
		await write(queryClient, graphKey, runs());
		// A second Pause node parks the same run again.
		await write(queryClient, graphKey, runs("run-9"));
		await write(queryClient, sessions, session("WaitingForInput"));
		await write(queryClient, sessions, session("Running"));
		await write(queryClient, sessions, session("WaitingForApproval"));

		expect(warning.mock.calls.map((call: unknown[]) => (call[1] as { id: string }).id)).toEqual([
			"operation:graph-run:run-9:waiting",
			"operation:graph-run:run-9:waiting",
			"operation:work-session:s-1:waiting",
			"operation:work-session:s-1:waiting",
		]);
	});

	it("alerts a session that moves straight from one kind of wait to the other, also across a reload", async () => {
		const sessions = listWorkSessionsOptions().queryKey;
		const session = (status: string) => ({ items: [{ id: "s-1", title: "Docs", status }] });
		const queryClient = seededClient();
		renderCoordinator(queryClient);

		await write(queryClient, sessions, session("WaitingForInput"));
		await write(queryClient, sessions, session("WaitingForApproval"));
		expect(warning).toHaveBeenCalledTimes(2);
		expect(warning).toHaveBeenLastCalledWith("Work session Docs waits for your approval", {
			id: "operation:work-session:s-1:waiting",
		});

		// A reload on the same browser storage: the same wait stays quiet, the next kind of wait alerts.
		const reloaded = seededClient();
		reloaded.setQueryData(sessions, session("WaitingForApproval") as never);
		renderCoordinator(reloaded);
		await act(async () => undefined);
		expect(warning).toHaveBeenCalledTimes(2);
		await write(reloaded, sessions, session("WaitingForInput"));
		expect(warning).toHaveBeenCalledTimes(3);
		expect(warning).toHaveBeenLastCalledWith("Work session Docs waits for your input", {
			id: "operation:work-session:s-1:waiting",
		});
	});

	it.each([
		["the editor", "/graph-workflows", 1],
		["another run", "/graph-workflows?definitionId=def-1&runId=run-8", 1],
		["the waiting run itself", "/graph-workflows?definitionId=def-1&runId=run-9", 0],
	])("on %s, alerts a parked run only when it is not the run on screen", async (_page, url, alerts) => {
		window.history.replaceState(null, "", url);
		const queryClient = seededClient();
		renderCoordinator(queryClient);

		await write(queryClient, graphKey, runs("run-9"));

		expect(warning).toHaveBeenCalledTimes(alerts);
		// A wait the user is looking at is not claimed, so the next page load elsewhere still alerts it.
		expect(alertedWaits().includes("graph-run:run-9")).toBe(alerts === 1);
	});

	it("keeps every unchanged wait claimed across a reload, however many there are", async () => {
		const ids = Array.from({ length: 51 }, (_, index) => `run-${index}`);
		renderCoordinator(seededClient(ids.map(waitingRun)));
		await act(async () => undefined);
		expect(warning).toHaveBeenCalledTimes(51);

		renderCoordinator(seededClient(ids.map(waitingRun)));
		await act(async () => undefined);

		expect(warning).toHaveBeenCalledTimes(51);
	});

	it("keeps a claimed wait through StrictMode's remount and a later unmount and mount, and alerts it once", async () => {
		const queryClient = seededClient([waitingRun("run-9")]);
		const { wrapper } = createProvidersWrapper({ queryClient });
		const strict = render(
			<StrictMode>
				<OperationToastCoordinator />
			</StrictMode>,
			{ wrapper },
		);
		await act(async () => undefined);
		expect(alertedWaits()).toEqual(["graph-run:run-9"]);

		strict.unmount();
		expect(alertedWaits()).toEqual(["graph-run:run-9"]);
		renderCoordinator(queryClient);
		await act(async () => undefined);

		expect(warning).toHaveBeenCalledOnce();
	});

	it("does not take a fresh page's empty first snapshot as the end of a wait", async () => {
		renderCoordinator(seededClient([waitingRun("run-9")]));
		await act(async () => undefined);
		expect(warning).toHaveBeenCalledOnce();

		// A reload: the feed starts empty before its first read, and the read then answers the same parked run.
		const reloaded = seededClient();
		renderCoordinator(reloaded);
		await act(async () => undefined);
		await write(reloaded, graphKey, runs("run-9"));

		expect(warning).toHaveBeenCalledOnce();
		expect(alertedWaits()).toEqual(["graph-run:run-9"]);
	});

	describe("while the tab is hidden", () => {
		class FakeNotification {
			static readonly permission: NotificationPermission = "granted";
			static readonly shown: string[] = [];
			onclick: (() => void) | null = null;
			constructor(title: string) {
				FakeNotification.shown.push(title);
			}
			close = vi.fn();
		}

		beforeEach(() => {
			vi.useFakeTimers({ toFake: ["setInterval", "clearInterval"] });
			vi.spyOn(document, "visibilityState", "get").mockReturnValue("hidden");
			focusManager.setFocused(undefined);
			FakeNotification.shown.length = 0;
			vi.stubGlobal("Notification", FakeNotification);
			useJobNotificationStore.getState().actions.setSystemNotifications(true);
			document.title = "XE Local AI Engine";
		});

		afterEach(() => {
			vi.useRealTimers();
			vi.unstubAllGlobals();
			useJobNotificationStore.getState().actions.setSystemNotifications(false);
		});

		it("re-reads parked runs on the hub's push, with no timer, and marks the title and notifies on the answer", async () => {
			sources.graphRunsRead.mockResolvedValue(runs("run-9"));
			const queryClient = seededClient();
			renderCoordinator(queryClient);
			await act(async () => undefined);

			await act(async () => {
				expect(
					hubs.broadcast("graph-workflows/hub", "graphWorkflowRunStatusChanged", {
						runId: "run-9",
						status: "WaitingForApproval",
					}),
				).toBe(1);
			});
			await act(() => new Promise<void>((resolve) => setTimeout(resolve, 0)));

			expect(vi.getTimerCount()).toBe(0);
			expect(sources.graphRunsRead).toHaveBeenCalledOnce();
			expect(document.title).toBe("(1) XE Local AI Engine");
			expect(FakeNotification.shown).toEqual(["A workflow waits for your decision"]);
		});
	});

	it("alerts a work session waiting for input, read out of the session list", async () => {
		const queryClient = seededClient();
		renderCoordinator(queryClient);

		await write(queryClient, listWorkSessionsOptions().queryKey, {
			items: [{ id: "s-1", title: "Docs", status: "WaitingForInput" }],
		});

		expect(warning).toHaveBeenCalledExactlyOnceWith("Work session Docs waits for your input", {
			id: "operation:work-session:s-1:waiting",
		});
	});

	it("reports a Model Lab failure and a finished checkpoint download while no Model Lab page is open", async () => {
		const queryClient = seededClient();
		const experiment = { id: "e-1", installedModelName: "qwen3-0.6b", repoId: "Qwen/Qwen3-0.6B", errorMessage: null };
		const artifact = { id: "a-1", repoId: "Qwen/Qwen3-0.6B" };
		queryClient.setQueryData(experimentsKey, {
			activeExperimentId: "e-1",
			experiments: [{ ...experiment, status: "Running" }],
		} as never);
		queryClient.setQueryData(artifactsKey, { items: [{ ...artifact, status: "Downloading" }] } as never);
		renderCoordinator(queryClient);

		await write(queryClient, experimentsKey, {
			activeExperimentId: null,
			experiments: [{ ...experiment, status: "Failed", errorMessage: "The trace ran out of memory." }],
		});
		await write(queryClient, artifactsKey, { items: [{ ...artifact, status: "Ready" }] });

		expect(error).toHaveBeenCalledExactlyOnceWith("The trace ran out of memory.", {
			id: "operation:model-lab-experiment:e-1:failed",
			title: "The Model Lab experiment on qwen3-0.6b failed",
		});
		expect(success).toHaveBeenCalledExactlyOnceWith("The base checkpoint Qwen/Qwen3-0.6B is downloaded", {
			id: "operation:base-artifact-download:a-1:succeeded",
		});
	});
});
