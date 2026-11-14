import { HubConnectionState } from "@microsoft/signalr";
import { skipToken, useQueries, useQuery, useQueryClient } from "@tanstack/react-query";
import type { TFunction } from "i18next";
import { useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";

import {
	getImageJobOptions,
	getTrainingRunOptions,
	listImageJobsOptions,
	listTrainingDatasetsOptions,
	listTrainingRunsOptions,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { acquireHubConnection } from "@/core/api/signalr/SharedHubConnection";
import { joinHubGroup } from "@/core/api/signalr/SharedHubGroups";
import {
	applyKnowledgeDocumentStatus,
	applyModelDownloadEvent,
	applySchedulerRunEvent,
	fromDatasetBuilds,
	fromImageJobs,
	fromKnowledgeIndexing,
	fromModelDownloads,
	fromSchedulerRuns,
	fromTrainingRuns,
	type KnowledgeIndexingBatch,
	type ModelDownloadEntry,
	type ModelDownloads,
	removeKnowledgeDocument,
	type SchedulerRunEntry,
	type SchedulerRuns,
} from "@/core/operations/models/JobOperationModels";
import type { Operation } from "@/core/operations/models/OperationModels";

/* eslint-disable react-doctor/effect-needs-cleanup -- Every effect offs its handlers and releases its shared lease; the rule only recognizes teardown of a connection created inline. */

// The job sources of the operations feed, on the hubs their pages already use, through the shared refcounted
// connection. Reading and watching are split: every consumer reads the shared cache, and only a `watch` caller (the
// shell's one toast coordinator) leases hubs, so a hub costs one lease and one handler however many views render it.
// Two shapes keep the idle cost low:
// - List sources (training runs, dataset builds, image jobs): a list GET is the snapshot (active runs and image jobs
//   only, the datasets list whole), and the hub is leased while the feature is on. Those hubs deliver a job's events to its group only, so each running id
//   is joined; a state event or replay reset re-reads the list, which carries the terminal state, and the one broadcast,
//   `job.created`, re-reads it for a job another tab or an API caller started.
// - Push sources (knowledge-base indexing, scheduler runs, image and Whisper model downloads): the hub broadcasts and
//   there is no cheap list to hydrate from, so it is leased while the feature is on and its events fold into a no-fetch
//   cache entry. A download already running at page load appears with its next progress push (at most a second).

function field(payload: unknown, key: string): string | undefined {
	const value = typeof payload === "object" && payload !== null ? (payload as Record<string, unknown>)[key] : undefined;
	return typeof value === "string" ? value : undefined;
}

interface ListSourceHub {
	readonly path: string;
	readonly subscribeArgs: (id: string) => readonly unknown[];
	/** Per client method: the job id when this payload may move the job's list row, else undefined. */
	readonly events: Readonly<Record<string, (payload: unknown) => string | undefined>>;
	/**
	 * Subscribe replays nothing, so an end between the list GET and the join reaches no handler: the list is re-read
	 * once after each pass of successful Subscribes. The images hub replays a job's buffered events instead.
	 */
	readonly liveOnly?: boolean;
}

// An afterSeq past any retained event: live events only, the list GET is the snapshot. A finished run's buffer is
// evicted, and the hub answers a replay reset for it, which re-reads the list like a state event.
const LIVE_ONLY = Number.MAX_SAFE_INTEGER;

const trainingRunHub: ListSourceHub = {
	path: "training/runs/hub",
	subscribeArgs: (id) => [id, LIVE_ONLY],
	liveOnly: true,
	events: {
		"trainingRun.event": (payload) => (field(payload, "kind") === "State" ? field(payload, "runId") : undefined),
		"trainingRun.replayReset": (payload) => field(payload, "runId"),
	},
};

const datasetBuildHub: ListSourceHub = {
	path: "training/datasets/hub",
	subscribeArgs: (id) => [id, LIVE_ONLY],
	liveOnly: true,
	events: {
		"datasetGeneration.event": (payload) => (field(payload, "kind") === "State" ? field(payload, "datasetId") : undefined),
		"datasetGeneration.replayReset": (payload) => field(payload, "datasetId"),
	},
};

const TERMINAL_IMAGE_PHASES: ReadonlySet<string | undefined> = new Set(["Succeeded", "Failed", "Cancelled"]);

const imageJobHub: ListSourceHub = {
	path: "images/hub",
	subscribeArgs: (id) => [id],
	events: {
		"imageJob.statusChanged": (payload) =>
			TERMINAL_IMAGE_PHASES.has(field(payload, "phase")) ? field(payload, "jobId") : undefined,
	},
};

// Broadcast on each list source's hub when a job of its kind is created anywhere.
const JOB_CREATED = "job.created";

// hub degraded: while a list source's hub is not connected or refused a Subscribe, no push announces a running job's
// end, so its list is re-read at this pace until the hub is back (whose reconnect re-reads it once) or nothing runs.
const HUB_DOWN_POLL_MS = 15_000;

function hubDownPoll(hubDown: boolean, operations: readonly Operation[]): number | false {
	return hubDown && operations.some((operation) => operation.state === "running") ? HUB_DOWN_POLL_MS : false;
}

/**
 * Leases `hub` while `watching` and holds each running id's group through `joinHubGroup`, which
 * re-subscribes after a reconnect and leaves a group only when its last holder on the shared connection does, so a page
 * watching the same id and this feed never drop each other's membership. `onChange` runs for an event of a held id, for
 * `job.created`, and after a reconnect, whose gap may hide a finish or a creation. `onHubDown` reports whether the lease
 * is not connected (a failed start, reconnecting, closed until the manager's restart) or a held id's Subscribe failed.
 */
function useListSourceHub(
	hub: ListSourceHub,
	watching: boolean,
	runningIds: readonly string[],
	onHubDown: (down: boolean) => void,
	onChange: () => void,
): void {
	const key = runningIds.toSorted().join(",");
	const wanted = useRef<ReadonlySet<string>>(new Set());
	const join = useRef<() => void>(() => undefined);
	const onChangeRef = useRef(onChange);
	useLayoutEffect(() => {
		onChangeRef.current = onChange;
	});

	useEffect(() => {
		wanted.current = new Set(key === "" ? [] : key.split(","));
		join.current();
	}, [key]);

	useEffect(() => {
		if (!watching) {
			return undefined;
		}
		const lease = acquireHubConnection(hub.path);
		const { connection } = lease;
		// The held id groups, each with its leave function.
		const joined = new Map<string, () => void>();
		// Held ids whose Subscribe failed: their end reaches no handler here.
		const refused = new Set<string>();
		let live = true;
		const syncDown = (): void => {
			if (live) {
				onHubDown(connection.state !== HubConnectionState.Connected || refused.size > 0);
			}
		};
		const settle = (id: string, ok: boolean): boolean => {
			if (ok) {
				refused.delete(id);
			} else {
				refused.add(id);
			}
			syncDown();
			return ok;
		};
		// The Subscribes sent in one tick (one join pass, or one reconnect's re-joins), so a live-only hub re-reads once.
		let pass: Promise<boolean>[] = [];
		const subscribe = (id: string): void => {
			if (connection.state !== HubConnectionState.Connected) {
				return;
			}
			const joining = connection.invoke("Subscribe", ...hub.subscribeArgs(id)).then(
				() => settle(id, true),
				() => settle(id, false),
			);
			if (!hub.liveOnly) {
				return;
			}
			if (pass.length === 0) {
				queueMicrotask(() => {
					// A refused Subscribe is left to the hub-down poll.
					Promise.all(pass)
						.then((results) => live && results.includes(true) && onChangeRef.current())
						.catch(() => undefined);
					pass = [];
				});
			}
			pass.push(joining);
		};
		const joinWanted = (): void => {
			for (const id of wanted.current) {
				if (!joined.has(id)) {
					joined.set(
						id,
						joinHubGroup(lease, ["Unsubscribe", id], () => subscribe(id)),
					);
				}
			}
			for (const [id, leave] of [...joined]) {
				if (!wanted.current.has(id)) {
					joined.delete(id);
					leave();
					settle(id, true);
				}
			}
		};
		const handlers = Object.entries(hub.events).map(([name, readId]) => {
			const handler = (payload: unknown): void => {
				const id = readId(payload);
				if (id !== undefined && joined.has(id)) {
					onChangeRef.current();
				}
			};
			return [name, handler] as const;
		});
		const created = (): void => onChangeRef.current();
		handlers.push([JOB_CREATED, created]);
		for (const [name, handler] of handlers) {
			connection.on(name, handler);
		}
		join.current = joinWanted;
		joinWanted();
		// Registered after the groups, so a reconnect re-subscribes them first; the gap may still have hidden a finish.
		// release() drops these registrations.
		lease.onReconnected(() => {
			syncDown();
			onChangeRef.current();
		});
		lease.onReconnecting(syncDown);
		lease.onClosed(syncDown);
		// A job created between the list GET and the first connect was announced to no handler here: re-read once
		// connected. A connection already up when the list was read leaves no such gap.
		const reread = connection.state !== HubConnectionState.Connected;
		lease.whenStarted
			.then(() => {
				syncDown();
				if (reread && live) {
					onChangeRef.current();
				}
			})
			.catch(() => undefined);
		return () => {
			live = false;
			onHubDown(false);
			join.current = () => undefined;
			for (const [name, handler] of handlers) {
				connection.off(name, handler);
			}
			for (const leave of joined.values()) {
				leave();
			}
			joined.clear();
			lease.release();
		};
	}, [watching, hub, onHubDown]);
}

// Operation ids carry the kind as a prefix; the hub groups want the job id alone.
function runningIds(operations: readonly Operation[]): string[] {
	return operations
		.filter((operation) => operation.state === "running")
		.map((operation) => operation.id.slice(operation.id.indexOf(":") + 1));
}

export interface JobSourceOptions {
	/** Signed in and the feature compiled in: the list GET may run. */
	readonly enabled: boolean;
	/** This caller owns the hub side (one per app). */
	readonly watch: boolean;
	readonly t: TFunction;
}

// The training-run and image-job sources read active work only, so a queue deeper than a history page cannot push a
// running job off it. shortcut: at most the endpoints' 200-row page per source; a deeper queue would need paging.
const ACTIVE_ROWS = 200;
// Ended jobs read back by id, kept per source; an older one has long since been seen terminal.
const ENDED_KEPT = 20;

/**
 * The last active-list row of each job that left that list since this hook saw it there, newest last. The active-only
 * list no longer says how such a job ended, so its source reads each once by id, and that terminal row is what the
 * toast coordinator sees.
 */
function useEndedRows<T extends { readonly id: string }>(active: readonly T[]): readonly T[] {
	const key = active
		.map((row) => row.id)
		.toSorted()
		.join(",");
	const [previous, setPrevious] = useState({ key, rows: active });
	const [ended, setEnded] = useState<readonly T[]>([]);
	if (previous.key !== key) {
		setPrevious({ key, rows: active });
		const left = (row: T): boolean => !active.some((current) => current.id === row.id);
		const gone = previous.rows.filter(left);
		if (gone.length > 0) {
			setEnded([...ended.filter((row) => !gone.some((done) => done.id === row.id)), ...gone].slice(-ENDED_KEPT));
		}
	}
	return ended;
}

// The by-id detail once it arrived; until then the last active row, still running, so the coordinator keeps the job
// between its last running sight and its end. A failed read drops the row: no alert, but no row stuck on "running".
function detailOrLastSeen<T>(ended: readonly T[]) {
	return (results: readonly { readonly data?: T; readonly isError: boolean }[]): T[] =>
		results.flatMap((result, index) => {
			const lastSeen = ended[index];
			return result.data !== undefined ? [result.data] : result.isError || lastSeen === undefined ? [] : [lastSeen];
		});
}

/** Training runs: the active ones, plus each one read back by id once it left that list. */
export function useTrainingRunOperations({ enabled, watch, t }: JobSourceOptions): readonly Operation[] {
	const queryClient = useQueryClient();
	const [hubDown, setHubDown] = useState(false);
	const options = listTrainingRunsOptions({ query: { page: 1, pageSize: ACTIVE_ROWS, activeOnly: true } });
	const { data } = useQuery({
		...options,
		enabled,
		refetchInterval: (query) => hubDownPoll(hubDown, fromTrainingRuns(query.state.data?.items ?? [], t)),
	});
	const ended = useEndedRows(enabled ? (data?.items ?? []) : []);
	const endedRuns = useQueries({
		queries: ended.map((run) => ({ ...getTrainingRunOptions({ path: { runId: run.id } }), enabled })),
		combine: useMemo(() => detailOrLastSeen(ended), [ended]),
	});
	const operations = useMemo(
		() => (enabled ? fromTrainingRuns([...endedRuns, ...(data?.items ?? [])], t) : []),
		[enabled, data, endedRuns, t],
	);
	useListSourceHub(trainingRunHub, enabled && watch, runningIds(operations), setHubDown, () => {
		queryClient.invalidateQueries({ queryKey: options.queryKey }).catch(() => undefined);
	});
	return operations;
}

// The datasets page caches its own mapped list under the generated key, so the raw response lives under a child key:
// a separate cache entry that the page's invalidation of the generated key still reaches by partial match.
const datasetBuildOptions = () => {
	const options = listTrainingDatasetsOptions();
	// The extra field also reaches the request options through the generated queryFn, which ignores it as it does `_id`.
	const queryKey = [{ ...options.queryKey[0], scope: "operations" }] as unknown as typeof options.queryKey;
	return { ...options, queryKey };
};

export function useDatasetBuildOperations({ enabled, watch, t }: JobSourceOptions): readonly Operation[] {
	const queryClient = useQueryClient();
	const [hubDown, setHubDown] = useState(false);
	const options = datasetBuildOptions();
	const { data } = useQuery({
		...options,
		enabled,
		refetchInterval: (query) => hubDownPoll(hubDown, fromDatasetBuilds(query.state.data?.items ?? [], t)),
	});
	const operations = useMemo(() => (enabled ? fromDatasetBuilds(data?.items ?? [], t) : []), [enabled, data, t]);
	useListSourceHub(datasetBuildHub, enabled && watch, runningIds(operations), setHubDown, () => {
		queryClient.invalidateQueries({ queryKey: options.queryKey }).catch(() => undefined);
	});
	return operations;
}

/** Image jobs: the queued and generating ones, plus each one read back by id once it left that list. */
export function useImageJobOperations({ enabled, watch, t }: JobSourceOptions): readonly Operation[] {
	const queryClient = useQueryClient();
	const [hubDown, setHubDown] = useState(false);
	const options = listImageJobsOptions({ query: { limit: ACTIVE_ROWS, offset: 0, activeOnly: true } });
	const { data } = useQuery({
		...options,
		enabled,
		refetchInterval: (query) => hubDownPoll(hubDown, fromImageJobs(query.state.data?.items ?? [], t)),
	});
	const ended = useEndedRows(enabled ? (data?.items ?? []) : []);
	const endedJobs = useQueries({
		queries: ended.map((job) => ({ ...getImageJobOptions({ path: { jobId: job.id } }), enabled })),
		combine: useMemo(() => detailOrLastSeen(ended), [ended]),
	});
	const operations = useMemo(
		() => (enabled ? fromImageJobs([...endedJobs, ...(data?.items ?? [])], t) : []),
		[enabled, data, endedJobs, t],
	);
	useListSourceHub(imageJobHub, enabled && watch, runningIds(operations), setHubDown, () => {
		queryClient.invalidateQueries({ queryKey: options.queryKey }).catch(() => undefined);
	});
	return operations;
}

type PushMap<V> = ReadonlyMap<string, V>;
type PushReducer<V> = (current: PushMap<V> | undefined, payload: unknown) => PushMap<V> | undefined;

/**
 * Folds a broadcasting hub's events into one no-fetch cache entry while `watch`. The entry is emptied on reconnect and
 * when the watcher detaches: either gap may have hidden a finish, and a row stuck on "running" would be worse than a
 * missed alert.
 */
function usePushSource<V>(
	{ enabled, watch }: Pick<JobSourceOptions, "enabled" | "watch">,
	path: string,
	queryKey: readonly string[],
	reducers: Readonly<Record<string, PushReducer<V>>>,
): PushMap<V> | undefined {
	const queryClient = useQueryClient();
	const { data } = useQuery<PushMap<V>>({ queryKey, queryFn: skipToken });
	const leased = enabled && watch;

	useEffect(() => {
		if (!leased) {
			return undefined;
		}
		const lease = acquireHubConnection(path);
		const { connection } = lease;
		const handlers = Object.entries(reducers).map(([name, reduce]) => {
			const handler = (payload: unknown): void => {
				queryClient.setQueryData<PushMap<V>>(queryKey, (current) => reduce(current, payload));
			};
			return [name, handler] as const;
		});
		for (const [name, handler] of handlers) {
			connection.on(name, handler);
		}
		lease.onReconnected(() => {
			queryClient.setQueryData<PushMap<V>>(queryKey, (current) => (current?.size ? new Map<string, V>() : current));
		});
		return () => {
			for (const [name, handler] of handlers) {
				connection.off(name, handler);
			}
			lease.release();
			// Detached, this watcher misses every event until it reattaches on a fresh connection, whose first start fires
			// no onReconnected: empty the entry now for the same reason a reconnect does.
			queryClient.setQueryData<PushMap<V>>(queryKey, (current) => (current?.size ? new Map<string, V>() : current));
		};
	}, [leased, path, queryKey, reducers, queryClient]);

	return enabled ? data : undefined;
}

const KNOWLEDGE_INDEXING_KEY = ["operations", "knowledgeIndexing"] as const;

const knowledgeReducers: Readonly<Record<string, PushReducer<string>>> = {
	"knowledge.documentChanged": (batch: KnowledgeIndexingBatch | undefined, payload) => {
		const documentId = field(payload, "documentId");
		const status = field(payload, "status");
		return documentId === undefined || status === undefined ? batch : applyKnowledgeDocumentStatus(batch, documentId, status);
	},
	"knowledge.documentRemoved": (batch: KnowledgeIndexingBatch | undefined, payload) => {
		const documentId = field(payload, "documentId");
		return documentId === undefined ? batch : removeKnowledgeDocument(batch, documentId);
	},
};

/** Knowledge-base indexing (uploads and repository imports) as one batch row. */
export function useKnowledgeIndexingOperation(options: JobSourceOptions): Operation | undefined {
	const batch = usePushSource(options, "knowledge-base/hub", KNOWLEDGE_INDEXING_KEY, knowledgeReducers);
	const { t } = options;
	return useMemo(() => fromKnowledgeIndexing(batch, t), [batch, t]);
}

const IMAGE_MODEL_DOWNLOADS_KEY = ["operations", "imageModelDownloads"] as const;

// Every method the images hub sends a connection gets a handler: a per-job event only reaches a joined job group.
const imageModelDownloadReducers: Readonly<Record<string, PushReducer<ModelDownloadEntry>>> = {
	"imageModelDownload.statusChanged": (downloads: ModelDownloads | undefined, payload) =>
		applyModelDownloadEvent(downloads, payload, "modelName"),
};

/** Image-model weight downloads, pushed on `images/hub` while the images feature is on. */
export function useImageModelDownloadOperations(options: JobSourceOptions): readonly Operation[] {
	const downloads = usePushSource(options, "images/hub", IMAGE_MODEL_DOWNLOADS_KEY, imageModelDownloadReducers);
	const { t } = options;
	return useMemo(() => fromModelDownloads(downloads, "image-model-download", t), [downloads, t]);
}

const TRANSCRIPTION_MODEL_DOWNLOADS_KEY = ["operations", "transcriptionModelDownloads"] as const;

const transcriptionModelDownloadReducers: Readonly<Record<string, PushReducer<ModelDownloadEntry>>> = {
	transcriptionModelDownloadChanged: (downloads: ModelDownloads | undefined, payload) =>
		applyModelDownloadEvent(downloads, payload, "modelId"),
};

/** Whisper weight downloads, pushed on `transcription/hub` while transcription is switched on. */
export function useTranscriptionModelDownloadOperations(options: JobSourceOptions): readonly Operation[] {
	const downloads = usePushSource(
		options,
		"transcription/hub",
		TRANSCRIPTION_MODEL_DOWNLOADS_KEY,
		transcriptionModelDownloadReducers,
	);
	const { t } = options;
	return useMemo(() => fromModelDownloads(downloads, "transcription-model-download", t), [downloads, t]);
}

const SCHEDULER_RUNS_KEY = ["operations", "schedulerRuns"] as const;

// Every method the hub broadcasts gets a handler, the ignored ones included, so the client logs no "No client method".
const schedulerReducers: Readonly<Record<string, PushReducer<SchedulerRunEntry>>> = Object.fromEntries(
	[
		"scheduler.runStarted",
		"scheduler.runProgress",
		"scheduler.runCompleted",
		"scheduler.runFailed",
		"scheduler.runCancelled",
		"scheduler.jobDefinitionChanged",
	].map((name) => [name, (runs: SchedulerRuns | undefined, payload: unknown) => applySchedulerRunEvent(runs, name, payload)]),
);

/** Scheduler runs on every page, not only the Scheduler page. */
export function useSchedulerRunOperations(options: JobSourceOptions): readonly Operation[] {
	const runs = usePushSource(options, "scheduler/hub", SCHEDULER_RUNS_KEY, schedulerReducers);
	const { t } = options;
	return useMemo(() => fromSchedulerRuns(runs, t), [runs, t]);
}
