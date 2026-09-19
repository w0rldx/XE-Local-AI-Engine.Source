// @vitest-environment jsdom

// The BODY SHAPES, asserted against what the hooks actually put on the wire. A dropped `expectedVersion` or
// `acceptPermissions` is not a rendering bug: it is a concurrency contract or a consent record silently lost, and the
// server's refusal arrives as a toast nobody connects to the missing field. Every request here is captured by MSW and
// read back, rather than trusted from the hook's source.

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import { HttpResponse, http } from "msw";
import type { ReactNode } from "react";
import { describe, expect, it } from "vitest";

import type {
	ExternalAppInstanceSummaryView,
	ExternalAppInstanceView,
	ExternalAppRuntimeResponse,
} from "@/features/externalApps/models/ExternalAppModels";
import {
	externalAppEventsPageSize,
	externalAppInvalidationKey,
	externalAppListPollInterval,
	externalAppListPollIntervalMs,
	externalAppQueryIds,
	useCancelExternalAppOperation,
	useExternalAppApplication,
	useExternalAppCatalog,
	useExternalAppInstallPreview,
	useExternalAppInstance,
	useExternalAppInstanceEvents,
	useExternalAppInstanceLogs,
	useExternalAppInstances,
	useExternalAppRuntime,
	useExternalAppUpdatePreview,
	useInstallExternalApp,
	useRefreshExternalAppCatalog,
	useRefreshExternalAppRuntime,
	useResetExternalApp,
	useRestartExternalApp,
	useStartExternalApp,
	useStopExternalApp,
	useUninstallExternalApp,
	useUpdateExternalApp,
	useUpdateExternalAppVariables,
} from "@/features/externalApps/queries/useExternalApps";
import {
	externalAppCatalog,
	externalAppInstallPreview,
	externalAppInstance,
	externalAppInstanceEventsResponse,
	externalAppInstanceLogs,
	externalAppInstancesResponse,
	externalAppInstanceSummary,
	externalAppManifest,
	externalAppRuntime,
	externalAppTestIds,
	externalAppUpdatePreview,
} from "@/features/externalApps/test/ExternalAppFixtures";
import { jsonRoute, localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

const instanceId = externalAppTestIds.instance;

interface CapturedRequest {
	readonly url: string;
	readonly body: unknown;
}

/**
 * Records every request that reaches `path`, and answers with `response`. The response is typed as one of the wire
 * shapes these routes answer — the 202 admission body is a summary, the variables PUT a full view, the runtime
 * refresh its own runtime projection — so a fixture that drifts from the generated types fails tsc here rather than
 * being accepted as an opaque JSON blob. `undefined` is the bodyless 202 the cancel route answers.
 */
function capture(
	method: "post" | "put" | "delete",
	path: string,
	response: ExternalAppInstanceSummaryView | ExternalAppInstanceView | ExternalAppRuntimeResponse | undefined,
): CapturedRequest[] {
	const requests: CapturedRequest[] = [];
	server.use(
		http[method](localApiPath(path), async ({ request }) => {
			const text = await request.text();
			requests.push({ url: request.url, body: text.length > 0 ? JSON.parse(text) : undefined });
			return response === undefined ? new HttpResponse(null, { status: 202 }) : HttpResponse.json(response);
		}),
	);
	return requests;
}

/** Counts the reads of the instance detail and of the installed list, which is what an invalidation is observed by. */
function countingReads(): { instance: number; list: number } {
	const reads = { instance: 0, list: 0 };
	server.use(
		http.get(localApiPath(`external-apps/instances/${instanceId}`), () => {
			reads.instance += 1;
			return HttpResponse.json(externalAppInstance());
		}),
		http.get(localApiPath("external-apps/instances"), () => {
			reads.list += 1;
			return HttpResponse.json(externalAppInstancesResponse([externalAppInstance({ status: "Stopped" })]));
		}),
	);
	return reads;
}

function harness(): { queryClient: QueryClient; wrapper: ({ children }: { children: ReactNode }) => ReactNode } {
	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
	return {
		queryClient,
		wrapper: ({ children }) => <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>,
	};
}

describe("externalAppInvalidationKey", () => {
	it("matches every cached variant of an endpoint without a path", () => {
		// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
		expect(externalAppInvalidationKey(externalAppQueryIds.instances)).toEqual([{ _id: "listExternalAppInstances" }]);
	});

	it("addresses one instance when given a path", () => {
		expect(externalAppInvalidationKey(externalAppQueryIds.instance, { instanceId })).toEqual([
			// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
			{ _id: "getExternalAppInstance", path: { instanceId } },
		]);
	});
});

describe("externalAppListPollInterval", () => {
	it("polls while a listed instance is busy", () => {
		const items = [externalAppInstanceSummary({ status: "Running" }), externalAppInstanceSummary({ status: "Updating" })];

		expect(externalAppListPollInterval(items)).toBe(externalAppListPollIntervalMs);
	});

	// A timer on rows that cannot change on their own burns for nothing — an instance-scoped hub cannot feed a list,
	// which is the only reason this list polls at all.
	it("does not poll once every instance is settled", () => {
		expect(externalAppListPollInterval([externalAppInstanceSummary({ status: "Stopped" })])).toBe(false);
		expect(externalAppListPollInterval([])).toBe(false);
		expect(externalAppListPollInterval(undefined)).toBe(false);
	});
});

describe("lifecycle mutations", () => {
	it("sends the rendered version as expectedVersion on start, stop, restart and reset", async () => {
		const started = capture("post", `external-apps/instances/${instanceId}/start`, externalAppInstanceSummary());
		const stopped = capture("post", `external-apps/instances/${instanceId}/stop`, externalAppInstanceSummary());
		const restarted = capture("post", `external-apps/instances/${instanceId}/restart`, externalAppInstanceSummary());
		const reset = capture("post", `external-apps/instances/${instanceId}/reset`, externalAppInstanceSummary());
		const { wrapper } = harness();

		const { result } = renderHook(
			() => ({
				start: useStartExternalApp(),
				stop: useStopExternalApp(),
				restart: useRestartExternalApp(),
				reset: useResetExternalApp(),
			}),
			{ wrapper },
		);
		result.current.start.mutate({ path: { instanceId }, body: { expectedVersion: 7 } });
		result.current.stop.mutate({ path: { instanceId }, body: { expectedVersion: 7 } });
		result.current.restart.mutate({ path: { instanceId }, body: { expectedVersion: 7 } });
		result.current.reset.mutate({ path: { instanceId }, body: { expectedVersion: 7 } });

		await waitFor(() => expect(reset).toHaveLength(1));
		expect([started[0]?.body, stopped[0]?.body, restarted[0]?.body, reset[0]?.body]).toEqual([
			{ expectedVersion: 7 },
			{ expectedVersion: 7 },
			{ expectedVersion: 7 },
			{ expectedVersion: 7 },
		]);
	});

	// Uninstall carries the token as a QUERY parameter — it has no body. The generated client types it optional, which
	// the server does not: a missing token is a 400, so the call site always sends the version its row rendered.
	it("sends expectedVersion on uninstall as a query parameter", async () => {
		const requests = capture("delete", `external-apps/instances/${instanceId}`, externalAppInstanceSummary());
		const { wrapper } = harness();

		const { result } = renderHook(() => useUninstallExternalApp(), { wrapper });
		result.current.mutate({ path: { instanceId }, query: { expectedVersion: 7 } });

		await waitFor(() => expect(requests).toHaveLength(1));
		expect(new URL(requests[0]?.url ?? "").searchParams.get("expectedVersion")).toBe("7");
		expect(requests[0]?.body).toBeUndefined();
	});

	// Cancel targets the OPERATION, not the row: a version token would refuse the one action an operator has while a
	// multi-gigabyte pull is running.
	it("posts a bodyless cancel to the cancel route", async () => {
		const requests = capture("post", `external-apps/instances/${instanceId}/cancel`, undefined);
		const { wrapper } = harness();

		const { result } = renderHook(() => useCancelExternalAppOperation(), { wrapper });
		result.current.mutate({ path: { instanceId } });

		await waitFor(() => expect(requests).toHaveLength(1));
		expect(requests[0]?.url).toContain(`/instances/${instanceId}/cancel`);
		expect(requests[0]?.body).toBeUndefined();
	});

	// BOTH feeds, and both are mounted here: the list renders its own `version`, so a test that watched only the
	// detail would pass while the installed table went on serving a token the next Start would have refused.
	it("re-reads the instance and the list once a command is accepted", async () => {
		const reads = countingReads();
		server.use(jsonRoute("post", `external-apps/instances/${instanceId}/start`, externalAppInstanceSummary()));
		const { wrapper } = harness();

		const { result } = renderHook(
			() => ({ instance: useExternalAppInstance(instanceId), list: useExternalAppInstances(), start: useStartExternalApp() }),
			{ wrapper },
		);
		await waitFor(() => expect([reads.instance, reads.list]).toEqual([1, 1]));
		result.current.start.mutate({ path: { instanceId }, body: { expectedVersion: 7 } });

		// A 202 carries only a SUMMARY — no manifest, no variables, no ports — so the detail must be re-read rather
		// than primed from the response.
		await waitFor(() => expect([reads.instance, reads.list]).toEqual([2, 2]));
	});
});

describe("install, update and settings", () => {
	it("sends the acceptance and the preview's fingerprint on install", async () => {
		const requests = capture("post", "external-apps/instances", externalAppInstanceSummary());
		const { wrapper } = harness();

		const { result } = renderHook(() => useInstallExternalApp(), { wrapper });
		result.current.mutate({
			body: {
				applicationId: externalAppTestIds.application,
				manifestVersion: 3,
				manifestSha256: "a".repeat(64),
				variables: { baseUrl: "http://127.0.0.1" },
				acceptPermissions: true,
			},
		});

		await waitFor(() => expect(requests).toHaveLength(1));
		expect(requests[0]?.body).toEqual({
			applicationId: externalAppTestIds.application,
			manifestVersion: 3,
			manifestSha256: "a".repeat(64),
			variables: { baseUrl: "http://127.0.0.1" },
			acceptPermissions: true,
		});
	});

	// `acceptPermissions` is a BOOLEAN, not a list of names: the dialog showed the diff before the call, and the
	// server's validator rejects `false`.
	it("sends the target fingerprint, the variables and the row version on update", async () => {
		const requests = capture("post", `external-apps/instances/${instanceId}/update`, externalAppInstanceSummary());
		const { wrapper } = harness();

		const { result } = renderHook(() => useUpdateExternalApp(), { wrapper });
		result.current.mutate({
			path: { instanceId },
			body: {
				manifestVersion: 4,
				manifestSha256: "b".repeat(64),
				acceptPermissions: true,
				variables: { baseUrl: "http://127.0.0.1" },
				expectedVersion: 7,
			},
		});

		await waitFor(() => expect(requests).toHaveLength(1));
		expect(requests[0]?.body).toEqual({
			manifestVersion: 4,
			manifestSha256: "b".repeat(64),
			acceptPermissions: true,
			variables: { baseUrl: "http://127.0.0.1" },
			expectedVersion: 7,
		});
	});

	it("sends the rendered version with a settings save", async () => {
		const requests = capture("put", `external-apps/instances/${instanceId}/variables`, externalAppInstance());
		const { wrapper } = harness();

		const { result } = renderHook(() => useUpdateExternalAppVariables(), { wrapper });
		result.current.mutate({ path: { instanceId }, body: { variables: { baseUrl: "http://ntfy.local" }, expectedVersion: 7 } });

		await waitFor(() => expect(requests).toHaveLength(1));
		expect(requests[0]?.body).toEqual({ variables: { baseUrl: "http://ntfy.local" }, expectedVersion: 7 });
	});

	// A save bumps the row's `version`, and the installed table renders one of its own. Invalidating only the detail
	// left that table serving the pre-save token for its whole 30-second window, which Start then echoed into a 409.
	it("re-reads the instance and the list once settings are saved", async () => {
		const reads = countingReads();
		server.use(jsonRoute("put", `external-apps/instances/${instanceId}/variables`, externalAppInstance({ version: 8 })));
		const { wrapper } = harness();

		const { result } = renderHook(
			() => ({
				instance: useExternalAppInstance(instanceId),
				list: useExternalAppInstances(),
				save: useUpdateExternalAppVariables(),
			}),
			{ wrapper },
		);
		await waitFor(() => expect([reads.instance, reads.list]).toEqual([1, 1]));
		result.current.save.mutate({
			path: { instanceId },
			body: { variables: { baseUrl: "http://ntfy.local" }, expectedVersion: 7 },
		});

		await waitFor(() => expect([reads.instance, reads.list]).toEqual([2, 2]));
	});

	// The 400 says the daemon the card OFFERED is not the observed one any more. Toasting alone left the card holding
	// the rejected id, so the next click sent it again and was refused again.
	it("re-reads the runtime when a trust request is refused because the daemon changed", async () => {
		let runtimeReads = 0;
		server.use(
			http.get(localApiPath("external-apps/runtime"), () => {
				runtimeReads += 1;
				return HttpResponse.json(externalAppRuntime());
			}),
			http.post(localApiPath("external-apps/runtime/refresh"), () =>
				HttpResponse.json({ type: "about:blank", title: "Bad Request", status: 400, detail: "daemon changed" }, { status: 400 }),
			),
		);
		const { wrapper } = harness();

		const { result } = renderHook(() => ({ runtime: useExternalAppRuntime(), refresh: useRefreshExternalAppRuntime() }), {
			wrapper,
		});
		await waitFor(() => expect(runtimeReads).toBe(1));
		result.current.refresh.mutate({ body: { acknowledgeDaemonId: externalAppTestIds.daemon } });

		await waitFor(() => expect(result.current.refresh.isError).toBe(true));
		await waitFor(() => expect(runtimeReads).toBe(2));
	});

	// The operator approves the identity they were SHOWN. A daemon that changed again between render and click is
	// refused with a 400 rather than silently trusted, which only works if the id travels as a string.
	it("sends the observed daemon id as a string when the runtime is trusted", async () => {
		const requests = capture("post", "external-apps/runtime/refresh", externalAppRuntime());
		const { wrapper } = harness();

		const { result } = renderHook(() => useRefreshExternalAppRuntime(), { wrapper });
		result.current.mutate({ body: { acknowledgeDaemonId: externalAppTestIds.daemon } });

		await waitFor(() => expect(requests).toHaveLength(1));
		expect(requests[0]?.body).toEqual({ acknowledgeDaemonId: externalAppTestIds.daemon });
	});
});

describe("bounded feeds", () => {
	it("reads the history ascending from an exclusive lower bound, one page at a time", async () => {
		const urls: string[] = [];
		server.use(
			http.get(localApiPath(`external-apps/instances/${instanceId}/events`), ({ request }) => {
				urls.push(request.url);
				return HttpResponse.json(externalAppInstanceEventsResponse());
			}),
		);
		const { wrapper } = harness();

		const { result } = renderHook(() => useExternalAppInstanceEvents(instanceId), { wrapper });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));
		const query = new URL(urls[0] ?? "").searchParams;
		expect([query.get("afterSequence"), query.get("limit")]).toEqual(["0", "200"]);
		// The page size is also the hub's replay cap: one page is exactly one full replay window.
		expect(externalAppEventsPageSize).toBe(200);
	});

	it("advances the lower bound when more history is asked for", async () => {
		const urls: string[] = [];
		server.use(
			http.get(localApiPath(`external-apps/instances/${instanceId}/events`), ({ request }) => {
				urls.push(request.url);
				return HttpResponse.json(externalAppInstanceEventsResponse({ items: [], highestSequence: 12 }));
			}),
		);
		const { wrapper } = harness();

		const { result } = renderHook(() => useExternalAppInstanceEvents(instanceId, { afterSequence: 12 }), { wrapper });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));
		expect(new URL(urls[0] ?? "").searchParams.get("afterSequence")).toBe("12");
	});

	it("reads logs for the chosen service and tail bound", async () => {
		const urls: string[] = [];
		server.use(
			http.get(localApiPath(`external-apps/instances/${instanceId}/logs`), ({ request }) => {
				urls.push(request.url);
				return HttpResponse.json(externalAppInstanceLogs());
			}),
		);
		const { wrapper } = harness();

		const { result } = renderHook(() => useExternalAppInstanceLogs(instanceId, "web", 500), { wrapper });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));
		const query = new URL(urls[0] ?? "").searchParams;
		expect([query.get("service"), query.get("tail")]).toEqual(["web", "500"]);
	});

	it("asks for nothing until an instance id is known", () => {
		const { wrapper } = harness();

		const { result } = renderHook(() => useExternalAppInstance(undefined), { wrapper });

		expect(result.current.fetchStatus).toBe("idle");
	});
});

describe("read hooks", () => {
	// One mount per read hook: the generated `*Options()` builders are the only place a route can be got wrong, and a
	// wrong one shows up as an unhandled MSW request naming the URL rather than as an empty page.
	it("each target their own route", async () => {
		server.use(
			jsonRoute("get", "external-apps/runtime", externalAppRuntime()),
			jsonRoute("get", "external-apps/catalog", externalAppCatalog()),
			jsonRoute("get", `external-apps/catalog/${externalAppTestIds.application}`, externalAppManifest()),
			jsonRoute("get", `external-apps/catalog/${externalAppTestIds.application}/install-preview`, externalAppInstallPreview()),
			jsonRoute("get", "external-apps/instances", externalAppInstancesResponse()),
			jsonRoute("get", `external-apps/instances/${instanceId}/update-preview`, externalAppUpdatePreview()),
		);
		const { wrapper } = harness();

		const { result } = renderHook(
			() => ({
				runtime: useExternalAppRuntime(),
				catalog: useExternalAppCatalog(),
				application: useExternalAppApplication(externalAppTestIds.application),
				installPreview: useExternalAppInstallPreview(externalAppTestIds.application, true),
				instances: useExternalAppInstances(),
				updatePreview: useExternalAppUpdatePreview(instanceId, true),
			}),
			{ wrapper },
		);

		await waitFor(() => expect(Object.values(result.current).every((query) => query.isSuccess)).toBe(true));
		expect(result.current.updatePreview.data?.targetManifestVersion).toBe(4);
	});

	it("holds a preview back until its dialog opens", () => {
		const { wrapper } = harness();

		const { result } = renderHook(() => useExternalAppInstallPreview(externalAppTestIds.application, false), { wrapper });

		expect(result.current.fetchStatus).toBe("idle");
	});

	// A refresh can rewrite every manifest, so the single-application entries have to go with the list; leaving them
	// would show a stale tested version next to a freshly refreshed card.
	it("drops the catalog and every single-application entry when the catalog is refreshed", async () => {
		let catalogReads = 0;
		server.use(
			http.get(localApiPath("external-apps/catalog"), () => {
				catalogReads += 1;
				return HttpResponse.json(externalAppCatalog());
			}),
			jsonRoute("post", "external-apps/catalog/refresh", externalAppCatalog()),
		);
		const { wrapper } = harness();

		const { result } = renderHook(() => ({ catalog: useExternalAppCatalog(), refresh: useRefreshExternalAppCatalog() }), {
			wrapper,
		});
		await waitFor(() => expect(catalogReads).toBe(1));
		result.current.refresh.mutate({});

		await waitFor(() => expect(catalogReads).toBe(2));
	});
});
