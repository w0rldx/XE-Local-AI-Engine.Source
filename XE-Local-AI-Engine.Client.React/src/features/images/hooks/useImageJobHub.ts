import { HubConnectionState } from "@microsoft/signalr";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useRef } from "react";

import { acquireHubConnection } from "@/core/api/signalr/SharedHubConnection";
import {
	IMAGE_JOB_STATUS_CHANGED,
	type ImageJobProgressView,
	type ImageJobStatus,
	imageJobStatusPushSchema,
	keepLatestImageJobProgress,
	toImageJobProgressView,
} from "@/features/images/models/ImageModels";

// Realtime push for image-job status. Connects to the image SignalR hub for the lifetime of the mounting component
// and, for every active (non-terminal) job the page passes in, joins that job's per-job group via the hub's
// Subscribe(jobId) method (the backend delivers a job's events ONLY to its group).
//
// Each push does two things with the payload, and BOTH matter:
//
//  1. It writes the generation timeline (phase, step counters, estimate) into a per-job cache entry. None of that is
//     in the REST contract — the runtime reads it off the daemon's stdout — so if this hook dropped the payload, no
//     amount of refetching would ever produce it. That is what the hook used to do: parse, dedupe on seq, then call
//     invalidate and discard every other field.
//  2. It invalidates the jobs list on a TERMINAL transition only. A refetch per step would be pure churn: the row's
//     REST fields do not change between steps, and the only field that does — the status — is already in the push.
//
// Job state lives in TanStack Query, never a store, which is why the live progress gets its own query key rather than
// a ref or a context.
//
// Connection + subscribe lifecycle copies usePreviewWorkflowHub: invokes are guarded on Connected, re-applied on
// reconnect (a transient drop loses group membership), and cleanup defers stop() until the start promise settles so a
// StrictMode double-invoke / fast remount cannot abort an in-flight negotiation.

// The partial generated-query-key that matches every cached variant of listImageJobs. Kept local so the literal
// `_id` (which trips biome's naming-convention rule) is constructed in one place, mirroring useImageQueries.
function jobsInvalidationKey(): readonly [{ _id: string }] {
	// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
	return [{ _id: "listImageJobs" }];
}

/** Cache key for one job's live generation timeline. Push-fed only — it has no endpoint behind it. */
export function imageJobProgressKey(jobId: string): readonly [string, string] {
	return ["imageJobProgress", jobId];
}

/**
 * Reads the live generation timeline for one job, or `null` before the first push lands. Push-fed: the query has no
 * fetcher, so it never goes to the network and never overwrites what the hub wrote.
 */
export function useImageJobProgress(jobId: string): ImageJobProgressView | null {
	const query = useQuery({
		queryKey: imageJobProgressKey(jobId),
		queryFn: (): ImageJobProgressView | null => null,
		staleTime: Number.POSITIVE_INFINITY,
	});
	return query.data ?? null;
}

export function useImageJobHub(activeJobIds: readonly string[]): void {
	const queryClient = useQueryClient();

	// Latest desired active-job set, read inside the effect's reconcile without re-running the (connection-owning)
	// effect on every list change. Updated synchronously below so a reconcile always sees the current set. The
	// initializer below is a type-only cast (zero-cost) rather than `new Set(...)`, since `.current` is
	// unconditionally reassigned to the current set two lines down on every render anyway.
	const desiredJobIdsRef = useRef<ReadonlySet<string>>(undefined as unknown as ReadonlySet<string>);
	// Per-job highest seq seen, so a replayed/duplicated push (same seq) is ignored. Lives across the connection.
	const lastSeqByJob = useRef<Map<string, number>>(new Map());
	// Per-job last coarse status pushed, so the list is refetched on a real transition rather than on every step.
	const lastStatusByJob = useRef<Map<string, ImageJobStatus>>(new Map());
	// Bumped whenever the desired set changes, to trigger the reconcile effect below.
	const reconcileRef = useRef<((desired: ReadonlySet<string>) => void) | undefined>(undefined);

	const desired = new Set(activeJobIds);
	desiredJobIdsRef.current = desired;

	// Drive a reconcile whenever the active-job set changes (a new job enqueued / a job reaching terminal). Keyed on the
	// sorted-joined id string so it fires exactly when membership should change, not on every render. The set is rebuilt
	// from that key inside the effect (job ids are GUIDs — no embedded commas) so the effect body uses its only dep.
	const desiredKey = [...desired].sort().join(",");
	useEffect(() => {
		const target = new Set(desiredKey ? desiredKey.split(",") : []);
		reconcileRef.current?.(target);
	}, [desiredKey]);

	useEffect(() => {
		// Shared refcounted connection: reused across mounts so re-opening the images page does not pay a fresh negotiate +
		// WebSocket upgrade. Handlers + per-job group membership below stay per-mount so this subscriber coexists with any
		// other subscriber to the same hub.
		const hub = acquireHubConnection("images/hub");
		const { connection } = hub;

		const invalidateJobs = (): void => {
			queryClient.invalidateQueries({ queryKey: jobsInvalidationKey() }).catch(() => undefined);
		};

		const onStatus = (payload: unknown): void => {
			const parsed = imageJobStatusPushSchema.safeParse(payload);
			if (!parsed.success) {
				return;
			}
			const push = parsed.data;
			// Dedupe on the per-job monotonic seq: a push whose seq is not newer than the last one seen for this job is a
			// replay/duplicate (events arrive both via late-subscriber replay AND live) and carries nothing new.
			const lastSeq = lastSeqByJob.current.get(push.jobId);
			if (lastSeq !== undefined && push.seq <= lastSeq) {
				return;
			}
			lastSeqByJob.current.set(push.jobId, push.seq);

			const progress = toImageJobProgressView(push);
			queryClient.setQueryData(imageJobProgressKey(push.jobId), (current: ImageJobProgressView | null | undefined) =>
				keepLatestImageJobProgress(current ?? null, progress),
			);

			// Refetch the row only when the COARSE status moves (queued → generating → terminal). Those are the
			// transitions that change what the list endpoint serves — the start timestamp, and on a terminal push the
			// duration, produced size, resolved seed and image id. A step tick changes none of them, so refetching on
			// each would be pure churn against a job that emits one every second.
			const lastStatus = lastStatusByJob.current.get(push.jobId);
			lastStatusByJob.current.set(push.jobId, progress.status);
			if (lastStatus !== progress.status) {
				invalidateJobs();
			}
		};

		connection.on(IMAGE_JOB_STATUS_CHANGED, onStatus);

		// The jobIds this connection has joined a group for. Diffed against the desired set so each id is Subscribe'd /
		// Unsubscribe'd exactly once. Lives across reconnects so a reconnect can re-join every active job.
		const subscribedJobIds = new Set<string>();

		const joinJobGroup = (jobId: string): void => {
			connection.invoke("Subscribe", jobId).catch((error: unknown) => {
				console.warn("image job hub failed to subscribe to job", jobId, error);
			});
		};

		const leaveJobGroup = (jobId: string): void => {
			connection.invoke("Unsubscribe", jobId).catch((error: unknown) => {
				console.warn("image job hub failed to unsubscribe from job", jobId, error);
			});
		};

		// Reconcile the connection's group membership to the desired active-job set. No-op unless Connected (a desired
		// set computed while disconnected is applied wholesale by onreconnected / the post-start reconcile).
		const reconcileSubscriptions = (target: ReadonlySet<string>): void => {
			if (connection.state !== HubConnectionState.Connected) {
				return;
			}
			for (const jobId of target) {
				if (!subscribedJobIds.has(jobId)) {
					subscribedJobIds.add(jobId);
					joinJobGroup(jobId);
				}
			}
			for (const jobId of [...subscribedJobIds]) {
				if (!target.has(jobId)) {
					subscribedJobIds.delete(jobId);
					leaveJobGroup(jobId);
				}
			}
		};

		reconcileRef.current = reconcileSubscriptions;

		let disposed = false;
		// Reconcile group membership once the shared connection is up. whenStarted resolves when the initial start settles,
		// or on the next microtask for a late subscriber that acquires while the connection is already connected; guarded
		// on Connected because whenStarted also resolves after a failed initial start.
		hub.whenStarted.then(() => {
			if (disposed || connection.state !== HubConnectionState.Connected) {
				return;
			}
			// On (re)connect the server-side group membership is empty — forget what we thought we'd joined and re-apply
			// the current desired set so a job active before/at connect is subscribed.
			subscribedJobIds.clear();
			reconcileSubscriptions(desiredJobIdsRef.current);
		});

		// After a transient drop + automatic reconnect the server has dropped all group memberships, so re-join every
		// currently-active job. Scoped to this handle and dropped by hub.release() below.
		hub.onReconnected(() => {
			subscribedJobIds.clear();
			reconcileSubscriptions(desiredJobIdsRef.current);
		});

		return () => {
			disposed = true;
			reconcileRef.current = undefined;
			connection.off(IMAGE_JOB_STATUS_CHANGED, onStatus);
			// Release the shared lease: drops this handle's reconnected callback and, once the last subscriber releases,
			// stops the connection after the start promise settles (so cleanup never aborts an in-flight negotiation).
			hub.release();
		};
	}, [queryClient]);
}
