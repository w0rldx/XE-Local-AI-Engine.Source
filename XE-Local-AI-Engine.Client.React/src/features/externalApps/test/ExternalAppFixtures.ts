// The feature's one fixture file. Per-DTO builders rather than per-route, the `DevWorkflowFixtures.ts` shape: the
// instance payload backs several test files from one builder, parameterised by status.
//
// Every builder returns the WIRE shape (all fields optional, as hey-api types them from the OpenAPI document), so a
// test can spread overrides in without fighting a stricter local type than the server actually promises. The update
// preview spells its members exactly, so a server-side rename breaks compilation rather than a rendering.

import type {
	ExternalAppCatalogResponse,
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
	ExternalAppSummaryView,
	ExternalAppUpdatePreview,
	ExternalAppVariableView,
	ListExternalAppInstanceEventsResponse,
	ListExternalAppInstancesResponse,
} from "@/features/externalApps/models/ExternalAppModels";

export const externalAppTestIds = {
	instance: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
	otherInstance: "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
	application: "ntfy",
	daemon: "MJ7X:QWER:TY12:3456",
} as const;

export function externalAppPermissions(overrides: Partial<ExternalAppPermissionsView> = {}): ExternalAppPermissionsView {
	return { internet: true, localNetwork: false, hostFiles: "none", gpu: "none", ...overrides };
}

/**
 * Two services on purpose: `web` holds a capability and a published port `worker` does not, which is the case an
 * application-level permission block hides and the per-part block exists to show.
 *
 * The four application-level aggregates are the SERVER's, ordinal-sorted, and the panel renders them rather than
 * unioning `services` itself — so they are set here independently of the map, which is what lets a test tell the two
 * apart.
 */
export function externalAppEffectivePermissions(
	overrides: Partial<ExternalAppEffectivePermissionsView> = {},
): ExternalAppEffectivePermissionsView {
	return {
		internet: true,
		localNetwork: false,
		hostFiles: "none",
		gpu: "none",
		capabilities: ["NET_BIND_SERVICE"],
		writableRootFilesystem: false,
		publishedPorts: ["web:80"],
		extraHosts: [],
		services: {
			web: {
				capabilities: ["NET_BIND_SERVICE"],
				writableRootFilesystem: false,
				publishedPorts: ["web:80"],
				extraHosts: [],
			},
			worker: { capabilities: [], writableRootFilesystem: false, publishedPorts: [], extraHosts: [] },
		},
		...overrides,
	};
}

export function externalAppVariable(overrides: Partial<ExternalAppVariableView> = {}): ExternalAppVariableView {
	return {
		name: "baseUrl",
		label: "Base URL",
		description: "Where the application tells clients it lives.",
		type: "string",
		required: true,
		default: "http://127.0.0.1",
		allowedValues: null,
		advanced: false,
		validation: null,
		...overrides,
	};
}

export function externalAppManifest(overrides: Partial<ExternalAppManifestView> = {}): ExternalAppManifestView {
	return {
		id: externalAppTestIds.application,
		manifestVersion: 3,
		displayName: "ntfy",
		summary: "Send yourself push notifications from anything on this computer.",
		description: "A small publish/subscribe notification server.",
		homepage: "https://example.invalid/ntfy",
		license: "Apache-2.0",
		trust: "Community",
		testedVersion: "2.11.0",
		requires: [],
		permissions: externalAppPermissions(),
		resources: { minimumMemoryMb: 128, recommendedMemoryMb: 256, cpuHint: 1, pidsLimit: 128 },
		services: [],
		variables: [externalAppVariable()],
		...overrides,
	};
}

export function externalAppSummary(overrides: Partial<ExternalAppSummaryView> = {}): ExternalAppSummaryView {
	return {
		id: externalAppTestIds.application,
		manifestVersion: 3,
		displayName: "ntfy",
		summary: "Send yourself push notifications from anything on this computer.",
		homepage: "https://example.invalid/ntfy",
		license: "Apache-2.0",
		trust: "Community",
		testedVersion: "2.11.0",
		requires: [],
		permissions: externalAppPermissions(),
		resources: { minimumMemoryMb: 128, recommendedMemoryMb: 256, cpuHint: 1, pidsLimit: 128 },
		installedInstanceId: null,
		installedStatus: null,
		...overrides,
	};
}

export function externalAppCatalog(overrides: Partial<ExternalAppCatalogResponse> = {}): ExternalAppCatalogResponse {
	return {
		schemaVersion: 1,
		generatedAtUtc: 1_700_000_000_000,
		fetchedAtUtc: 1_700_000_000_000,
		fromBundledSeed: false,
		refreshFailureMessage: null,
		lastRefreshFailure: null,
		applications: [externalAppSummary()],
		...overrides,
	};
}

export function externalAppRuntime(overrides: Partial<ExternalAppRuntimeResponse> = {}): ExternalAppRuntimeResponse {
	return {
		// The node's own spellings: `ContainerRuntimeResolver.DockerProvider` is lower-case `docker`, and the mapper
		// stringifies the `DockerDaemonEndpointSource` MEMBER NAME, so a socket at /var/run/docker.sock reports
		// `DefaultUnixSocket`. A fixture with "Docker"/"Default" tests values no node ever emits.
		provider: "docker",
		status: "Ready",
		available: true,
		ready: true,
		message: "",
		requiresOperatorConfirmation: false,
		endpoint: "unix:///var/run/docker.sock",
		endpointSource: "DefaultUnixSocket",
		observedDaemon: {
			daemonId: externalAppTestIds.daemon,
			serverVersion: "27.1.1",
			endpoint: "unix:///var/run/docker.sock",
			confirmedAtUtc: 1_700_000_000_000,
		},
		pinnedDaemon: null,
		capabilities: {
			containers: true,
			networks: true,
			bindStorage: true,
			loopbackPortPublishing: true,
			healthChecks: true,
			restartPolicies: true,
			logs: true,
			imagePull: true,
			gpuDevices: false,
		},
		foreignInstallContainers: 0,
		...overrides,
	};
}

export function externalAppResourceCheck(overrides: Partial<ExternalAppResourceCheckView> = {}): ExternalAppResourceCheckView {
	return {
		satisfied: true,
		failureCategory: null,
		requiredMemoryBytes: 134_217_728,
		availableMemoryBytes: 8_589_934_592,
		requiredDiskBytes: 1_073_741_824,
		availableDiskBytes: 107_374_182_400,
		message: "",
		...overrides,
	};
}

export function externalAppInstallPreview(overrides: Partial<ExternalAppInstallPreview> = {}): ExternalAppInstallPreview {
	return {
		applicationId: externalAppTestIds.application,
		manifestVersion: 3,
		manifestSha256: "a".repeat(64),
		canInstall: true,
		blockedReason: null,
		existingInstanceId: null,
		permissions: externalAppPermissions(),
		effectivePermissions: externalAppEffectivePermissions(),
		variables: [externalAppVariable()],
		runtime: externalAppRuntime(),
		missingCapabilities: [],
		resourceCheck: externalAppResourceCheck(),
		...overrides,
	};
}

/**
 * The two versions differ on purpose: the confirm body's `manifestVersion` is `targetManifestVersion`, and a fixture
 * where the two matched would let the wrong one pass the assertion. There is no `manifestVersion` member here.
 */
export function externalAppUpdatePreview(overrides: Partial<ExternalAppUpdatePreview> = {}): ExternalAppUpdatePreview {
	return {
		applicationId: externalAppTestIds.application,
		instanceId: externalAppTestIds.instance,
		currentManifestVersion: 3,
		targetManifestVersion: 4,
		manifestSha256: "b".repeat(64),
		variables: [externalAppVariable()],
		currentValues: { baseUrl: "http://127.0.0.1" },
		addedPermissions: [],
		effectivePermissions: externalAppEffectivePermissions(),
		resourceVerdict: externalAppResourceCheck(),
		canUpdate: true,
		blockedReason: null,
		...overrides,
	};
}

export function externalAppPublishedPort(overrides: Partial<ExternalAppPublishedPortView> = {}): ExternalAppPublishedPortView {
	return { service: "web", containerPort: 80, hostPort: 45_123, openPath: "/", url: "http://127.0.0.1:45123/", ...overrides };
}

export function externalAppInstance(overrides: Partial<ExternalAppInstanceView> = {}): ExternalAppInstanceView {
	return {
		id: externalAppTestIds.instance,
		applicationId: externalAppTestIds.application,
		displayName: "ntfy",
		manifestVersion: 3,
		status: "Running",
		desiredState: "Running",
		runtimeOverride: null,
		// The stored spelling, which `ExternalAppTestFixture` on the node side pins: lower-case `docker`.
		runtimeProvider: "docker",
		manifest: externalAppManifest(),
		publishedPorts: [externalAppPublishedPort()],
		variables: { baseUrl: "http://127.0.0.1" },
		failureCategory: null,
		failureSummary: null,
		updateAvailable: false,
		availableManifestVersion: null,
		catalogMissing: false,
		installedAtUtc: 1_700_000_000_000,
		startedAtUtc: 1_700_000_100_000,
		stoppedAtUtc: null,
		updatedAtUtc: 1_700_000_100_000,
		lastSequence: 5,
		version: 7,
		...overrides,
	};
}

/** What the list and every lifecycle 202 carry: no manifest, no variables, no published ports. */
export function externalAppInstanceSummary(
	overrides: Partial<ExternalAppInstanceSummaryView> = {},
): ExternalAppInstanceSummaryView {
	return {
		id: externalAppTestIds.instance,
		applicationId: externalAppTestIds.application,
		displayName: "ntfy",
		manifestVersion: 3,
		status: "Running",
		desiredState: "Running",
		failureCategory: null,
		failureSummary: null,
		updateAvailable: false,
		availableManifestVersion: null,
		catalogMissing: false,
		updatedAtUtc: 1_700_000_100_000,
		version: 7,
		...overrides,
	};
}

/**
 * The list envelope. Typed as the generated response, so a row that drifts from `ExternalAppInstanceView` fails tsc in
 * every test that answers this route rather than rendering as `undefined` fields. The list carries the FULL view: the
 * summary shape belongs to the six 202 admission bodies alone.
 */
export function externalAppInstancesResponse(
	items: readonly ExternalAppInstanceView[] = [externalAppInstance()],
): ListExternalAppInstancesResponse {
	return { items: [...items] };
}

/** The event-feed envelope, typed the same way. */
export function externalAppInstanceEventsResponse(
	overrides: Partial<ListExternalAppInstanceEventsResponse> = {},
): ListExternalAppInstanceEventsResponse {
	return { items: [externalAppInstanceEvent()], highestSequence: 1, hasMore: false, ...overrides };
}

export function externalAppInstanceEvent(overrides: Partial<ExternalAppInstanceEventView> = {}): ExternalAppInstanceEventView {
	return { sequence: 1, atUtc: 1_700_000_000_000, kind: "Installed", detailJson: null, ...overrides };
}

export function externalAppInstanceLogs(
	overrides: Partial<ExternalAppInstanceLogsResponse> = {},
): ExternalAppInstanceLogsResponse {
	return { service: "web", text: "listening on :80\n", lineCount: 1, truncated: false, ...overrides };
}
