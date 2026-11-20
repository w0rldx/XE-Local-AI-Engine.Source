import { HubConnectionState } from "@microsoft/signalr";
import { skipToken, useQueries, useQuery, useQueryClient } from "@tanstack/react-query";
import type { TFunction } from "i18next";
import { useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";

import { getErrorStatus } from "@/core/api/errors/RetryClassification";
import {
	getImageJobOptions,
	getModelLabExperimentOptions,
	getTrainingRunOptions,
	listBaseArtifactsOptions,
	listGraphWorkflowRunsOptions,
	listImageJobsOptions,
	listModelLabExperimentsOptions,
	listTrainingDatasetsOptions,
	listTrainingRunsOptions,
	listWorkSessionsOptions,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { acquireHubConnection } from "@/core/api/signalr/SharedHubConnection";
import { joinHubGroup } from "@/core/api/signalr/SharedHubGroups";
import {
	applyKnowledgeDocumentStatus,
	applyModelDownloadEvent,
	applySchedulerRunEvent,
	fromBaseArtifactDownloads,
	fromDatasetBuilds,
	fromImageJobs,
	fromKnowledgeIndexing,
	fromModelDownloads,
	fromModelLabExperiments,
	fromSchedulerRuns,
	fromTrainingRuns,
	fromWaitingGraphRuns,
	fromWaitingWorkSessions,
	graphRunWaitingStatus,
	type KnowledgeIndexingBatch,
	type ModelDownloadEntry,
	type ModelDownloads,
	removeKnowledgeDocument,
	type SchedulerRunEntry,
	type SchedulerRuns,
	stringField,
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
// - Broadcast-only list sources (graph runs and work sessions waiting for the user, base checkpoint downloads): the
//   same list snapshot, re-read on their hub's app-wide broadcast; nothing is joined. Model Lab experiments are a list
//   source on the training-run hub like training runs.

interface ListSourceHub {
	readonly path: string;
	/** How a running id's group is joined; absent for a broadcast-only source, which passes no running ids. */
	readonly subscribeArgs?: (id: string) => readonly unknown[];
	/** Per client method: the job id when this payload may move the job's list row, else undefined. */
	readonly events: Readonly<Record<string, (payload: unknown) => string | undefined>>;
	/** Per broadcast client method: whether this payload may change the list. */
	readonly broadcasts: Readonly<Record<string, (payload: unknown) => boolean>>;
	/**
	 * Subscribe replays nothing, so an end between the list GET and the join reaches no handler: the list is re-read
	 * once after each pass of successful Subscribes. The images hub replays a job's buffered events instead.
	 */
	readonly liveOnly?: boolean;
}

// An afterSeq past any retained event: live events only, the list GET is the snapshot. A finished run's buffer is
// evicted, and the hub answers a replay reset for it, which re-reads the list like a state event.
const LIVE_ONLY = Number.MAX_SAFE_INTEGER;

// Broadcast on a job kind's own hub when a job of that kind is created anywhere, and for a base checkpoint download,
// which has no group, also when it ends. The payload names the kind, since three kinds share the training-run hub.
const JOB_CREATED = "job.created";
const JOB_ENDED = "job.ended";

const ofKind =
	(kind: string) =>
	(payload: unknown): boolean =>
		stringField(payload, "kind") === kind;

const trainingRunHub: ListSourceHub = {
	path: "training/runs/hub",
	subscribeArgs: (id) => [id, LIVE_ONLY],
	liveOnly: true,
	events: {
		"trainingRun.event": (payload) => (stringField(payload, "kind") === "State" ? stringField(payload, "runId") : undefined),
		"trainingRun.replayReset": (payload) => stringField(payload, "runId"),
	},
	broadcasts: { [JOB_CREATED]: ofKind("TrainingRun") },
};

// Model Lab experiments stream on the training-run hub under their own id, and end with the same State event.
const modelLabExperimentHub: ListSourceHub = {
	...trainingRunHub,
	broadcasts: { [JOB_CREATED]: ofKind("ModelLabExperiment") },
};

const baseArtifactDownloadHub: ListSourceHub = {
	path: "training/runs/hub",
	events: {},
	broadcasts: { [JOB_CREATED]: ofKind("BaseArtifactDownload"), [JOB_ENDED]: ofKind("BaseArtifactDownload") },
};

const graphRunStatusHub: ListSourceHub = {
	path: "graph-workflows/hub",
	events: {},
	broadcasts: { graphWorkflowRunStatusChanged: () => true },
};

const workSessionStatusHub: ListSourceHub = {
	path: "work-sessions/hub",
	events: {},
	broadcasts: { workSessionStatusChanged: () => true },
};

const datasetBuildHub: ListSourceHub = {
	path: "training/datasets/hub",
	subscribeArgs: (id) => [id, LIVE_ONLY],
	liveOnly: true,
	events: {
		"datasetGeneration.event": (payload) =>
			stringField(payload, "kind") === "State" ? stringField(payload, "datasetId") : undefined,
		"datasetGeneration.replayReset": (payload) => stringField(payload, "datasetId"),
	},
	broadcasts: { [JOB_CREATED]: ofKind("DatasetBuild") },
};

const TERMINAL_IMAGE_PHASES: ReadonlySet<string | undefined> = new Set(["Succeeded", "Failed", "Cancelled"]);

const imageJobHub: ListSourceHub = {
	path: "images/hub",
	subscribeArgs: (id) => [id],
	events: {
		"imageJob.statusChanged": (payload) =>
			TERMINAL_IMAGE_PHASES.has(stringField(payload, "phase")) ? stringField(payload, "jobId") : undefined,
	},
	broadcasts: { [JOB_CREATED]: ofKind("ImageJob") },
};

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
 * a matching broadcast (with its payload), and after a reconnect, whose gap may hide a finish or a creation. `onHubDown`
 * reports whether the lease is not connected (a failed start, reconnecting, closed until the manager's restart) or a held
 * id's Subscribe failed.
 */
function useListSourceHub(
	hub: ListSourceHub,
	watching: boolean,
	runningIds: readonly string[],
	onHubDown: (down: boolean) => void,
	onChange: (broadcast?: unknown) => void,
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
			if (hub.subscribeArgs === undefined || connection.state !== HubConnectionState.Connected) {
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
		for (const [name, matches] of Object.entries(hub.broadcasts)) {
			handlers.push([
				name,
				(payload: unknown): void => {
					if (matches(payload)) {
						onChangeRef.current(payload);
					}
				},
			]);
		}
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

// The same raw cache entry the datasets page reads through its own `select`, so both share one request and one poller.
export function useDatasetBuildOperations({ enabled, watch, t }: JobSourceOptions): readonly Operation[] {
	const queryClient = useQueryClient();
	const [hubDown, setHubDown] = useState(false);
	const options = listTrainingDatasetsOptions();
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
		const documentId = stringField(payload, "documentId");
		const status = stringField(payload, "status");
		return documentId === undefined || status === undefined ? batch : applyKnowledgeDocumentStatus(batch, documentId, status);
	},
	"knowledge.documentRemoved": (batch: KnowledgeIndexingBatch | undefined, payload) => {
		const documentId = stringField(payload, "documentId");
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

// A 403 or 404 (the caller's role, or the feature switched off on the node) stops a pushed list quietly: no lease, no
// poll, no focus or mount refetch, no rows.
function isUnavailable(error: unknown): boolean {
	const status = getErrorStatus(error);
	return status === 403 || status === 404;
}

// Discovery pace of a pushed list whose hub is down.
const DISCOVERY_POLL_MS = 60_000;

interface ListQuery<T> {
	readonly state: { readonly data?: T; readonly error: unknown };
}

const available = (query: ListQuery<unknown>): boolean => !isUnavailable(query.state.error);

/**
 * A pushed list's read options: hydrated once, re-read on its hub's push, and no interval beside a live hub.
 * hub degraded: while `degraded` (the watching caller's hub is down) no push announces a change, so the list is
 * re-read every minute, or at the hub-down pace while one of its rows runs, also in a hidden tab, whose title count and
 * OS notification are for; the reconnect re-reads it once and the interval stops.
 */
function pushedListSettings<T = unknown>(degraded: boolean, running: (data: T) => boolean = () => false) {
	return {
		refetchInterval: (query: ListQuery<T>): number | false => {
			if (!degraded || !available(query)) {
				return false;
			}
			return query.state.data !== undefined && running(query.state.data) ? HUB_DOWN_POLL_MS : DISCOVERY_POLL_MS;
		},
		refetchIntervalInBackground: degraded,
		refetchOnWindowFocus: available,
		refetchOnMount: available,
	};
}

/**
 * Graph runs parked for a decision (a Pause node), read by status so an empty feed still discovers the first one, and
 * re-read on `graphWorkflowRunStatusChanged`: a run moving into a wait, or out of one this list holds.
 */
export function useWaitingGraphRunOperations({ enabled, watch, t }: JobSourceOptions): readonly Operation[] {
	const queryClient = useQueryClient();
	const [hubDown, setHubDown] = useState(false);
	const options = listGraphWorkflowRunsOptions({ query: { status: graphRunWaitingStatus, limit: ACTIVE_ROWS } });
	const { data, error } = useQuery({ ...options, enabled, ...pushedListSettings<unknown>(watch && hubDown) });
	const unavailable = isUnavailable(error);
	const runs = enabled && !unavailable ? data?.runs : undefined;
	useListSourceHub(graphRunStatusHub, enabled && watch && !unavailable, [], setHubDown, (broadcast) => {
		const runId = stringField(broadcast, "runId");
		if (
			broadcast === undefined ||
			stringField(broadcast, "status") === graphRunWaitingStatus ||
			runs?.some((run) => run.id === runId) === true
		) {
			queryClient.invalidateQueries({ queryKey: options.queryKey }).catch(() => undefined);
		}
	});
	return useMemo(() => fromWaitingGraphRuns(runs ?? [], t), [runs, t]);
}

/**
 * Work sessions waiting for input or approval, filtered out of the list the Work Sessions page reads, and re-read on
 * `workSessionStatusChanged`.
 */
export function useWaitingWorkSessionOperations({ enabled, watch, t }: JobSourceOptions): readonly Operation[] {
	const queryClient = useQueryClient();
	const [hubDown, setHubDown] = useState(false);
	const options = listWorkSessionsOptions();
	const { data, error } = useQuery({ ...options, enabled, ...pushedListSettings<unknown>(watch && hubDown) });
	const unavailable = isUnavailable(error);
	const sessions = enabled && !unavailable ? data?.items : undefined;
	useListSourceHub(workSessionStatusHub, enabled && watch && !unavailable, [], setHubDown, () => {
		queryClient.invalidateQueries({ queryKey: options.queryKey }).catch(() => undefined);
	});
	return useMemo(() => fromWaitingWorkSessions(sessions ?? [], t), [sessions, t]);
}

/**
 * Model Lab experiments, from the list the Model Lab page reads, so a run that ends while the user is elsewhere alerts.
 * A list source on the training-run hub: `job.created` announces a start, the running id's State event its end.
 */
export function useModelLabExperimentOperations({ enabled, watch, t }: JobSourceOptions): readonly Operation[] {
	const queryClient = useQueryClient();
	const [hubDown, setHubDown] = useState(false);
	const options = listModelLabExperimentsOptions();
	const { data, error } = useQuery({ ...options, enabled, ...pushedListSettings<unknown>(watch && hubDown) });
	const unavailable = isUnavailable(error);
	const list = enabled && !unavailable ? data : undefined;
	const reread = (): void => {
		queryClient.invalidateQueries({ queryKey: options.queryKey }).catch(() => undefined);
	};
	// hub degraded: the running experiment is read by id, one row instead of every retained prompt and result, until it
	// leaves Running; then the list is re-read once for the terminal row.
	const activeId = list?.activeExperimentId ?? null;
	const activeOptions = getModelLabExperimentOptions({ path: { experimentId: activeId ?? "" } });
	useQuery({
		...activeOptions,
		queryFn: async (context) => {
			// `queryOptions()` always emits a queryFn; the generated one reads its request off the key.
			const experiment = await activeOptions.queryFn!(context);
			if (experiment.status !== "Running") {
				reread();
			}
			return experiment;
		},
		enabled: watch && hubDown && activeId !== null,
		refetchInterval: (query) => (query.state.data?.status === "Running" ? HUB_DOWN_POLL_MS : false),
		refetchIntervalInBackground: true,
	});
	const operations = useMemo(() => fromModelLabExperiments(list?.experiments ?? [], t), [list, t]);
	useListSourceHub(modelLabExperimentHub, enabled && watch && !unavailable, runningIds(operations), setHubDown, reread);
	return operations;
}

// sampled value: a download's byte count lives in the node's memory and no hub pushes it, so beside a live hub the
// watching caller re-reads the list once a minute while one downloads, for the row's progress.
const DOWNLOAD_PROGRESS_POLL_MS = 60_000;

/**
 * Base checkpoint downloads (Model Lab and training share the store), from the list both pages read, re-read on the
 * training-run hub's `job.created` and `job.ended` for a download.
 */
export function useBaseArtifactDownloadOperations({ enabled, watch, t }: JobSourceOptions): readonly Operation[] {
	const queryClient = useQueryClient();
	const [hubDown, setHubDown] = useState(false);
	const options = listBaseArtifactsOptions();
	const downloading = (list: { readonly items: readonly { readonly status: string }[] }): boolean =>
		list.items.some((artifact) => artifact.status === "Downloading");
	const settings = pushedListSettings(watch && hubDown, downloading);
	const { data, error } = useQuery({
		...options,
		enabled,
		...settings,
		refetchInterval: (query) =>
			settings.refetchInterval(query) ||
			(watch && available(query) && query.state.data !== undefined && downloading(query.state.data)
				? DOWNLOAD_PROGRESS_POLL_MS
				: false),
	});
	const unavailable = isUnavailable(error);
	const artifacts = enabled && !unavailable ? data?.items : undefined;
	useListSourceHub(baseArtifactDownloadHub, enabled && watch && !unavailable, [], setHubDown, () => {
		queryClient.invalidateQueries({ queryKey: options.queryKey }).catch(() => undefined);
	});
	return useMemo(() => fromBaseArtifactDownloads(artifacts ?? [], t), [artifacts, t]);
}
