// Server state for the work-session surface. Every call goes through the generated hey-api `*Options()` /
// `*Mutation()` wrapped in withResponseValidation, exactly as `useDevelopment.ts` does — no hand-wired axios.
//
// The generated query keys are single-element arrays `[{ _id: "<operationId>", path, query, … }]`, and TanStack
// matches them by PARTIAL DEEP equality. So `[{ _id, path: { sessionId } }]` invalidates every cached variant of one
// endpoint for one session while leaving the other sessions' caches alone.

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	cancelWorkSessionMutation,
	createWorkSessionMutation,
	deleteWorkSessionMutation,
	getWorkSessionArtifactContentOptions,
	getWorkSessionOptions,
	listWorkSessionArtifactsOptions,
	listWorkSessionCheckpointsOptions,
	listWorkSessionEventsOptions,
	listWorkSessionFindingsOptions,
	listWorkSessionsOptions,
	listWorkSessionTasksOptions,
	pauseWorkSessionMutation,
	postWorkSessionMessageMutation,
	resumeWorkSessionMutation,
	startWorkSessionMutation,
	updateWorkSessionMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";

/** Generated operationIds, which are also the generated SDK fn names and the `_id` of every generated query key. */
export const workSessionQueryIds = {
	list: "listWorkSessions",
	get: "getWorkSession",
	tasks: "listWorkSessionTasks",
	findings: "listWorkSessionFindings",
	artifacts: "listWorkSessionArtifacts",
	checkpoints: "listWorkSessionCheckpoints",
	events: "listWorkSessionEvents",
	artifactContent: "getWorkSessionArtifactContent",
} as const;

export type WorkSessionQueryId = (typeof workSessionQueryIds)[keyof typeof workSessionQueryIds];

/**
 * Partial generated-query-key filter. Without `sessionId` it matches every session's cached variant of that
 * endpoint (what the list needs); with it, only the one session's.
 */
export function workSessionInvalidationKey(
	operationId: string,
	sessionId?: string,
): readonly [{ _id: string; path?: { sessionId: string } }] {
	// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
	return sessionId ? [{ _id: operationId, path: { sessionId } }] : [{ _id: operationId }];
}

// The events feed is the only one the server pages (`limit`/`hasMore`); the other four are `sinceSeq`-bounded and
// otherwise complete, so the client always reads them whole from 0.
const FULL_FEED = { sinceSeq: 0 } as const;
export const workSessionEventsPageSize = 200;
/** The server clamps `limit` at 500; asking for more is silently truncated, so the tab stops offering "load more" there. */
export const workSessionEventsMaxLimit = 500;

interface FeedOptions {
	/** Polling cadence while the hub is unavailable. `undefined` (the live case) means no polling at all. */
	readonly pollIntervalMs?: number;
	readonly enabled?: boolean;
}

function feedQuerySettings(sessionId: string | undefined, options: FeedOptions) {
	return {
		enabled: (options.enabled ?? true) && Boolean(sessionId),
		refetchInterval: options.pollIntervalMs ?? false,
	} as const;
}

export function useWorkSessionList(options: FeedOptions = {}) {
	return useQuery({
		...withResponseValidation(listWorkSessionsOptions()),
		refetchInterval: options.pollIntervalMs ?? false,
	});
}

export function useWorkSession(sessionId: string | undefined, options: FeedOptions = {}) {
	return useQuery({
		...withResponseValidation(getWorkSessionOptions({ path: { sessionId: sessionId ?? "" } })),
		...feedQuerySettings(sessionId, options),
	});
}

export function useWorkSessionTasks(sessionId: string | undefined, options: FeedOptions = {}) {
	return useQuery({
		...withResponseValidation(listWorkSessionTasksOptions({ path: { sessionId: sessionId ?? "" }, query: FULL_FEED })),
		...feedQuerySettings(sessionId, options),
	});
}

export function useWorkSessionFindings(sessionId: string | undefined, options: FeedOptions = {}) {
	return useQuery({
		...withResponseValidation(listWorkSessionFindingsOptions({ path: { sessionId: sessionId ?? "" }, query: FULL_FEED })),
		...feedQuerySettings(sessionId, options),
	});
}

export function useWorkSessionArtifacts(sessionId: string | undefined, options: FeedOptions = {}) {
	return useQuery({
		...withResponseValidation(listWorkSessionArtifactsOptions({ path: { sessionId: sessionId ?? "" }, query: FULL_FEED })),
		...feedQuerySettings(sessionId, options),
	});
}

export function useWorkSessionCheckpoints(sessionId: string | undefined, options: FeedOptions = {}) {
	return useQuery({
		...withResponseValidation(listWorkSessionCheckpointsOptions({ path: { sessionId: sessionId ?? "" }, query: FULL_FEED })),
		...feedQuerySettings(sessionId, options),
	});
}

export function useWorkSessionEvents(sessionId: string | undefined, limit: number, options: FeedOptions = {}) {
	return useQuery({
		...withResponseValidation(
			listWorkSessionEventsOptions({ path: { sessionId: sessionId ?? "" }, query: { sinceSeq: 0, limit } }),
		),
		...feedQuerySettings(sessionId, options),
	});
}

export function useWorkSessionArtifactContent(sessionId: string | undefined, artifactId: string | undefined) {
	return useQuery({
		...withResponseValidation(
			getWorkSessionArtifactContentOptions({ path: { sessionId: sessionId ?? "", artifactId: artifactId ?? "" } }),
		),
		enabled: Boolean(sessionId) && Boolean(artifactId),
	});
}

export function useCreateWorkSession() {
	const queryClient = useQueryClient();
	return useMutation({
		...withResponseValidation(createWorkSessionMutation()),
		onSuccess: () => queryClient.invalidateQueries({ queryKey: workSessionInvalidationKey(workSessionQueryIds.list) }),
	});
}

export function useUpdateWorkSession(sessionId: string) {
	const queryClient = useQueryClient();
	return useMutation({
		...withResponseValidation(updateWorkSessionMutation()),
		onSuccess: async () => {
			await queryClient.invalidateQueries({ queryKey: workSessionInvalidationKey(workSessionQueryIds.get, sessionId) });
			await queryClient.invalidateQueries({ queryKey: workSessionInvalidationKey(workSessionQueryIds.list) });
		},
	});
}

export function useDeleteWorkSession() {
	const queryClient = useQueryClient();
	return useMutation({
		...withResponseValidation(deleteWorkSessionMutation()),
		// The delete also removes the owned conversation, so the caller must navigate away rather than keep a route
		// open on it. Only the list is refreshed here.
		onSuccess: () => queryClient.invalidateQueries({ queryKey: workSessionInvalidationKey(workSessionQueryIds.list) }),
	});
}

/**
 * The four lifecycle commands. Each answers with the session's new state, but the supervisor picks the work up
 * out-of-band (202), so the detail query is invalidated rather than primed from the response.
 */
export function useWorkSessionLifecycle(sessionId: string) {
	const queryClient = useQueryClient();
	const refresh = async (): Promise<void> => {
		await queryClient.invalidateQueries({ queryKey: workSessionInvalidationKey(workSessionQueryIds.get, sessionId) });
		await queryClient.invalidateQueries({ queryKey: workSessionInvalidationKey(workSessionQueryIds.list) });
	};

	const start = useMutation({ ...withResponseValidation(startWorkSessionMutation()), onSuccess: refresh });
	const pause = useMutation({ ...withResponseValidation(pauseWorkSessionMutation()), onSuccess: refresh });
	const resume = useMutation({ ...withResponseValidation(resumeWorkSessionMutation()), onSuccess: refresh });
	const cancel = useMutation({ ...withResponseValidation(cancelWorkSessionMutation()), onSuccess: refresh });

	return { start, pause, resume, cancel };
}

export function usePostWorkSessionMessage() {
	return useMutation(withResponseValidation(postWorkSessionMessageMutation()));
}
