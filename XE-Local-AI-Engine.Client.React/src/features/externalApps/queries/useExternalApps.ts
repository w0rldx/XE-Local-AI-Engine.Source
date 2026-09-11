// Server state for the External Apps surface. Every call goes through the generated hey-api `*Options()` /
// `*Mutation()` wrapped in withResponseValidation, exactly as `useDevWorkflows.ts` does — no hand-wired axios, no
// hand-written request types.
//
// The generated query keys are single-element arrays `[{ _id: "<operationId>", path, query, … }]`, and TanStack
// matches them by PARTIAL DEEP equality. So `[{ _id, path: { instanceId } }]` invalidates every cached variant of one
// endpoint for one instance while leaving the other instances' caches alone.

import { type QueryClient, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	cancelExternalAppOperationMutation,
	getExternalAppCatalogApplicationOptions,
	getExternalAppInstallPreviewOptions,
	getExternalAppInstanceLogsOptions,
	getExternalAppInstanceOptions,
	getExternalAppRuntimeOptions,
	getExternalAppUpdatePreviewOptions,
	installExternalAppMutation,
	listExternalAppCatalogOptions,
	listExternalAppInstanceEventsOptions,
	listExternalAppInstancesOptions,
	refreshExternalAppCatalogMutation,
	refreshExternalAppRuntimeMutation,
	resetExternalAppMutation,
	restartExternalAppMutation,
	startExternalAppMutation,
	stopExternalAppMutation,
	uninstallExternalAppMutation,
	updateExternalAppMutation,
	updateExternalAppVariablesMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { isExternalAppBusy, toExternalAppStatus } from "@/features/externalApps/models/ExternalAppModels";

/** Generated operationIds, which are also the generated SDK fn names and the `_id` of every generated query key. */
export const externalAppQueryIds = {
	runtime: "getExternalAppRuntime",
	catalog: "listExternalAppCatalog",
	application: "getExternalAppCatalogApplication",
	installPreview: "getExternalAppInstallPreview",
	instances: "listExternalAppInstances",
	instance: "getExternalAppInstance",
	updatePreview: "getExternalAppUpdatePreview",
	events: "listExternalAppInstanceEvents",
	logs: "getExternalAppInstanceLogs",
} as const;

export type ExternalAppQueryId = (typeof externalAppQueryIds)[keyof typeof externalAppQueryIds];

/**
 * Partial generated-query-key filter. Without a `path` it matches every cached variant of that endpoint (what the
 * lists need); with one, only the addressed instance or application.
 */
export function externalAppInvalidationKey(
	operationId: string,
	path?: Readonly<Record<string, string>>,
): readonly [{ _id: string; path?: Readonly<Record<string, string>> }] {
	// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
	return path ? [{ _id: operationId, path }] : [{ _id: operationId }];
}

/** Instance-list cadence while any listed instance is busy. An instance-scoped hub cannot feed a list. */
export const externalAppListPollIntervalMs = 5_000;

/** The history feed's page size, and the hub's replay cap — one page is one full replay window. */
export const externalAppEventsPageSize = 200;

interface FeedOptions {
	/** Polling cadence while the hub is unavailable. `undefined` (the live case) means no polling at all. */
	readonly pollIntervalMs?: number;
	readonly enabled?: boolean;
}

/**
 * The container-runtime health card. No polling: it changes when the operator changes the machine, and the card's own
 * "Check again" action plus a hub ping on a terminal status are what refresh it.
 */
export function useExternalAppRuntime() {
	return useQuery({ ...withResponseValidation(getExternalAppRuntimeOptions()) });
}

/**
 * Re-probes the runtime. An omitted `acknowledgeDaemonId` re-probes without approving anything; supplied, it must
 * equal the daemon id currently observed or the node answers 400 — so the operator approves the identity they were
 * SHOWN, and a daemon that changed again between render and click is refused rather than silently trusted.
 */
export function useRefreshExternalAppRuntime() {
	const queryClient = useQueryClient();
	const invalidateRuntime = (): Promise<void> =>
		queryClient.invalidateQueries({ queryKey: externalAppInvalidationKey(externalAppQueryIds.runtime) });
	return useMutation({
		...withResponseValidation(refreshExternalAppRuntimeMutation()),
		onSuccess: invalidateRuntime,
		// A refusal is itself news about the daemon: the 400 means the id the card OFFERED is no longer the observed
		// one, so the card has to be re-read or it keeps offering to approve an identity the node has already rejected.
		onError: invalidateRuntime,
	});
}

export function useExternalAppCatalog() {
	return useQuery({ ...withResponseValidation(listExternalAppCatalogOptions()) });
}

/** A refresh can change every application's manifest, so it drops the catalog AND every single-application entry. */
export function useRefreshExternalAppCatalog() {
	const queryClient = useQueryClient();
	return useMutation({
		...withResponseValidation(refreshExternalAppCatalogMutation()),
		onSuccess: async () => {
			await queryClient.invalidateQueries({ queryKey: externalAppInvalidationKey(externalAppQueryIds.catalog) });
			await queryClient.invalidateQueries({ queryKey: externalAppInvalidationKey(externalAppQueryIds.application) });
		},
	});
}

export function useExternalAppApplication(applicationId: string | undefined) {
	return useQuery({
		...withResponseValidation(getExternalAppCatalogApplicationOptions({ path: { applicationId: applicationId ?? "" } })),
		enabled: Boolean(applicationId),
	});
}

/**
 * The install preview: the manifest fingerprint the acceptance binds to, the declared variables, the effective
 * permissions and the resource verdict. `staleTime: 0` because the dialog re-reads it on entering the resource step —
 * the numbers there must be current, not whatever was true when the dialog opened.
 */
export function useExternalAppInstallPreview(applicationId: string | undefined, enabled: boolean) {
	return useQuery({
		...withResponseValidation(getExternalAppInstallPreviewOptions({ path: { applicationId: applicationId ?? "" } })),
		enabled: enabled && Boolean(applicationId),
		staleTime: 0,
	});
}

/**
 * The installed list. Polls at 5s, but only while a listed instance is actually busy: an instance-scoped hub cannot
 * feed a list, and a timer on a settled list is a timer burning for nothing.
 */
export function useExternalAppInstances() {
	return useQuery({
		...withResponseValidation(listExternalAppInstancesOptions()),
		refetchInterval: (query) => externalAppListPollInterval(query.state.data?.items),
	});
}

/**
 * The list's polling decision, exported so it is asserted directly rather than by waiting five seconds on a timer: a
 * settled list must answer `false`, because a timer on rows that cannot change on their own burns for nothing.
 *
 * Typed on the one field it reads, so it keeps working when the list widens from the summary shape to the full
 * instance view (see `ExternalAppInstanceListItem`).
 */
export function externalAppListPollInterval(items: readonly { readonly status?: string }[] | undefined): number | false {
	const anyBusy = (items ?? []).some((item) => isExternalAppBusy(toExternalAppStatus(item.status)));
	return anyBusy ? externalAppListPollIntervalMs : false;
}

/** The full instance: manifest snapshot, variables and published ports. The hub hook supplies a poll only when down. */
export function useExternalAppInstance(instanceId: string | undefined, options: FeedOptions = {}) {
	return useQuery({
		...withResponseValidation(getExternalAppInstanceOptions({ path: { instanceId: instanceId ?? "" } })),
		enabled: (options.enabled ?? true) && Boolean(instanceId),
		refetchInterval: options.pollIntervalMs ?? false,
	});
}

/**
 * The append-only history feed. `afterSequence` is an EXCLUSIVE LOWER bound and the rows come back ascending, so the
 * feed pages FORWARD: "load more" advances it to the last sequence returned. Decreasing it would repeat a page.
 */
export function useExternalAppInstanceEvents(
	instanceId: string | undefined,
	options: FeedOptions & { readonly afterSequence?: number } = {},
) {
	return useQuery({
		...withResponseValidation(
			listExternalAppInstanceEventsOptions({
				path: { instanceId: instanceId ?? "" },
				query: { afterSequence: options.afterSequence ?? 0, limit: externalAppEventsPageSize },
			}),
		),
		enabled: (options.enabled ?? true) && Boolean(instanceId),
		refetchInterval: options.pollIntervalMs ?? false,
	});
}

/**
 * The update preview. Its members are read 1:1 — there is no `manifestVersion` here: the confirm body's
 * `manifestVersion` is `targetManifestVersion` and its `manifestSha256` is this preview's. An application that left
 * the catalog answers 200 with `canUpdate: false, blockedReason: "CatalogMissing"`, not a 404.
 */
export function useExternalAppUpdatePreview(instanceId: string | undefined, enabled: boolean) {
	return useQuery({
		...withResponseValidation(getExternalAppUpdatePreviewOptions({ path: { instanceId: instanceId ?? "" } })),
		enabled: enabled && Boolean(instanceId),
		staleTime: 0,
	});
}

/** Logs are read on demand — the hub carries no log lines — so this is enabled only while the Logs tab is mounted. */
export function useExternalAppInstanceLogs(
	instanceId: string | undefined,
	service: string | undefined,
	tail: number,
	options: FeedOptions = {},
) {
	return useQuery({
		...withResponseValidation(
			getExternalAppInstanceLogsOptions({ path: { instanceId: instanceId ?? "" }, query: { service, tail } }),
		),
		enabled: (options.enabled ?? true) && Boolean(instanceId),
		staleTime: 0,
	});
}

/**
 * Install. The body carries `acceptPermissions: true` and the manifest fingerprint COPIED from the preview the
 * operator was shown — never re-derived — so the node can refuse an install whose disclosure has gone stale.
 */
export function useInstallExternalApp() {
	const queryClient = useQueryClient();
	return useMutation({
		...withResponseValidation(installExternalAppMutation()),
		onSuccess: async () => {
			await queryClient.invalidateQueries({ queryKey: externalAppInvalidationKey(externalAppQueryIds.instances) });
			await queryClient.invalidateQueries({ queryKey: externalAppInvalidationKey(externalAppQueryIds.catalog) });
		},
	});
}

/**
 * Every lifecycle verb echoes the `version` its row rendered as `expectedVersion`; a stale one is a 409
 * `ExternalAppVersionConflict`, which invalidates so the next click carries a fresh token. V1 has no idempotency
 * keys — this is the whole concurrency contract.
 *
 * All of them answer 202 with a SUMMARY, which has no manifest, variables or published ports, so the caller re-reads
 * rather than priming the instance cache from the response.
 */
function useInstanceInvalidation(): (instanceId: string | undefined, alsoCatalog?: boolean) => Promise<void> {
	const queryClient = useQueryClient();
	return async (instanceId, alsoCatalog = false) => {
		if (instanceId) {
			await queryClient.invalidateQueries({
				queryKey: externalAppInvalidationKey(externalAppQueryIds.instance, { instanceId }),
			});
		}
		await queryClient.invalidateQueries({ queryKey: externalAppInvalidationKey(externalAppQueryIds.instances) });
		if (alsoCatalog) {
			await queryClient.invalidateQueries({ queryKey: externalAppInvalidationKey(externalAppQueryIds.catalog) });
		}
	};
}

/**
 * The re-read a stale-row conflict forces. Both queries, never just the detail: the installed table renders its own
 * `version` and would otherwise serve the rejected token to the next click for the whole of its 30-second window.
 */
export function invalidateExternalAppInstance(queryClient: QueryClient, instanceId: string): void {
	queryClient
		.invalidateQueries({ queryKey: externalAppInvalidationKey(externalAppQueryIds.instance, { instanceId }) })
		.catch(() => undefined);
	queryClient.invalidateQueries({ queryKey: externalAppInvalidationKey(externalAppQueryIds.instances) }).catch(() => undefined);
}

export function useStartExternalApp() {
	const invalidate = useInstanceInvalidation();
	return useMutation({
		...withResponseValidation(startExternalAppMutation()),
		onSuccess: (_data, variables) => invalidate(variables.path?.instanceId),
	});
}

export function useStopExternalApp() {
	const invalidate = useInstanceInvalidation();
	return useMutation({
		...withResponseValidation(stopExternalAppMutation()),
		onSuccess: (_data, variables) => invalidate(variables.path?.instanceId),
	});
}

export function useRestartExternalApp() {
	const invalidate = useInstanceInvalidation();
	return useMutation({
		...withResponseValidation(restartExternalAppMutation()),
		onSuccess: (_data, variables) => invalidate(variables.path?.instanceId),
	});
}

/** An update is an install against a different manifest: same fingerprint echo, same acceptance, plus the row version. */
export function useUpdateExternalApp() {
	const invalidate = useInstanceInvalidation();
	return useMutation({
		...withResponseValidation(updateExternalAppMutation()),
		onSuccess: (_data, variables) => invalidate(variables.path?.instanceId),
	});
}

export function useResetExternalApp() {
	const invalidate = useInstanceInvalidation();
	return useMutation({
		...withResponseValidation(resetExternalAppMutation()),
		onSuccess: (_data, variables) => invalidate(variables.path?.instanceId),
	});
}

/** Uninstall also drops the catalog: the application's card goes back to offering Install. */
export function useUninstallExternalApp() {
	const invalidate = useInstanceInvalidation();
	return useMutation({
		...withResponseValidation(uninstallExternalAppMutation()),
		onSuccess: (_data, variables) => invalidate(variables.path?.instanceId, true),
	});
}

/**
 * Cancel targets the OPERATION, not the row, so it carries no `expectedVersion` and answers a bodyless 202: the
 * operation settles through its own `finally`, which is why the settled state is read off the hub or a re-read.
 */
export function useCancelExternalAppOperation() {
	const invalidate = useInstanceInvalidation();
	return useMutation({
		...withResponseValidation(cancelExternalAppOperationMutation()),
		onSuccess: (_data, variables) => invalidate(variables.path?.instanceId),
	});
}

/**
 * Saving the settings of a stopped instance. `expectedVersion` is required and echoes the `version` the tab rendered;
 * masked secrets round-trip as the sentinel, which the node reads as "keep what is stored".
 */
export function useUpdateExternalAppVariables() {
	const invalidate = useInstanceInvalidation();
	return useMutation({
		...withResponseValidation(updateExternalAppVariablesMutation()),
		// The LIST carries a `version` too, and a save bumps it. Dropping only the detail left the installed table
		// serving the pre-save token for its whole poll window, which Start then echoed into a 409.
		onSuccess: (_data, variables) => invalidate(variables.path?.instanceId),
	});
}
