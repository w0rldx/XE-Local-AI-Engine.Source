// Domain vocabulary for the External Apps surface. The generated client types every enum as a bare `string` (they
// cross the wire as enum NAMES), so these unions are the client-side narrowing — and the place a status typo fails to
// compile instead of falling silently through a colour map. Same shape as `DevWorkflowModels.ts`.
//
// One narrowing shape here, deliberately: a render must pick SOMETHING, so an unknown token falls back to the state
// with no controls rather than putting a raw identifier in front of an operator.

import type { TFunction } from "i18next";

import type {
	XeLocalAiEngineClientEndpointsExternalAppsV1ExternalAppCatalogResponse as ExternalAppCatalogResponse,
	XeLocalAiEngineClientEndpointsExternalAppsV1ExternalAppDaemonView as ExternalAppDaemonView,
	XeLocalAiEngineClientEndpointsExternalAppsV1ExternalAppEffectivePermissionsView as ExternalAppEffectivePermissionsView,
	XeLocalAiEngineClientEndpointsExternalAppsV1ExternalAppInstallPreview as ExternalAppInstallPreview,
	XeLocalAiEngineClientEndpointsExternalAppsV1ExternalAppInstanceEventView as ExternalAppInstanceEventView,
	XeLocalAiEngineClientEndpointsExternalAppsV1ExternalAppInstanceLogsResponse as ExternalAppInstanceLogsResponse,
	XeLocalAiEngineClientEndpointsExternalAppsV1ExternalAppInstanceSummaryView as ExternalAppInstanceSummaryView,
	XeLocalAiEngineClientEndpointsExternalAppsV1ExternalAppInstanceView as ExternalAppInstanceView,
	XeLocalAiEngineClientEndpointsExternalAppsV1ExternalAppManifestView as ExternalAppManifestView,
	XeLocalAiEngineClientEndpointsExternalAppsV1ExternalAppPermissionsView as ExternalAppPermissionsView,
	XeLocalAiEngineClientEndpointsExternalAppsV1ExternalAppPublishedPortView as ExternalAppPublishedPortView,
	XeLocalAiEngineClientEndpointsExternalAppsV1ExternalAppResourceCheckView as ExternalAppResourceCheckView,
	XeLocalAiEngineClientEndpointsExternalAppsV1ExternalAppRuntimeResponse as ExternalAppRuntimeResponse,
	XeLocalAiEngineClientEndpointsExternalAppsV1ExternalAppServicePermissionsView as ExternalAppServicePermissionsView,
	XeLocalAiEngineClientEndpointsExternalAppsV1ExternalAppSummaryView as ExternalAppSummaryView,
	XeLocalAiEngineClientEndpointsExternalAppsV1ExternalAppUpdatePreview as ExternalAppUpdatePreview,
	XeLocalAiEngineClientEndpointsExternalAppsV1ExternalAppVariableView as ExternalAppVariableView,
	XeLocalAiEngineClientEndpointsExternalAppsV1ListExternalAppInstanceEventsResponse as ListExternalAppInstanceEventsResponse,
	XeLocalAiEngineClientEndpointsExternalAppsV1ListExternalAppInstancesResponse as ListExternalAppInstancesResponse,
} from "@/core/api/generated/types.gen";

export type {
	ExternalAppCatalogResponse,
	ExternalAppDaemonView,
	ExternalAppEffectivePermissionsView,
	ExternalAppInstallPreview,
	ExternalAppInstanceEventView,
	ExternalAppInstanceLogsResponse,
	ExternalAppInstanceSummaryView,
	ExternalAppInstanceView,
	ExternalAppManifestView,
	ExternalAppPermissionsView,
	ExternalAppPublishedPortView,
	ExternalAppResourceCheckView,
	ExternalAppRuntimeResponse,
	ExternalAppServicePermissionsView,
	ExternalAppSummaryView,
	ExternalAppUpdatePreview,
	ExternalAppVariableView,
	ListExternalAppInstanceEventsResponse,
	ListExternalAppInstancesResponse,
};

/**
 * One row of `GET external-apps/instances`, which carries the FULL view: manifest, variables and published ports
 * included. Aliased here and nowhere else. Nothing may re-read a row with a per-instance GET to reach a field — that
 * fan-out is exactly what the widened list removed. The six 202 admission bodies still answer
 * `ExternalAppInstanceSummaryView`, so a card rendered from one still re-reads or waits for the hub ping.
 */
export type ExternalAppInstanceListItem = ExternalAppInstanceView;

/** The ten instance statuses. `StoppedUnexpectedly` is a settled state that needs a human, not an in-flight one. */
export const externalAppStatuses = [
	"Installing",
	"Stopped",
	"Starting",
	"Running",
	"Stopping",
	"Updating",
	"Resetting",
	"Uninstalling",
	"Failed",
	"StoppedUnexpectedly",
] as const;
export type ExternalAppStatus = (typeof externalAppStatuses)[number];

/** The thirteen failure categories, each with its own sentence under `pages.externalApps.failure.*`. */
export const externalAppFailureCategories = [
	"RuntimeUnavailable",
	"RuntimeIncompatible",
	"GpuNotSupported",
	"InsufficientMemory",
	"InsufficientDisk",
	"ImagePullFailed",
	"ConfigurationMissing",
	"PolicyViolation",
	"PortUnavailable",
	"HealthCheckFailed",
	"StoppedUnexpectedly",
	"StorageError",
	"Unknown",
] as const;
export type ExternalAppFailureCategory = (typeof externalAppFailureCategories)[number];

/** The seven container-runtime statuses. Only `Ready` means the runtime can be used. */
export const containerRuntimeStatuses = [
	"Ready",
	"DaemonUnreachable",
	"PermissionDenied",
	"ApiVersionTooOld",
	"DaemonIdentityChanged",
	"NotConfigured",
	"ProbeFailed",
] as const;
export type ContainerRuntimeStatus = (typeof containerRuntimeStatuses)[number];

/**
 * The sixteen event kinds of the REST history feed, PascalCase as `Enum.ToString()` writes them. The HUB ping carries
 * the same enum lowerCamelCase — the hub hook never reads it, so the two casings never meet.
 */
export const externalAppEventKinds = [
	"Installed",
	"StartRequested",
	"Started",
	"StopRequested",
	"Stopped",
	"Restarted",
	"UpdateRequested",
	"Updated",
	"ResetRequested",
	"Reset",
	"UninstallRequested",
	"Uninstalled",
	"Failed",
	"RestoredOnBoot",
	"StoppedUnexpectedly",
	"PermissionAccepted",
] as const;
export type ExternalAppEventKind = (typeof externalAppEventKinds)[number];

/**
 * The closed `addedPermissions` vocabulary the server diffs per service. A name outside it highlights nothing and
 * renders nothing: the SPA never invents a sentence for a grant it has no wording for.
 */
export const externalAppPermissionNames = [
	"internet",
	"localNetwork",
	"hostFiles",
	"gpu",
	"capabilities",
	"writableRootFilesystem",
	"publishedPorts",
	"extraHosts",
] as const;
export type ExternalAppPermissionName = (typeof externalAppPermissionNames)[number];

/**
 * The nine runtime-capability names a manifest may put in `requires[]`, mirroring `ContainerRuntimeCapabilities.Names`
 * (`XE-Local-AI-Engine.Client.Application/Services/Containers/ContainerRuntimeCapabilities.cs`) in its order. They
 * cross the wire as the flag names themselves, so the SPA needs a sentence per name; camel-splitting one into
 * "loopback port publishing" is a wire identifier dressed up as English.
 */
const externalAppCapabilityNames = [
	"containers",
	"networks",
	"bindStorage",
	"loopbackPortPublishing",
	"healthChecks",
	"restartPolicies",
	"logs",
	"imagePull",
	"gpuDevices",
] as const;

/**
 * A capability name in an operator's words. A name outside the nine is rendered verbatim: a manifest written against a
 * later schema may ask for one this build has no sentence for, and the raw name is more honest than silence.
 */
export function externalAppCapabilityLabel(name: string, t: TFunction): string {
	return (externalAppCapabilityNames as readonly string[]).includes(name)
		? t(`pages.externalApps.runtime.capability.${name}`)
		: name;
}

/**
 * The secret sentinel, which MUST equal `ExternalAppVariableMask.Value` on the node
 * (`XE-Local-AI-Engine.Client.Application/Services/ExternalApps/ExternalAppVariableMask.cs`). The two sides cannot
 * share a symbol, so both assert the literal: a mismatch would silently overwrite a stored secret with the mask.
 * Declared here rather than imported from `features/customTools` — that feature's sentinel has a different value.
 */
export const EXTERNAL_APP_SECRET_SENTINEL = "__XE_EXTERNAL_APP_SECRET_UNCHANGED__";

/** The six in-flight statuses: spinner on the badge, lifecycle actions disabled, the instance list polling. */
const busyStatuses: ReadonlySet<string> = new Set<ExternalAppStatus>([
	"Installing",
	"Starting",
	"Stopping",
	"Updating",
	"Resetting",
	"Uninstalling",
]);

/** The three long operations a Cancel can reach. Start/Stop/Restart settle on their own and are not cancellable. */
const cancellableStatuses: ReadonlySet<string> = new Set<ExternalAppStatus>(["Installing", "Updating", "Resetting"]);

function narrow<T extends string>(members: readonly T[], value: string | null | undefined, fallback: T): T {
	return members.includes(value as T) ? (value as T) : fallback;
}

/** Unknown → `Stopped`: the state with no spinner and the smallest action set, never a raw token. */
export function toExternalAppStatus(value: string | null | undefined): ExternalAppStatus {
	return narrow(externalAppStatuses, value, "Stopped");
}

/** Unknown → `Unknown`, which is a real member of the enum and already has its own sentence. */
export function toExternalAppFailureCategory(value: string | null | undefined): ExternalAppFailureCategory {
	return narrow(externalAppFailureCategories, value, "Unknown");
}

/** Unknown → `ProbeFailed`: "the check did not finish" is the only honest reading of a status this build cannot name. */
export function toContainerRuntimeStatus(value: string | null | undefined): ContainerRuntimeStatus {
	return narrow(containerRuntimeStatuses, value, "ProbeFailed");
}

/**
 * Unknown → the literal `"Unknown"`, which is NOT one of the sixteen kinds: the history row renders
 * `events.kind.unknown` rather than a raw identifier a newer server invented.
 */
export function toExternalAppEventKind(value: string | null | undefined): ExternalAppEventKind | "Unknown" {
	return narrow([...externalAppEventKinds, "Unknown"] as const, value, "Unknown");
}

export function isExternalAppBusy(status: ExternalAppStatus): boolean {
	return busyStatuses.has(status);
}

export function isExternalAppCancellable(status: ExternalAppStatus): boolean {
	return cancellableStatuses.has(status);
}
