// @vitest-environment jsdom

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, cleanup, renderHook, waitFor } from "@testing-library/react";
import { t } from "i18next";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// One fake lease per hub path, recording what the hooks register, join and release.
const hubs = vi.hoisted(() => {
	interface FakeHub {
		readonly handlers: Map<string, (payload: unknown) => void>;
		readonly connection: {
			state: string;
			on: ReturnType<typeof vi.fn>;
			off: ReturnType<typeof vi.fn>;
			invoke: ReturnType<typeof vi.fn>;
		};
		readonly release: ReturnType<typeof vi.fn>;
		readonly reconnected: (() => void)[];
		readonly dropped: (() => void)[];
	}
	const byPath = new Map<string, FakeHub>();
	// Set by deferStart: the next lease is still connecting until the test resolves its start.
	let pendingStart: Promise<void> | undefined;
	const deferStart = (): (() => void) => {
		let resolve = (): void => undefined;
		pendingStart = new Promise<void>((done) => {
			resolve = done;
		});
		return () => resolve();
	};
	const acquire = vi.fn((path: string) => {
		const handlers = new Map<string, (payload: unknown) => void>();
		const whenStarted = pendingStart ?? Promise.resolve();
		const hub: FakeHub = {
			handlers,
			connection: {
				state: pendingStart ? "Connecting" : "Connected",
				on: vi.fn((name: string, handler: (payload: unknown) => void) => handlers.set(name, handler)),
				off: vi.fn((name: string) => handlers.delete(name)),
				invoke: vi.fn(async () => undefined),
			},
			release: vi.fn(),
			reconnected: [],
			dropped: [],
		};
		pendingStart = undefined;
		byPath.set(path, hub);
		const dropped = (callback: () => void) => {
			hub.dropped.push(callback);
			return () => undefined;
		};
		return {
			connection: hub.connection,
			whenStarted,
			onReconnected: (callback: () => void) => {
				hub.reconnected.push(callback);
				return () => undefined;
			},
			onReconnecting: dropped,
			onClosed: dropped,
			release: hub.release,
		};
	});
	return { acquire, byPath, deferStart };
});

vi.mock("@/core/api/signalr/SharedHubConnection", () => ({ acquireHubConnection: hubs.acquire }));

// A training-run detail read that nothing seeded stays pending until the test answers it, by run id.
const pendingDetails = vi.hoisted(() => new Map<string, (run: unknown) => void>());
// The broadcast-backed sources' reads, answered per test.
const pushed = vi.hoisted(() => ({
	graphRuns: vi.fn<() => Promise<unknown>>(),
	workSessions: vi.fn<() => Promise<unknown>>(),
	experiments: vi.fn<() => Promise<unknown>>(),
	experiment: vi.fn<() => Promise<unknown>>(),
	artifacts: vi.fn<() => Promise<unknown>>(),
}));
vi.mock("@/core/api/generated/@tanstack/react-query.gen", async (importOriginal) => {
	const actual = await importOriginal<typeof import("@/core/api/generated/@tanstack/react-query.gen")>();
	return {
		...actual,
		getTrainingRunOptions: (options: Parameters<typeof actual.getTrainingRunOptions>[0]) => ({
			...actual.getTrainingRunOptions(options),
			queryFn: () => new Promise((resolve) => pendingDetails.set(options.path.runId, resolve)),
		}),
		listGraphWorkflowRunsOptions: (options: Parameters<typeof actual.listGraphWorkflowRunsOptions>[0]) => ({
			...actual.listGraphWorkflowRunsOptions(options),
			queryFn: pushed.graphRuns,
		}),
		listWorkSessionsOptions: () => ({ ...actual.listWorkSessionsOptions(), queryFn: pushed.workSessions }),
		listModelLabExperimentsOptions: () => ({ ...actual.listModelLabExperimentsOptions(), queryFn: pushed.experiments }),
		getModelLabExperimentOptions: (options: Parameters<typeof actual.getModelLabExperimentOptions>[0]) => ({
			...actual.getModelLabExperimentOptions(options),
			queryFn: pushed.experiment,
		}),
		listBaseArtifactsOptions: () => ({ ...actual.listBaseArtifactsOptions(), queryFn: pushed.artifacts }),
	};
});

import {
	getTrainingRunOptions,
	listBaseArtifactsOptions,
	listGraphWorkflowRunsOptions,
	listImageJobsOptions,
	listTrainingDatasetsOptions,
	listModelLabExperimentsOptions,
	listTrainingRunsOptions,
	listWorkSessionsOptions,
} from "@/core/api/generated/@tanstack/react-query.gen";
import {
	type JobSourceOptions,
	useBaseArtifactDownloadOperations,
	useDatasetBuildOperations,
	useImageJobOperations,
	useKnowledgeIndexingOperation,
	useModelLabExperimentOperations,
	useSchedulerRunOperations,
	useTrainingRunOperations,
	useWaitingGraphRunOperations,
	useWaitingWorkSessionOperations,
} from "@/core/operations/hooks/useJobOperations";
import { type Operation, operationTransitions } from "@/core/operations/models/OperationModels";

// The feed reads active work only.
const trainingKey = listTrainingRunsOptions({ query: { page: 1, pageSize: 200, activeOnly: true } }).queryKey;
const imageKey = listImageJobsOptions({ query: { limit: 200, offset: 0, activeOnly: true } }).queryKey;
const datasetKey = listTrainingDatasetsOptions().queryKey;

let queryClient: QueryClient;

function wrapper({ children }: { children: ReactNode }) {
	return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
}

function hub(path: string) {
	const found = hubs.byPath.get(path);
	if (!found) {
		throw new Error(`no lease was taken on ${path}`);
	}
	return found;
}

function emit(path: string, name: string, payload: unknown): void {
	const handler = hub(path).handlers.get(name);
	if (!handler) {
		throw new Error(`nothing handles ${name} on ${path}`);
	}
	act(() => handler(payload));
}

function reconnect(path: string): void {
	act(() => {
		for (const callback of hub(path).reconnected) {
			callback();
		}
	});
}

function seedTrainingRuns(...statuses: [string, string][]): void {
	queryClient.setQueryData(trainingKey, {
		items: statuses.map(([id, status]) => ({ id, status })),
		totalCount: statuses.length,
		page: 1,
		pageSize: 200,
	} as never);
}

const on: JobSourceOptions = { enabled: true, watch: true, t };

beforeEach(() => {
	hubs.acquire.mockClear();
	hubs.byPath.clear();
	pendingDetails.clear();
	// The seeded lists are the snapshot; nothing in these tests may go to the network.
	queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: Number.POSITIVE_INFINITY } } });
});

afterEach(() => {
	cleanup();
});

describe("list sources", () => {
	it("leases no hub when the feature is off, or for a caller that only reads", () => {
		seedTrainingRuns(["run-1", "Training"]);

		const off = renderHook(() => useTrainingRunOperations({ ...on, enabled: false }), { wrapper });
		const reader = renderHook(() => useTrainingRunOperations({ ...on, watch: false }), { wrapper });

		expect(off.result.current).toEqual([]);
		expect(reader.result.current).toHaveLength(1);
		expect(hubs.acquire).not.toHaveBeenCalled();
	});

	it("holds the hub but joins no group while the list holds no running work", () => {
		seedTrainingRuns(["run-1", "Succeeded"], ["run-2", "Failed"]);

		renderHook(() => useTrainingRunOperations(on), { wrapper });

		expect(hubs.acquire).toHaveBeenCalledExactlyOnceWith("training/runs/hub");
		expect(hub("training/runs/hub").connection.invoke).not.toHaveBeenCalled();
	});

	it("re-reads the list once the hub first connects, so a run created before the connect is not missed", async () => {
		seedTrainingRuns();
		const start = hubs.deferStart();
		const invalidate = vi.spyOn(queryClient, "invalidateQueries").mockResolvedValue(undefined);
		const { result } = renderHook(() => useTrainingRunOperations(on), { wrapper });
		await act(async () => undefined);
		expect(invalidate).not.toHaveBeenCalled();

		// The run's job.created went out while the hub was still connecting, so no handler saw it.
		await act(async () => {
			hub("training/runs/hub").connection.state = "Connected";
			start();
		});
		expect(invalidate).toHaveBeenCalledExactlyOnceWith({ queryKey: trainingKey });
		act(() => seedTrainingRuns(["run-7", "Training"]));
		await waitFor(() => expect(result.current.map((operation) => operation.state)).toEqual(["running"]));
	});

	it("re-reads an empty list when another client creates a run, then follows it to its end", async () => {
		seedTrainingRuns();
		const { result } = renderHook(() => useTrainingRunOperations(on), { wrapper });
		await act(async () => undefined);
		const runs = hub("training/runs/hub");
		const invalidate = vi.spyOn(queryClient, "invalidateQueries").mockResolvedValue(undefined);

		emit("training/runs/hub", "job.created", { id: "run-9", kind: "TrainingRun" });
		expect(invalidate).toHaveBeenCalledExactlyOnceWith({ queryKey: trainingKey });

		// The re-read shows the new run: it joins the run's group, so the end reaches this tab too.
		act(() => seedTrainingRuns(["run-9", "Training"]));
		await waitFor(() => expect(runs.connection.invoke).toHaveBeenCalledWith("Subscribe", "run-9", Number.MAX_SAFE_INTEGER));
		expect(result.current.map((operation) => operation.state)).toEqual(["running"]);
		// The join re-reads once more, for an end between the re-read and the Subscribe.
		await waitFor(() => expect(invalidate).toHaveBeenCalledTimes(2));
		emit("training/runs/hub", "trainingRun.event", { runId: "run-9", kind: "State" });
		expect(invalidate).toHaveBeenCalledTimes(3);
		act(() => seedTrainingRuns(["run-9", "Failed"]));
		await waitFor(() => expect(result.current.map((operation) => operation.state)).toEqual(["failed"]));
	});

	it("joins each running run live-only, re-reads the list on its state event, and leaves its group once nothing runs", async () => {
		seedTrainingRuns(["run-1", "Training"], ["run-2", "Succeeded"]);
		const invalidate = vi.spyOn(queryClient, "invalidateQueries");

		const { result } = renderHook(() => useTrainingRunOperations(on), { wrapper });
		await act(async () => undefined);

		expect(hubs.acquire).toHaveBeenCalledExactlyOnceWith("training/runs/hub");
		const runs = hub("training/runs/hub");
		expect(runs.connection.invoke).toHaveBeenCalledExactlyOnceWith("Subscribe", "run-1", Number.MAX_SAFE_INTEGER);
		// The join's own re-read; only the events below are under test from here.
		expect(invalidate).toHaveBeenCalledExactlyOnceWith({ queryKey: trainingKey });
		invalidate.mockClear();

		emit("training/runs/hub", "trainingRun.event", { runId: "run-1", kind: "Progress" });
		emit("training/runs/hub", "trainingRun.event", { runId: "other-run", kind: "State" });
		expect(invalidate).not.toHaveBeenCalled();
		emit("training/runs/hub", "trainingRun.event", { runId: "run-1", kind: "State" });
		expect(invalidate).toHaveBeenCalledExactlyOnceWith({ queryKey: trainingKey });

		act(() => seedTrainingRuns(["run-1", "Succeeded"], ["run-2", "Succeeded"]));

		// The query cache notifies its observers on the next tick.
		await waitFor(() => expect(result.current.map((operation) => operation.state)).toEqual(["succeeded", "succeeded"]));
		expect(runs.connection.invoke).toHaveBeenLastCalledWith("Unsubscribe", "run-1");
		expect(runs.release).not.toHaveBeenCalled();
	});

	it("joins a run that starts later on the same lease, and re-joins and re-reads after a reconnect", async () => {
		seedTrainingRuns(["run-1", "Training"]);
		const invalidate = vi.spyOn(queryClient, "invalidateQueries");
		renderHook(() => useTrainingRunOperations(on), { wrapper });
		await act(async () => undefined);
		const runs = hub("training/runs/hub");

		act(() => seedTrainingRuns(["run-1", "Training"], ["run-2", "Queued"]));
		await waitFor(() => expect(runs.connection.invoke).toHaveBeenLastCalledWith("Subscribe", "run-2", Number.MAX_SAFE_INTEGER));
		expect(hubs.acquire).toHaveBeenCalledOnce();

		runs.connection.invoke.mockClear();
		reconnect("training/runs/hub");
		expect(runs.connection.invoke).toHaveBeenCalledTimes(2);
		expect(invalidate).toHaveBeenCalledWith({ queryKey: trainingKey });
	});

	it("follows a running run behind a deeper queue than any history page, and reads its end back by id", async () => {
		// Thirty newer queued runs would push it off a newest-first page of 25; the active-only read still holds it.
		const queued = Array.from({ length: 30 }, (_, index): [string, string] => [`run-q${index}`, "Queued"]);
		seedTrainingRuns(...queued, ["run-old", "Training"]);
		queryClient.setQueryData(getTrainingRunOptions({ path: { runId: "run-old" } }).queryKey, {
			id: "run-old",
			status: "Succeeded",
		} as never);
		const invalidate = vi.spyOn(queryClient, "invalidateQueries").mockResolvedValue(undefined);
		const { result } = renderHook(() => useTrainingRunOperations(on), { wrapper });
		await act(async () => undefined);
		const old = (): Operation | undefined => result.current.find((operation) => operation.id === "training-run:run-old");
		expect(old()?.state).toBe("running");
		expect(hub("training/runs/hub").connection.invoke).toHaveBeenCalledWith("Subscribe", "run-old", Number.MAX_SAFE_INTEGER);
		const before = new Map(result.current.map((operation) => [operation.id, operation.state]));
		// The join's own re-read, once for all 31 Subscribes.
		expect(invalidate).toHaveBeenCalledExactlyOnceWith({ queryKey: trainingKey });
		invalidate.mockClear();

		emit("training/runs/hub", "trainingRun.event", { runId: "run-old", kind: "State" });
		expect(invalidate).toHaveBeenCalledExactlyOnceWith({ queryKey: trainingKey });
		act(() => seedTrainingRuns(...queued));

		await waitFor(() => expect(old()?.state).toBe("succeeded"));
		expect(operationTransitions(before, result.current).finished.map((operation) => operation.id)).toEqual([
			"training-run:run-old",
		]);
	});

	it("keeps a run that left the active list running until its uncached detail arrives, so its end alerts once", async () => {
		seedTrainingRuns(["run-1", "Training"]);
		vi.spyOn(queryClient, "invalidateQueries").mockResolvedValue(undefined);
		const { result } = renderHook(() => useTrainingRunOperations(on), { wrapper });
		await act(async () => undefined);
		// The coordinator's bookkeeping, one step per snapshot it renders.
		let seen = operationTransitions(new Map(), result.current).seen;
		const finished: string[] = [];
		const observe = (): void => {
			const next = operationTransitions(seen, result.current);
			seen = next.seen;
			finished.push(...next.finished.map((operation) => operation.id));
		};
		const states = () => result.current.map((operation) => [operation.id, operation.state]);

		act(() => seedTrainingRuns());
		await waitFor(() => expect(pendingDetails.has("run-1")).toBe(true));
		expect(states()).toEqual([["training-run:run-1", "running"]]);
		observe();

		await act(async () => pendingDetails.get("run-1")?.({ id: "run-1", status: "Succeeded" }));
		await waitFor(() => expect(states()).toEqual([["training-run:run-1", "succeeded"]]));
		observe();
		expect(finished).toEqual(["training-run:run-1"]);
	});

	it("re-reads the list once a live-only Subscribe lands, so a run that ended before the join still alerts once", async () => {
		seedTrainingRuns(["run-1", "Training"]);
		const invalidate = vi.spyOn(queryClient, "invalidateQueries").mockResolvedValue(undefined);
		const { result } = renderHook(() => useTrainingRunOperations(on), { wrapper });
		// The Subscribe goes out on the next microtask; hold it open.
		let joined = (): void => undefined;
		hub("training/runs/hub").connection.invoke.mockImplementationOnce(
			() =>
				new Promise<void>((resolve) => {
					joined = resolve;
				}),
		);
		await act(async () => undefined);
		expect(hub("training/runs/hub").connection.invoke).toHaveBeenCalledWith("Subscribe", "run-1", Number.MAX_SAFE_INTEGER);
		let seen = operationTransitions(new Map(), result.current).seen;
		const finished: string[] = [];
		const observe = (): void => {
			const next = operationTransitions(seen, result.current);
			seen = next.seen;
			finished.push(...next.finished.map((operation) => operation.id));
		};

		// The run ends now, before the join: its State event reaches no group member, so nothing is pushed.
		await act(async () => joined());
		expect(invalidate).toHaveBeenCalledExactlyOnceWith({ queryKey: trainingKey });
		act(() => seedTrainingRuns());
		await waitFor(() => expect(pendingDetails.has("run-1")).toBe(true));
		observe();
		await act(async () => pendingDetails.get("run-1")?.({ id: "run-1", status: "Succeeded" }));
		await waitFor(() => expect(result.current.map((operation) => operation.state)).toEqual(["succeeded"]));
		observe();
		expect(finished).toEqual(["training-run:run-1"]);
	});

	it("re-reads the list once for a pass that joins three runs", async () => {
		seedTrainingRuns(["run-1", "Training"], ["run-2", "Queued"], ["run-3", "Preparing"]);
		const invalidate = vi.spyOn(queryClient, "invalidateQueries").mockResolvedValue(undefined);
		renderHook(() => useTrainingRunOperations(on), { wrapper });
		await act(async () => undefined);

		expect(hub("training/runs/hub").connection.invoke).toHaveBeenCalledTimes(3);
		expect(invalidate).toHaveBeenCalledExactlyOnceWith({ queryKey: trainingKey });
	});

	it("releases the lease on unmount", async () => {
		seedTrainingRuns(["run-1", "Training"]);
		const { unmount } = renderHook(() => useTrainingRunOperations(on), { wrapper });
		await act(async () => undefined);

		unmount();

		expect(hub("training/runs/hub").release).toHaveBeenCalledOnce();
		expect(hub("training/runs/hub").handlers.size).toBe(0);
	});

	it("reads dataset builds from the raw response under the generated key the datasets page shares", async () => {
		const raw = { items: [{ id: "ds-1", name: "Support", status: "Generating", workStatus: "Running" }] };
		queryClient.setQueryData(datasetKey, raw as never);

		const { result } = renderHook(() => useDatasetBuildOperations(on), { wrapper });

		expect(result.current).toEqual([expect.objectContaining({ id: "dataset-build:ds-1", state: "running" })]);
		// The source caches nothing of its own: the page's entry stays the raw response it maps through `select`.
		expect(queryClient.getQueryData(datasetKey)).toBe(raw);
		expect(queryClient.getQueryCache().findAll({ queryKey: [{ _id: "listTrainingDatasets" }] })).toHaveLength(1);
	});

	it("re-reads dataset builds on a replay reset and image jobs only on a terminal phase", async () => {
		queryClient.setQueryData(datasetKey, {
			items: [{ id: "ds-1", name: "Support", workStatus: "Running" }],
		} as never);
		queryClient.setQueryData(imageKey, { items: [{ id: "job-1", status: "Generating" }], totalCount: 1 } as never);
		const invalidate = vi.spyOn(queryClient, "invalidateQueries");

		renderHook(() => [useDatasetBuildOperations(on), useImageJobOperations(on)], { wrapper });
		await act(async () => undefined);

		expect(hub("training/datasets/hub").connection.invoke).toHaveBeenCalledWith("Subscribe", "ds-1", Number.MAX_SAFE_INTEGER);
		expect(hub("images/hub").connection.invoke).toHaveBeenCalledWith("Subscribe", "job-1");
		// Only the live-only dataset hub re-reads after its join; the images hub replays a job's events instead.
		expect(invalidate).toHaveBeenCalledExactlyOnceWith({ queryKey: datasetKey });
		invalidate.mockClear();
		emit("images/hub", "imageJob.statusChanged", { jobId: "job-1", phase: "Generating" });
		expect(invalidate).not.toHaveBeenCalled();
		emit("images/hub", "imageJob.statusChanged", { jobId: "job-1", phase: "Succeeded" });
		emit("training/datasets/hub", "datasetGeneration.replayReset", { datasetId: "ds-1" });
		expect(invalidate).toHaveBeenCalledWith({ queryKey: imageKey });
		expect(invalidate).toHaveBeenCalledWith({ queryKey: datasetKey });
	});
});

describe("list re-read while the hub is down", () => {
	const RUNS = "training/runs/hub";
	const POLL_MS = 15_000;

	// Only the interval is faked: the query cache still notifies on a real next tick.
	beforeEach(() => {
		vi.useFakeTimers({ toFake: ["setInterval", "clearInterval"] });
		vi.spyOn(queryClient, "invalidateQueries").mockResolvedValue(undefined);
	});

	afterEach(() => {
		vi.useRealTimers();
	});

	// The list query's own fetches, stubbed on the cached query so nothing reaches the network.
	function spyListFetch() {
		const query = queryClient.getQueryCache().find({ queryKey: trainingKey });
		if (!query) {
			throw new Error("seed the list first");
		}
		return vi.spyOn(query, "fetch").mockResolvedValue(undefined as never);
	}

	async function nextTick(): Promise<void> {
		await act(() => new Promise<void>((resolve) => setTimeout(resolve, 0)));
	}

	function advance(ms: number): void {
		act(() => vi.advanceTimersByTime(ms));
	}

	function connectAgain(): void {
		hub(RUNS).connection.state = "Connected";
		reconnect(RUNS);
	}

	it("re-reads a running job's list while the hub's first start has failed, and stops once it connects", async () => {
		seedTrainingRuns(["run-1", "Training"]);
		const fetch = spyListFetch();
		const start = hubs.deferStart();
		renderHook(() => useTrainingRunOperations(on), { wrapper });
		await act(async () => {
			hub(RUNS).connection.state = "Disconnected";
			start();
		});

		advance(POLL_MS);
		expect(fetch).toHaveBeenCalledOnce();

		connectAgain();
		advance(3 * POLL_MS);
		expect(fetch).toHaveBeenCalledOnce();
	});

	it.each(["Reconnecting", "Disconnected"])(
		"re-reads a running job's list while a dropped hub is %s, and stops on reconnect",
		async (state) => {
			seedTrainingRuns(["run-1", "Training"]);
			const fetch = spyListFetch();
			renderHook(() => useTrainingRunOperations(on), { wrapper });
			await act(async () => undefined);

			act(() => {
				hub(RUNS).connection.state = state;
				for (const callback of hub(RUNS).dropped) {
					callback();
				}
			});
			advance(POLL_MS);
			expect(fetch).toHaveBeenCalledOnce();

			connectAgain();
			advance(3 * POLL_MS);
			expect(fetch).toHaveBeenCalledOnce();
		},
	);

	it("re-reads a running job's list while the hub refuses its Subscribe, until the job ends", async () => {
		seedTrainingRuns();
		const fetch = spyListFetch();
		renderHook(() => useTrainingRunOperations(on), { wrapper });
		await act(async () => undefined);
		hub(RUNS).connection.invoke.mockRejectedValue(new Error("refused"));

		act(() => seedTrainingRuns(["run-1", "Training"]));
		await nextTick();
		expect(hub(RUNS).connection.invoke).toHaveBeenCalledWith("Subscribe", "run-1", Number.MAX_SAFE_INTEGER);
		// A refused join does not re-read; the poll below covers it.
		expect(queryClient.invalidateQueries).not.toHaveBeenCalled();
		advance(POLL_MS);
		expect(fetch).toHaveBeenCalledOnce();

		act(() => seedTrainingRuns(["run-1", "Succeeded"]));
		await nextTick();
		advance(3 * POLL_MS);
		expect(fetch).toHaveBeenCalledOnce();
	});

	it("does not re-read the list while the hub is connected", async () => {
		seedTrainingRuns(["run-1", "Training"]);
		const fetch = spyListFetch();
		renderHook(() => useTrainingRunOperations(on), { wrapper });
		await act(async () => undefined);

		advance(4 * POLL_MS);
		expect(fetch).not.toHaveBeenCalled();
	});

	it("does not re-read the list while the hub is down but nothing runs", async () => {
		seedTrainingRuns(["run-1", "Succeeded"]);
		const fetch = spyListFetch();
		const start = hubs.deferStart();
		renderHook(() => useTrainingRunOperations(on), { wrapper });
		await act(async () => {
			hub(RUNS).connection.state = "Disconnected";
			start();
		});

		advance(4 * POLL_MS);
		expect(fetch).not.toHaveBeenCalled();
	});
});

describe("cancelled dataset build", () => {
	it("re-reads the list on the cancel's State push, and the cancelled build leaves the feed without an alert row", async () => {
		queryClient.setQueryData(datasetKey, { items: [{ id: "ds-1", name: "Support", workStatus: "Running" }] } as never);
		const invalidate = vi.spyOn(queryClient, "invalidateQueries").mockResolvedValue(undefined);
		const { result } = renderHook(() => useDatasetBuildOperations(on), { wrapper });
		await act(async () => undefined);

		// The executor's push for a cancel names the work status; the list re-read then shows it.
		emit("training/datasets/hub", "datasetGeneration.event", {
			datasetId: "ds-1",
			kind: "State",
			payload: { state: "Cancelled" },
		});
		expect(invalidate).toHaveBeenCalledWith({ queryKey: datasetKey });

		act(() => {
			queryClient.setQueryData(datasetKey, { items: [{ id: "ds-1", name: "Support", workStatus: "Cancelled" }] } as never);
		});
		await waitFor(() => expect(result.current).toEqual([]));
	});
});

describe("push sources", () => {
	it("leases no hub for a disabled feature or a reading caller", () => {
		renderHook(
			() => [useKnowledgeIndexingOperation({ ...on, enabled: false }), useSchedulerRunOperations({ ...on, watch: false })],
			{
				wrapper,
			},
		);

		expect(hubs.acquire).not.toHaveBeenCalled();
	});

	it("folds knowledge-base pushes into one shared row that a reading caller sees too", async () => {
		const watcher = renderHook(() => useKnowledgeIndexingOperation(on), { wrapper });
		const reader = renderHook(() => useKnowledgeIndexingOperation({ ...on, watch: false }), { wrapper });

		emit("knowledge-base/hub", "knowledge.documentChanged", { documentId: "a", status: "Embedding" });
		await waitFor(() => expect(reader.result.current?.state).toBe("running"));
		emit("knowledge-base/hub", "knowledge.documentChanged", { documentId: "a", status: "Indexed" });
		await waitFor(() => expect(watcher.result.current?.state).toBe("succeeded"));
		expect(hubs.acquire).toHaveBeenCalledExactlyOnceWith("knowledge-base/hub");
	});

	it("settles each of two serially recovered documents, and a failed one, with the knowledge page closed", async () => {
		// Every admission, recovery and a rejected (queue-full) retry announce a row only through the status-free refresh,
		// which the feed ignores: a document joins a batch at its first real transition, so batches settle and never reopen.
		const { result } = renderHook(() => useKnowledgeIndexingOperation(on), { wrapper });
		const refreshLeavesBatchUnchanged = () => {
			const before = result.current;
			emit("knowledge-base/hub", "knowledge.documentsChanged", {});
			expect(result.current).toBe(before);
		};
		refreshLeavesBatchUnchanged();

		emit("knowledge-base/hub", "knowledge.documentChanged", { documentId: "a", status: "Extracting" });
		await waitFor(() => expect(result.current?.state).toBe("running"));
		emit("knowledge-base/hub", "knowledge.documentChanged", { documentId: "a", status: "Indexed" });
		await waitFor(() => expect(result.current?.state).toBe("succeeded"));
		refreshLeavesBatchUnchanged();

		emit("knowledge-base/hub", "knowledge.documentChanged", { documentId: "b", status: "Extracting" });
		await waitFor(() => expect(result.current?.detail).toBe("0 of 1 done"));
		refreshLeavesBatchUnchanged();
		emit("knowledge-base/hub", "knowledge.documentChanged", { documentId: "b", status: "Indexed" });
		await waitFor(() => expect(result.current?.state).toBe("succeeded"));

		emit("knowledge-base/hub", "knowledge.documentChanged", { documentId: "c", status: "Extracting" });
		await waitFor(() => expect(result.current?.detail).toBe("0 of 1 done"));
		emit("knowledge-base/hub", "knowledge.documentChanged", { documentId: "c", status: "Failed" });
		await waitFor(() => expect(result.current?.state).toBe("failed"));
		refreshLeavesBatchUnchanged();
		expect(result.current?.state).toBe("failed");
	});

	it("empties the scheduler runs after a reconnect gap and releases on unmount", async () => {
		const { result, unmount } = renderHook(() => useSchedulerRunOperations(on), { wrapper });
		const scheduler = hub("scheduler/hub");
		expect([...scheduler.handlers.keys()]).toContain("scheduler.jobDefinitionChanged");

		emit("scheduler/hub", "scheduler.runStarted", { runId: "r1" });
		await waitFor(() => expect(result.current).toHaveLength(1));
		reconnect("scheduler/hub");
		await waitFor(() => expect(result.current).toEqual([]));

		unmount();
		expect(scheduler.release).toHaveBeenCalledOnce();
		expect(scheduler.handlers.size).toBe(0);
	});

	it("does not bring back a running scheduler run after the watcher detached and reattached", async () => {
		const first = renderHook(() => useSchedulerRunOperations(on), { wrapper });
		emit("scheduler/hub", "scheduler.runStarted", { runId: "r1" });
		await waitFor(() => expect(first.result.current).toHaveLength(1));

		// The shell unmounts (e.g. on /command) and misses the run's end; a fresh lease on return fires no onReconnected.
		first.unmount();
		const second = renderHook(() => useSchedulerRunOperations(on), { wrapper });

		expect(second.result.current).toEqual([]);
	});
});

describe("broadcast-backed sources", () => {
	const MINUTE_MS = 60_000;
	const HUB_DOWN_MS = 15_000;
	const GRAPH = "graph-workflows/hub";
	const SESSIONS = "work-sessions/hub";
	const RUNS = "training/runs/hub";
	const graphKey = listGraphWorkflowRunsOptions({ query: { status: "WaitingForApproval", limit: 200 } }).queryKey;
	const waitingRun = { id: "run-9", definitionId: "def-1", status: "WaitingForApproval" };

	beforeEach(() => {
		for (const read of Object.values(pushed)) {
			read.mockReset();
		}
		vi.useFakeTimers({ toFake: ["setInterval", "clearInterval"] });
	});

	afterEach(() => {
		vi.useRealTimers();
	});

	async function settle(): Promise<void> {
		await act(() => new Promise<void>((resolve) => setTimeout(resolve, 0)));
	}

	async function advance(ms: number): Promise<void> {
		act(() => vi.advanceTimersByTime(ms));
		await settle();
	}

	// Ticks until the assertion holds: a read's answer reaches the hook a few ticks after it was asked.
	async function eventually(assertion: () => void, ticks = 20): Promise<void> {
		try {
			assertion();
		} catch (error) {
			if (ticks === 0) {
				throw error;
			}
			await settle();
			await eventually(assertion, ticks - 1);
		}
	}

	// What the list query's refetchInterval option resolves to right now, for its one observer.
	function intervalOf(queryKey: readonly unknown[]): unknown {
		const query = queryClient.getQueryCache().find({ queryKey });
		const interval = query?.observers[0]?.options.refetchInterval;
		return typeof interval === "function" && query ? interval(query) : interval;
	}

	function dropHub(path: string): void {
		act(() => {
			hub(path).connection.state = "Reconnecting";
			for (const callback of hub(path).dropped) {
				callback();
			}
		});
	}

	function connectAgain(path: string): void {
		hub(path).connection.state = "Connected";
		reconnect(path);
	}

	const states = (operations: readonly Operation[]) => operations.map(({ id, state }) => ({ id, state }));

	it("discovers the first parked graph run from an empty feed on the status broadcast, with no interval beside the hub", async () => {
		pushed.graphRuns.mockResolvedValueOnce({ runs: [] }).mockResolvedValue({ runs: [waitingRun] });
		const { result } = renderHook(() => useWaitingGraphRunOperations(on), { wrapper });
		await eventually(() => expect(pushed.graphRuns).toHaveBeenCalledOnce());
		await settle();

		await advance(3 * MINUTE_MS);
		expect(pushed.graphRuns).toHaveBeenCalledOnce();
		expect(intervalOf(graphKey)).toBe(false);

		emit(GRAPH, "graphWorkflowRunStatusChanged", { runId: "run-9", status: "WaitingForApproval" });

		await eventually(() => expect(states(result.current)).toEqual([{ id: "graph-run:run-9", state: "waiting" }]));
		expect(pushed.graphRuns).toHaveBeenCalledTimes(2);
	});

	it("re-reads the parked runs only for a move into a wait or out of one it holds", async () => {
		pushed.graphRuns.mockResolvedValueOnce({ runs: [waitingRun] }).mockResolvedValue({ runs: [] });
		const { result } = renderHook(() => useWaitingGraphRunOperations(on), { wrapper });
		await eventually(() => expect(result.current).toHaveLength(1));

		// Another run starting or finishing cannot change this list.
		emit(GRAPH, "graphWorkflowRunStatusChanged", { runId: "run-1", status: "Running" });
		await settle();
		expect(pushed.graphRuns).toHaveBeenCalledOnce();

		emit(GRAPH, "graphWorkflowRunStatusChanged", { runId: "run-9", status: "Running" });
		await eventually(() => expect(result.current).toEqual([]));
		expect(pushed.graphRuns).toHaveBeenCalledTimes(2);
	});

	it("leases no hub for a reading caller, and reads nothing while the feature is off", async () => {
		pushed.graphRuns.mockResolvedValue({ runs: [waitingRun] });
		const reader = renderHook(() => useWaitingGraphRunOperations({ ...on, watch: false }), { wrapper });
		const off = renderHook(() => useWaitingWorkSessionOperations({ ...on, enabled: false }), { wrapper });
		await eventually(() => expect(reader.result.current).toHaveLength(1));

		await advance(3 * MINUTE_MS);

		expect(hubs.acquire).not.toHaveBeenCalled();
		expect(pushed.graphRuns).toHaveBeenCalledOnce();
		expect(pushed.workSessions).not.toHaveBeenCalled();
		expect(off.result.current).toEqual([]);
	});

	it("filters waiting work sessions out of the list, and drops the row on the session's status broadcast", async () => {
		pushed.workSessions
			.mockResolvedValueOnce({
				items: [
					{ id: "s-1", title: "Docs", status: "WaitingForInput" },
					{ id: "s-2", title: "Refactor", status: "Running" },
				],
			})
			.mockResolvedValue({ items: [{ id: "s-1", title: "Docs", status: "Running" }] });
		const { result } = renderHook(() => useWaitingWorkSessionOperations(on), { wrapper });
		await eventually(() => expect(result.current.map((row) => row.id)).toEqual(["work-session:s-1"]));
		await advance(3 * MINUTE_MS);
		expect(pushed.workSessions).toHaveBeenCalledOnce();
		expect(intervalOf(listWorkSessionsOptions().queryKey)).toBe(false);

		emit(SESSIONS, "workSessionStatusChanged", { sessionId: "s-1" });

		await eventually(() => expect(result.current).toEqual([]));
	});

	it("follows a Model Lab experiment started elsewhere from its creation broadcast to its failure, under one id", async () => {
		const experiment = { id: "e-1", installedModelName: "qwen3-0.6b", repoId: "Qwen/Qwen3-0.6B", errorMessage: null };
		let list: unknown = { activeExperimentId: null, experiments: [] };
		pushed.experiments.mockImplementation(async () => list);
		const { result } = renderHook(() => useModelLabExperimentOperations(on), { wrapper });
		await eventually(() => expect(pushed.experiments).toHaveBeenCalledOnce());
		await settle();

		// A training run's creation on the shared hub is not this list's business.
		emit(RUNS, "job.created", { id: "run-1", kind: "TrainingRun" });
		await settle();
		expect(pushed.experiments).toHaveBeenCalledOnce();

		list = { activeExperimentId: "e-1", experiments: [{ ...experiment, status: "Running" }] };
		emit(RUNS, "job.created", { id: "e-1", kind: "ModelLabExperiment" });
		await eventually(() => expect(states(result.current)).toEqual([{ id: "model-lab-experiment:e-1", state: "running" }]));
		const running = result.current;
		await eventually(() => expect(hub(RUNS).connection.invoke).toHaveBeenCalledWith("Subscribe", "e-1", Number.MAX_SAFE_INTEGER));
		await advance(3 * MINUTE_MS);
		expect(intervalOf(listModelLabExperimentsOptions().queryKey)).toBe(false);

		list = {
			activeExperimentId: null,
			experiments: [{ ...experiment, status: "Failed", errorMessage: "The trace ran out of memory." }],
		};
		emit(RUNS, "trainingRun.event", { runId: "e-1", kind: "State" });
		await eventually(() => expect(result.current.map((row) => row.state)).toEqual(["failed"]));

		expect(pushed.experiment).not.toHaveBeenCalled();
		expect(operationTransitions(operationTransitions(new Map(), running).seen, result.current).finished).toEqual([
			expect.objectContaining({ id: "model-lab-experiment:e-1", state: "failed", detail: "The trace ran out of memory." }),
		]);
	});

	it("re-reads base checkpoints on their start and end broadcasts, once a minute only for a running download's bytes", async () => {
		let items: unknown[] = [];
		pushed.artifacts.mockImplementation(async () => ({ items }));
		const { result } = renderHook(() => useBaseArtifactDownloadOperations(on), { wrapper });
		await eventually(() => expect(pushed.artifacts).toHaveBeenCalledOnce());
		await settle();
		expect(intervalOf(listBaseArtifactsOptions().queryKey)).toBe(false);

		items = [{ id: "a-1", repoId: "Qwen/Qwen3-0.6B", status: "Downloading" }];
		emit(RUNS, "job.created", { id: "a-1", kind: "BaseArtifactDownload" });
		await eventually(() => expect(result.current.map((row) => row.state)).toEqual(["running"]));
		expect(intervalOf(listBaseArtifactsOptions().queryKey)).toBe(MINUTE_MS);
		await advance(MINUTE_MS);
		expect(pushed.artifacts).toHaveBeenCalledTimes(3);

		items = [{ id: "a-1", repoId: "Qwen/Qwen3-0.6B", status: "Ready" }];
		emit(RUNS, "job.ended", { id: "a-1", kind: "BaseArtifactDownload" });
		await eventually(() => expect(result.current.map((row) => row.state)).toEqual(["succeeded"]));
		await advance(3 * MINUTE_MS);

		expect(pushed.artifacts).toHaveBeenCalledTimes(4);
		expect(intervalOf(listBaseArtifactsOptions().queryKey)).toBe(false);
	});

	const sources = [
		["graph runs", GRAPH, pushed.graphRuns, useWaitingGraphRunOperations, { runs: [] }],
		["work sessions", SESSIONS, pushed.workSessions, useWaitingWorkSessionOperations, { items: [] }],
		[
			"Model Lab experiments",
			RUNS,
			pushed.experiments,
			useModelLabExperimentOperations,
			{ activeExperimentId: null, experiments: [] },
		],
		["base checkpoint downloads", RUNS, pushed.artifacts, useBaseArtifactDownloadOperations, { items: [] }],
	] as const;

	it.each(sources)(
		"still discovers %s once a minute while their hub is down, and stops on reconnect",
		async (_name, path, read, useSource, empty) => {
			read.mockResolvedValue(empty);
			renderHook(() => useSource(on), { wrapper });
			await eventually(() => expect(read).toHaveBeenCalledOnce());
			await settle();

			dropHub(path);
			await advance(MINUTE_MS - 1);
			expect(read).toHaveBeenCalledOnce();
			await advance(1);
			expect(read).toHaveBeenCalledTimes(2);

			// The reconnect re-reads once for the gap, then the interval stops.
			connectAgain(path);
			await settle();
			await advance(3 * MINUTE_MS);
			expect(read).toHaveBeenCalledTimes(3);
		},
	);

	it("follows a running experiment by id, not by the whole list, while the hub is down", async () => {
		const experiment = { id: "e-1", installedModelName: "qwen3-0.6b", repoId: "Qwen/Qwen3-0.6B", errorMessage: null };
		const running = { activeExperimentId: "e-1", experiments: [{ ...experiment, status: "Running" }] };
		pushed.experiments
			.mockResolvedValueOnce(running)
			.mockResolvedValueOnce(running)
			.mockResolvedValue({ activeExperimentId: null, experiments: [{ ...experiment, status: "Succeeded" }] });
		pushed.experiment
			.mockResolvedValueOnce({ ...experiment, status: "Running" })
			.mockResolvedValue({ ...experiment, status: "Succeeded" });
		const { result } = renderHook(() => useModelLabExperimentOperations(on), { wrapper });
		await eventually(() => expect(result.current.map((row) => row.state)).toEqual(["running"]));
		// The live-only join's re-read.
		await eventually(() => expect(pushed.experiments).toHaveBeenCalledTimes(2));
		pushed.experiments.mockClear();

		dropHub(RUNS);
		await eventually(() => expect(pushed.experiment).toHaveBeenCalledOnce());
		// The answer lands a tick after the read; only a Running answer arms the interval.
		await settle();
		await advance(HUB_DOWN_MS);

		await eventually(() => expect(pushed.experiment).toHaveBeenCalledTimes(2));
		await eventually(() => expect(pushed.experiments).toHaveBeenCalledOnce());
		await advance(2 * HUB_DOWN_MS);
		expect(pushed.experiment).toHaveBeenCalledTimes(2);
		expect(pushed.experiments).toHaveBeenCalledOnce();
		expect(result.current.map((row) => row.state)).toEqual(["succeeded"]);
	});

	it("re-reads a downloading checkpoint at the hub-down pace while the hub is down", async () => {
		pushed.artifacts.mockResolvedValue({ items: [{ id: "a-1", repoId: "Qwen/Qwen3-0.6B", status: "Downloading" }] });
		renderHook(() => useBaseArtifactDownloadOperations(on), { wrapper });
		await eventually(() => expect(pushed.artifacts).toHaveBeenCalledOnce());
		await settle();

		dropHub(RUNS);
		await advance(HUB_DOWN_MS);

		expect(pushed.artifacts).toHaveBeenCalledTimes(2);
		expect(intervalOf(listBaseArtifactsOptions().queryKey)).toBe(HUB_DOWN_MS);
	});

	it.each(
		sources.flatMap(([name, , read, useSource]) => [403, 404].map((statusCode) => [name, statusCode, read, useSource] as const)),
	)("stops the %s source quietly on a %i, and releases its hub", async (_name, statusCode, read, useSource) => {
		read.mockRejectedValue({ statusCode });
		const { result } = renderHook(() => useSource(on), { wrapper });
		await eventually(() => expect(queryClient.getQueryCache().getAll()[0]?.state.status).toBe("error"));

		await advance(3 * MINUTE_MS);

		expect(read).toHaveBeenCalledOnce();
		expect(result.current).toEqual([]);
		for (const call of hubs.acquire.mock.results) {
			expect((call.value as { release: ReturnType<typeof vi.fn> }).release).toHaveBeenCalled();
		}
	});
});
