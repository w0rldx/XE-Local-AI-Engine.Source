// @vitest-environment jsdom

// Everything on this page reads from the INSTANCE, never the catalog — the tested version included, which lives
// inside `instance.manifest`. The catalog handler here fails the test if it is ever called: an application the
// catalog dropped must still render completely.

import { fireEvent, screen, waitFor } from "@testing-library/react";
import { HttpResponse, http } from "msw";
import { describe, expect, it, vi } from "vitest";

import { ConfirmProvider } from "@/core/ui/components/ConfirmProvider/ConfirmProvider";
import { EXTERNAL_APP_SECRET_SENTINEL } from "@/features/externalApps/models/ExternalAppModels";
import { ExternalAppInstanceDetailPage } from "@/features/externalApps/pages/ExternalAppInstanceDetailPage";
import {
	externalAppInstance,
	externalAppInstanceLogs,
	externalAppManifest,
	externalAppRuntime,
	externalAppTestIds,
	externalAppVariable,
} from "@/features/externalApps/test/ExternalAppFixtures";
import { jsonRoute, localApiPath, problemDetailsRoute } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

const navigate = vi.hoisted(() => vi.fn());
const toastMock = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn(), info: vi.fn(), warning: vi.fn() }));
vi.mock("@/core/ui/notifications/Toast", () => ({ toast: toastMock }));
vi.mock("@tanstack/react-router", async (importOriginal) => ({
	...(await importOriginal<typeof import("@tanstack/react-router")>()),
	useNavigate: () => navigate,
}));
// The hub opens a real SignalR connection otherwise; the hook has its own wire-contract test.
vi.mock("@/features/externalApps/hooks/useExternalAppHub", () => ({
	useExternalAppHub: () => ({ connectionState: "connected", watermark: 0, pullProgress: {} }),
	// biome-ignore lint/style/useNamingConvention: the module's own constant name.
	EXTERNAL_APP_POLL_INTERVAL_MS: 3000,
}));

setupMswServer();

const instanceId = externalAppTestIds.instance;
const instancePath = `external-apps/instances/${instanceId}`;

function baseRoutes(instance = externalAppInstance(), runtime = externalAppRuntime()) {
	return [
		jsonRoute("get", instancePath, instance),
		jsonRoute("get", "external-apps/runtime", runtime),
		jsonRoute("get", `${instancePath}/events`, { items: [], highestSequence: 0, hasMore: false }),
		jsonRoute("get", `${instancePath}/logs`, externalAppInstanceLogs()),
		// A catalog read here is the defect this page exists to avoid.
		http.get(localApiPath("external-apps/catalog"), () => {
			throw new Error("the detail page must not query the catalog");
		}),
	];
}

function renderPage() {
	renderWithProviders(
		<ConfirmProvider>
			<ExternalAppInstanceDetailPage instanceId={instanceId} />
		</ConfirmProvider>,
	);
}

describe("ExternalAppInstanceDetailPage", () => {
	it("renders the overview from the instance's own manifest, with no catalog query in play", async () => {
		server.use(...baseRoutes());
		renderPage();

		await waitFor(() => expect(screen.getByTestId("external-app-detail-overview")).toBeDefined());
		const overview = screen.getByTestId("external-app-detail-overview");
		// `instance.manifest.testedVersion`; there is no top-level member to read it from.
		expect(overview.textContent).toContain("2.11.0");
		expect(overview.textContent).toContain("This computer only");
		expect(overview.textContent).toContain("Managed by XE");
		expect(screen.getByTestId("external-app-detail-address").textContent).toContain("127.0.0.1:45123");
		expect(screen.getByTestId("external-app-permissions")).toBeDefined();
	});

	it("falls back to the pending address line when no port has one yet", async () => {
		server.use(...baseRoutes(externalAppInstance({ status: "Installing", publishedPorts: [] })));
		renderPage();

		await waitFor(() => expect(screen.getByTestId("external-app-detail-address")).toBeDefined());
		expect(screen.getByTestId("external-app-detail-address").textContent).toContain("Assigned when");
	});

	it("banners an unreachable runtime and still renders a transient row as transient", async () => {
		server.use(
			...baseRoutes(externalAppInstance({ status: "Installing" }), externalAppRuntime({ available: false, ready: false })),
		);
		renderPage();

		await waitFor(() => expect(screen.getByTestId("external-app-detail-runtime-unavailable")).toBeDefined());
		// Never repainted as failed: reconciliation settles the row once a ready runtime returns.
		expect(screen.getByTestId("external-app-detail-overview").textContent).toContain("Installing");
		expect(screen.queryByTestId("external-app-detail-failure")).toBeNull();
	});

	it("renders the failure sentence for a set failure category", async () => {
		server.use(
			...baseRoutes(externalAppInstance({ status: "Failed", failureCategory: "ImagePullFailed", failureSummary: null })),
		);
		renderPage();

		await waitFor(() => expect(screen.getByTestId("external-app-detail-failure")).toBeDefined());
		expect(screen.getByTestId("external-app-detail-failure").textContent).toContain("could not be downloaded");
	});

	it("offers the update alert naming the available version, with no catalog query mounted", async () => {
		server.use(...baseRoutes(externalAppInstance({ status: "Stopped", updateAvailable: true, availableManifestVersion: 9 })));
		renderPage();

		await waitFor(() => expect(screen.getByTestId("external-app-detail-update-available")).toBeDefined());
		expect(screen.getByTestId("external-app-detail-update-available").textContent).toContain("Version 9");
	});

	it("sends the rendered version as expectedVersion when the settings tab saves", async () => {
		const bodies: unknown[] = [];
		server.use(
			...baseRoutes(externalAppInstance({ status: "Stopped", version: 11 })),
			http.put(localApiPath(`${instancePath}/variables`), async ({ request }) => {
				bodies.push(await request.json());
				return HttpResponse.json(externalAppInstance());
			}),
		);
		renderPage();
		await waitFor(() => expect(screen.getByTestId("external-app-detail-tab-settings")).toBeDefined());

		fireEvent.click(screen.getByTestId("external-app-detail-tab-settings"));
		await waitFor(() => expect(screen.getByTestId("external-app-detail-settings-save")).toBeDefined());
		fireEvent.click(screen.getByTestId("external-app-detail-settings-save"));

		await waitFor(() => expect(bodies).toHaveLength(1));
		expect(bodies[0]).toEqual({ variables: { baseUrl: "http://127.0.0.1" }, expectedVersion: 11 });
	});

	// The success handler replaces every value with the masked instance the node answered with, so anything typed
	// between the click and that answer was silently dropped. The form is locked for the whole flight instead.
	it("locks the settings form while the save is in flight and releases it when the node answers", async () => {
		let release: () => void = () => undefined;
		const answered = new Promise<void>((resolve) => {
			release = resolve;
		});
		server.use(
			...baseRoutes(externalAppInstance({ status: "Stopped", version: 11 })),
			http.put(localApiPath(`${instancePath}/variables`), async () => {
				await answered;
				return HttpResponse.json(externalAppInstance({ status: "Stopped", version: 12 }));
			}),
		);
		renderPage();
		await waitFor(() => expect(screen.getByTestId("external-app-detail-tab-settings")).toBeDefined());
		fireEvent.click(screen.getByTestId("external-app-detail-tab-settings"));
		await waitFor(() => expect(screen.getByTestId("external-app-variable-baseUrl")).toBeDefined());

		fireEvent.click(screen.getByTestId("external-app-detail-settings-save"));

		await waitFor(() => expect((screen.getByTestId("external-app-variable-baseUrl") as HTMLInputElement).disabled).toBe(true));
		expect((screen.getByTestId("external-app-detail-settings-save") as HTMLButtonElement).disabled).toBe(true);

		release();
		await waitFor(() => expect((screen.getByTestId("external-app-variable-baseUrl") as HTMLInputElement).disabled).toBe(false));
	});

	// An Update that lands while this tab is open swaps the installed manifest. A one-shot seed left the new
	// definitions rendering against the old values map, and resent a name the new manifest never declared — which the
	// node refuses, so every save 400s for as long as the page stays open.
	it("reconciles the settings form against the manifest an update installed under the open tab", async () => {
		const bodies: { variables: Record<string, string> }[] = [];
		let instanceReads = 0;
		const installed = externalAppInstance({ status: "Stopped", version: 11 });
		const updated = externalAppInstance({
			status: "Stopped",
			version: 12,
			manifestVersion: 4,
			manifest: externalAppManifest({
				manifestVersion: 4,
				variables: [externalAppVariable({ name: "apiKey", label: "API key", default: "k-default" })],
			}),
			variables: {},
		});
		server.use(
			http.get(localApiPath(instancePath), () => {
				instanceReads += 1;
				return HttpResponse.json(instanceReads === 1 ? installed : updated);
			}),
			jsonRoute("get", "external-apps/runtime", externalAppRuntime()),
			jsonRoute("get", `${instancePath}/events`, { items: [], highestSequence: 0, hasMore: false }),
			jsonRoute("get", `${instancePath}/logs`, externalAppInstanceLogs()),
			http.put(localApiPath(`${instancePath}/variables`), async ({ request }) => {
				bodies.push((await request.json()) as { variables: Record<string, string> });
				return HttpResponse.json(installed);
			}),
		);
		renderPage();
		await waitFor(() => expect(screen.getByTestId("external-app-detail-tab-settings")).toBeDefined());
		fireEvent.click(screen.getByTestId("external-app-detail-tab-settings"));
		await waitFor(() => expect(screen.getByTestId("external-app-variable-baseUrl")).toBeDefined());

		// The save invalidates the instance, and the re-read answers with the updated manifest.
		fireEvent.click(screen.getByTestId("external-app-detail-settings-save"));
		await waitFor(() => expect(bodies).toHaveLength(1));
		await waitFor(() => expect(screen.getByTestId("external-app-variable-apiKey")).toBeDefined());

		// The name the new manifest declares is seeded from its default; the one it dropped is gone from the form.
		expect((screen.getByTestId("external-app-variable-apiKey") as HTMLInputElement).value).toBe("k-default");
		expect(screen.queryByTestId("external-app-variable-baseUrl")).toBeNull();

		fireEvent.click(screen.getByTestId("external-app-detail-settings-save"));

		await waitFor(() => expect(bodies).toHaveLength(2));
		expect(bodies[1]?.variables).toEqual({ apiKey: "k-default" });
	});

	// The node answers a save with the MASKED instance. Keeping the typed replacement in local state left the
	// plaintext in the box and resent it verbatim on the next save, writing the secret in clear a second time.
	it("puts a saved secret back to the stored sentinel, so a second save resends no plaintext", async () => {
		const bodies: { variables: Record<string, string> }[] = [];
		const stored = externalAppInstance({
			status: "Stopped",
			version: 11,
			manifest: externalAppManifest({
				variables: [externalAppVariable(), externalAppVariable({ name: "token", label: "Token", type: "secret", default: null })],
			}),
			// What the node returns for a stored secret: never the value, always the sentinel.
			variables: { baseUrl: "http://127.0.0.1", token: EXTERNAL_APP_SECRET_SENTINEL },
		});
		server.use(
			...baseRoutes(stored),
			http.put(localApiPath(`${instancePath}/variables`), async ({ request }) => {
				bodies.push((await request.json()) as { variables: Record<string, string> });
				return HttpResponse.json(stored);
			}),
		);
		renderPage();
		await waitFor(() => expect(screen.getByTestId("external-app-detail-tab-settings")).toBeDefined());
		fireEvent.click(screen.getByTestId("external-app-detail-tab-settings"));
		await waitFor(() => expect(screen.getByTestId("external-app-variable-token")).toBeDefined());

		fireEvent.change(screen.getByTestId("external-app-variable-token"), { target: { value: "hunter2" } });
		fireEvent.click(screen.getByTestId("external-app-detail-settings-save"));
		await waitFor(() => expect(bodies).toHaveLength(1));
		expect(bodies[0]?.variables["token"]).toBe("hunter2");

		// Masked again once the server answered, so the box is empty and the next save keeps what is stored.
		await waitFor(() => expect((screen.getByTestId("external-app-variable-token") as HTMLInputElement).value).toBe(""));
		fireEvent.click(screen.getByTestId("external-app-detail-settings-save"));

		await waitFor(() => expect(bodies).toHaveLength(2));
		expect(bodies[1]?.variables["token"]).toBe(EXTERNAL_APP_SECRET_SENTINEL);
	});

	// The rejected `expectedVersion` is the one this tab rendered. Toasting alone let every retry echo the same stale
	// token for as long as the page stayed open.
	it("re-reads the instance when the save is refused for a stale version", async () => {
		let instanceReads = 0;
		server.use(
			http.get(localApiPath(instancePath), () => {
				instanceReads += 1;
				return HttpResponse.json(externalAppInstance({ status: "Stopped", version: instanceReads === 1 ? 11 : 12 }));
			}),
			jsonRoute("get", "external-apps/runtime", externalAppRuntime()),
			jsonRoute("get", `${instancePath}/events`, { items: [], highestSequence: 0, hasMore: false }),
			jsonRoute("get", `${instancePath}/logs`, externalAppInstanceLogs()),
			problemDetailsRoute("put", `${instancePath}/variables`, 409, {
				detail: "stale",
				// biome-ignore lint/suspicious/noExplicitAny: ProblemDetails does not declare conflictType, which is the field under test.
				...({ conflictType: "ExternalAppVersionConflict" } as any),
			}),
		);
		renderPage();
		await waitFor(() => expect(screen.getByTestId("external-app-detail-tab-settings")).toBeDefined());
		fireEvent.click(screen.getByTestId("external-app-detail-tab-settings"));
		await waitFor(() => expect(screen.getByTestId("external-app-detail-settings-save")).toBeDefined());
		expect(instanceReads).toBe(1);

		fireEvent.click(screen.getByTestId("external-app-detail-settings-save"));

		await waitFor(() => expect(instanceReads).toBe(2));
	});

	it("toasts the version conflict when the save carries a stale token", async () => {
		server.use(
			...baseRoutes(externalAppInstance({ status: "Stopped" })),
			problemDetailsRoute("put", `${instancePath}/variables`, 409, {
				detail: "stale",
				// biome-ignore lint/suspicious/noExplicitAny: ProblemDetails does not declare conflictType, which is the field under test.
				...({ conflictType: "ExternalAppVersionConflict" } as any),
			}),
		);
		toastMock.error.mockClear();
		renderPage();
		await waitFor(() => expect(screen.getByTestId("external-app-detail-tab-settings")).toBeDefined());

		fireEvent.click(screen.getByTestId("external-app-detail-tab-settings"));
		await waitFor(() => expect(screen.getByTestId("external-app-detail-settings-save")).toBeDefined());
		fireEvent.click(screen.getByTestId("external-app-detail-settings-save"));

		await waitFor(() => expect(toastMock.error).toHaveBeenCalled());
		expect(toastMock.error.mock.calls[0]?.[0]).toContain("changed while this page was open");
	});

	it("disables the settings form and says why while the application is running", async () => {
		server.use(...baseRoutes(externalAppInstance({ status: "Running" })));
		renderPage();
		await waitFor(() => expect(screen.getByTestId("external-app-detail-tab-settings")).toBeDefined());

		fireEvent.click(screen.getByTestId("external-app-detail-tab-settings"));

		await waitFor(() => expect(screen.getByTestId("external-app-detail-settings-stopped-only")).toBeDefined());
		expect((screen.getByTestId("external-app-detail-settings-save") as HTMLButtonElement).disabled).toBe(true);
	});

	it("renders the not-found state for an instance that is gone", async () => {
		server.use(
			problemDetailsRoute("get", instancePath, 404, { detail: "gone" }),
			jsonRoute("get", "external-apps/runtime", externalAppRuntime()),
		);
		renderPage();

		await waitFor(() => expect(screen.getByTestId("external-app-detail-not-found")).toBeDefined());
		expect(screen.getByTestId("external-app-detail-not-found").textContent).toContain("not installed");
	});

	it("keeps the logs tab reading the part names off the instance manifest", async () => {
		server.use(
			...baseRoutes(
				externalAppInstance({
					manifest: externalAppManifest({
						services: [
							{ name: "web", image: "example/ntfy", imageTag: "2", ports: [], storage: [], hasHealthcheck: false, dependsOn: [] },
						],
					}),
				}),
			),
		);
		renderPage();
		await waitFor(() => expect(screen.getByTestId("external-app-detail-tab-logs")).toBeDefined());

		fireEvent.click(screen.getByTestId("external-app-detail-tab-logs"));

		await waitFor(() => expect(screen.getByTestId("external-app-logs-text")).toBeDefined());
		expect(screen.getByTestId("external-app-logs-text").textContent).toContain("listening on :80");
	});
});
