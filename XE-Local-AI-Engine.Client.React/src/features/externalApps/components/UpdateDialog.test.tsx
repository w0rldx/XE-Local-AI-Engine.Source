// @vitest-environment jsdom

// The update body is the thing under test. It names the manifest being moved TO — `targetManifestVersion`, not the
// installed `currentManifestVersion` and not the instance's own `manifestVersion` — and the fixture's two versions
// differ so the wrong one cannot pass. The disclosure runs BEFORE the call, so a permission 409 here means the dialog
// was bypassed.

import { fireEvent, screen, waitFor } from "@testing-library/react";
import { HttpResponse, http } from "msw";
import { describe, expect, it, vi } from "vitest";

import { UpdateDialog } from "@/features/externalApps/components/UpdateDialog";
import type { ProblemDetails } from "@/core/api/models/ProblemDetails";
import type { ExternalAppInstanceSummaryView } from "@/features/externalApps/models/ExternalAppModels";
import {
	externalAppEffectivePermissions,
	externalAppInstance,
	externalAppInstancesResponse,
	externalAppInstanceSummary,
	externalAppTestIds,
	externalAppUpdatePreview,
	externalAppVariable,
} from "@/features/externalApps/test/ExternalAppFixtures";
import { useExternalAppInstance, useExternalAppInstances } from "@/features/externalApps/queries/useExternalApps";
import { jsonRoute, localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { createTestQueryClient, renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

const toastMock = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn(), info: vi.fn(), warning: vi.fn() }));
vi.mock("@/core/ui/notifications/Toast", () => ({ toast: toastMock }));

setupMswServer();

const instanceId = externalAppTestIds.instance;
const previewPath = `external-apps/instances/${instanceId}/update-preview`;
const updatePath = `external-apps/instances/${instanceId}/update`;

// The two bodies this route can answer: the 202 admission summary, or a ProblemDetails refusal. Typed rather than
// `DefaultBodyType`, so a drifted fixture fails tsc instead of being posted back as an opaque blob.
function captureUpdate(response: ExternalAppInstanceSummaryView | ProblemDetails = externalAppInstanceSummary(), status = 202) {
	const requests: unknown[] = [];
	server.use(
		http.post(localApiPath(updatePath), async ({ request }) => {
			requests.push(await request.json());
			return HttpResponse.json(response, { status });
		}),
	);
	return requests;
}

function renderDialog(onClose = vi.fn()) {
	renderWithProviders(<UpdateDialog instance={externalAppInstance({ version: 11 })} opened={true} onClose={onClose} />);
	return { onClose };
}

/** The Continue button is enabled only once the preview has landed, so this is also the wait for it. */
async function waitForPreview(): Promise<void> {
	await waitFor(() => expect((screen.getByTestId("external-app-update-next") as HTMLButtonElement).disabled).toBe(false));
}

/** Walks the dialog from the settings step to the resource step, taking the permission step when it is rendered. */
async function reachResources(): Promise<void> {
	await waitForPreview();
	fireEvent.click(screen.getByTestId("external-app-update-next"));
	const accept = screen.queryByTestId("external-app-update-accept");
	if (accept) {
		fireEvent.click(accept);
	}
	await waitFor(() => expect(screen.getByTestId("external-app-update-step-resources")).toBeDefined());
}

/** Mounts the two feeds a stale-row conflict has to drop, so the invalidation shows up as a second read. */
function MountedInstanceFeeds() {
	const instance = useExternalAppInstance(instanceId);
	const list = useExternalAppInstances();
	return <span data-testid="mounted-feeds">{`${instance.isSuccess}:${list.isSuccess}`}</span>;
}

describe("UpdateDialog", () => {
	it("runs settings → permissions → resources when the update asks for more access", async () => {
		server.use(jsonRoute("get", previewPath, externalAppUpdatePreview({ addedPermissions: ["capabilities"] })));
		renderDialog();

		await waitForPreview();
		expect(screen.getByTestId("external-app-update-step-variables")).toBeDefined();
		fireEvent.click(screen.getByTestId("external-app-update-next"));

		await waitFor(() => expect(screen.getByTestId("external-app-update-step-permissions")).toBeDefined());
		// The per-part block, from the preview's effective permissions: an application-level list hides a privilege
		// one part holds alone.
		expect(screen.getByTestId("external-app-permission-part-web")).toBeDefined();
		fireEvent.click(screen.getByTestId("external-app-update-accept"));

		await waitFor(() => expect(screen.getByTestId("external-app-update-step-resources")).toBeDefined());
	});

	it("skips the permission step when the target asks for nothing more", async () => {
		server.use(jsonRoute("get", previewPath, externalAppUpdatePreview({ addedPermissions: [] })));
		renderDialog();

		await waitForPreview();
		fireEvent.click(screen.getByTestId("external-app-update-next"));

		await waitFor(() => expect(screen.getByTestId("external-app-update-step-resources")).toBeDefined());
		expect(screen.queryByTestId("external-app-update-step-permissions")).toBeNull();
		expect(screen.getByTestId("external-app-update-resources").textContent).toContain("no additional access");
	});

	it("sends the target version, the preview's fingerprint, the acceptance and the row's expectedVersion", async () => {
		server.use(jsonRoute("get", previewPath, externalAppUpdatePreview()));
		const requests = captureUpdate();
		renderDialog();
		await reachResources();

		fireEvent.click(screen.getByTestId("external-app-update-confirm"));

		await waitFor(() => expect(requests).toHaveLength(1));
		expect(requests[0]).toEqual({
			// 4, not the installed 3 and not the instance's own manifestVersion.
			manifestVersion: 4,
			manifestSha256: "b".repeat(64),
			acceptPermissions: true,
			variables: { baseUrl: "http://127.0.0.1" },
			expectedVersion: 11,
		});
	});

	it("blocks Continue until a newly required target variable is filled in", async () => {
		server.use(
			jsonRoute(
				"get",
				previewPath,
				externalAppUpdatePreview({
					// The target declares a variable the installed version never had, so nothing seeds it.
					variables: [externalAppVariable({ name: "adminPassword", label: "Admin password", required: true, default: null })],
					currentValues: {},
				}),
			),
		);
		renderDialog();

		await waitFor(() => expect(screen.getByTestId("external-app-variable-adminPassword")).toBeDefined());
		expect((screen.getByTestId("external-app-update-next") as HTMLButtonElement).disabled).toBe(true);

		fireEvent.change(screen.getByTestId("external-app-variable-adminPassword"), { target: { value: "hunter2" } });
		await waitFor(() => expect((screen.getByTestId("external-app-update-next") as HTMLButtonElement).disabled).toBe(false));
	});

	it("disables the confirm and names the reason when the application left the catalog", async () => {
		server.use(jsonRoute("get", previewPath, externalAppUpdatePreview({ canUpdate: false, blockedReason: "CatalogMissing" })));
		renderDialog();
		await reachResources();

		expect((screen.getByTestId("external-app-update-confirm") as HTMLButtonElement).disabled).toBe(true);
		expect(screen.getByTestId("external-app-update-blocked").textContent).toContain("no longer offered in the catalog");
	});

	it("returns to the settings step when the manifest moved between disclosure and submit", async () => {
		server.use(jsonRoute("get", previewPath, externalAppUpdatePreview()));
		server.use(
			http.post(localApiPath(updatePath), () =>
				HttpResponse.json(
					{ type: "about:blank", title: "Conflict", status: 409, detail: "moved", conflictType: "ExternalAppManifestChanged" },
					{ status: 409, headers: { "content-type": "application/problem+json" } },
				),
			),
		);
		toastMock.error.mockClear();
		renderDialog();
		await reachResources();

		fireEvent.click(screen.getByTestId("external-app-update-confirm"));

		await waitFor(() => expect(screen.getByTestId("external-app-update-step-variables")).toBeDefined());
		expect(toastMock.error).toHaveBeenCalled();
	});

	// The installed manifest has no graphics-card grant and the target does. A panel reading the INSTALLED manifest
	// renders "No access to the graphics card" under "It does not get" and highlights that line as NEW access, which
	// is the disclosure contradicting itself on the one screen the operator is asked to agree with.
	it("reopens at the permission step showing the target's grant when the dialog was bypassed", async () => {
		server.use(
			jsonRoute(
				"get",
				previewPath,
				externalAppUpdatePreview({
					addedPermissions: [],
					effectivePermissions: externalAppEffectivePermissions({ gpu: "required" }),
				}),
			),
		);
		server.use(
			http.post(localApiPath(updatePath), () =>
				HttpResponse.json(
					{
						type: "about:blank",
						title: "Conflict",
						status: 409,
						detail: "needs acknowledgement",
						conflictType: "ExternalAppPermissionChangeRequiresAcknowledgement",
						addedPermissions: ["gpu"],
					},
					{ status: 409, headers: { "content-type": "application/problem+json" } },
				),
			),
		);
		renderDialog();
		await reachResources();

		fireEvent.click(screen.getByTestId("external-app-update-confirm"));

		await waitFor(() => expect(screen.getByTestId("external-app-update-step-permissions")).toBeDefined());
		// The SPA cannot compute the widening — it never sees the target's manifest — so it renders the server's list.
		const gpu = screen.getByTestId("external-app-permission-gpu");
		expect(gpu.textContent).toBe("Use of the graphics card");
		expect(gpu.getAttribute("data-added")).toBe("true");
		expect(screen.getByTestId("external-app-permissions-added").textContent).toContain("Use of the graphics card");
	});

	it("lands back on the resource step when a posted update 404s, so the refetched reason is visible", async () => {
		let previewReads = 0;
		server.use(
			http.get(localApiPath(previewPath), () => {
				previewReads += 1;
				return HttpResponse.json(
					previewReads === 1
						? externalAppUpdatePreview()
						: externalAppUpdatePreview({ canUpdate: false, blockedReason: "CatalogMissing" }),
				);
			}),
		);
		captureUpdate({ type: "about:blank", title: "Not Found", status: 404, detail: "gone" }, 404);
		renderDialog();
		await reachResources();

		fireEvent.click(screen.getByTestId("external-app-update-confirm"));

		await waitFor(() => expect(screen.getByTestId("external-app-update-blocked")).toBeDefined());
		expect(screen.getByTestId("external-app-update-blocked").textContent).toContain("no longer offered in the catalog");
		expect((screen.getByTestId("external-app-update-confirm") as HTMLButtonElement).disabled).toBe(true);
	});
	// R3-14. The acceptance is bound on LEAVING the settings step, on both branches: an update that asks for nothing
	// more never renders the permission step, and binding the fingerprint only there left this effect inert — a
	// preview that moved under the open dialog was then confirmed with `acceptPermissions: true` against a manifest
	// nobody had been shown.
	it("clears the acceptance and returns to step 1 when a refetched preview carries a different pair", async () => {
		let previewReads = 0;
		server.use(
			http.get(localApiPath(previewPath), () => {
				previewReads += 1;
				return HttpResponse.json(
					previewReads === 1
						? externalAppUpdatePreview({ addedPermissions: [] })
						: externalAppUpdatePreview({ addedPermissions: [], targetManifestVersion: 5, manifestSha256: "c".repeat(64) }),
				);
			}),
		);
		captureUpdate({ type: "about:blank", title: "Not Found", status: 404, detail: "gone" }, 404);
		toastMock.error.mockClear();
		renderDialog();
		// The permission step is skipped here, which is exactly the path that used to leave the acceptance unbound.
		await reachResources();
		expect(screen.queryByTestId("external-app-update-step-permissions")).toBeNull();

		// The 404 refetches, and the second read describes a different update.
		fireEvent.click(screen.getByTestId("external-app-update-confirm"));

		await waitFor(() => expect(screen.getByTestId("external-app-update-step-variables")).toBeDefined());
		expect(
			toastMock.error.mock.calls.some((call) => String(call[0]).includes("changed in the catalog while you were looking at it")),
		).toBe(true);
	});

	// Clearing the acceptance was only half the repair. The values map still held the OLD target's names, so the form
	// rendered the new target's fields while the body kept sending ones it does not declare — a 400 on every retry.
	it("reconciles the variables against the definitions a moved target brought with it", async () => {
		let previewReads = 0;
		const bodies: { variables: Record<string, string> }[] = [];
		server.use(
			http.get(localApiPath(previewPath), () => {
				previewReads += 1;
				return HttpResponse.json(
					previewReads === 1
						? externalAppUpdatePreview({ addedPermissions: [] })
						: externalAppUpdatePreview({
								addedPermissions: [],
								targetManifestVersion: 5,
								manifestSha256: "c".repeat(64),
								// `baseUrl` is gone and `apiUrl` is new and required, with nothing stored to seed it.
								variables: [externalAppVariable({ name: "apiUrl", label: "API URL", required: true, default: null })],
								currentValues: {},
							}),
				);
			}),
			http.post(localApiPath(updatePath), async ({ request }) => {
				bodies.push((await request.json()) as { variables: Record<string, string> });
				return bodies.length === 1
					? HttpResponse.json({ type: "about:blank", title: "Not Found", status: 404, detail: "gone" }, { status: 404 })
					: HttpResponse.json(externalAppInstanceSummary(), { status: 202 });
			}),
		);
		toastMock.error.mockClear();
		renderDialog();
		await reachResources();

		// The 404 refetches, and the second read describes a different target with different settings.
		fireEvent.click(screen.getByTestId("external-app-update-confirm"));
		await waitFor(() => expect(screen.getByTestId("external-app-update-step-variables")).toBeDefined());

		await waitFor(() => expect(screen.getByTestId("external-app-variable-apiUrl")).toBeDefined());
		expect(screen.queryByTestId("external-app-variable-baseUrl")).toBeNull();
		expect((screen.getByTestId("external-app-update-next") as HTMLButtonElement).disabled).toBe(true);

		fireEvent.change(screen.getByTestId("external-app-variable-apiUrl"), { target: { value: "http://127.0.0.1:8080" } });
		await waitFor(() => expect((screen.getByTestId("external-app-update-next") as HTMLButtonElement).disabled).toBe(false));
		await reachResources();
		fireEvent.click(screen.getByTestId("external-app-update-confirm"));

		// The dropped name is not in the retry's body: the node rejects a variable the target does not declare.
		await waitFor(() => expect(bodies).toHaveLength(2));
		expect(bodies[1]?.variables).toEqual({ apiUrl: "http://127.0.0.1:8080" });
	});

	// The rejected `expectedVersion` came from the instance the dialog was opened over. Refetching only the PREVIEW
	// left that row cached, so every retry echoed the same refused token.
	it("re-reads the instance and the list when the update is refused for a stale version", async () => {
		let instanceReads = 0;
		let listReads = 0;
		server.use(
			jsonRoute("get", previewPath, externalAppUpdatePreview({ addedPermissions: [] })),
			http.get(localApiPath(`external-apps/instances/${instanceId}`), () => {
				instanceReads += 1;
				return HttpResponse.json(externalAppInstance({ version: 12 }));
			}),
			http.get(localApiPath("external-apps/instances"), () => {
				listReads += 1;
				// The list carries the FULL view; only the 202 admission bodies are summaries.
				return HttpResponse.json(externalAppInstancesResponse());
			}),
			http.post(localApiPath(updatePath), () =>
				HttpResponse.json(
					{ type: "about:blank", title: "Conflict", status: 409, detail: "stale", conflictType: "ExternalAppVersionConflict" },
					{ status: 409, headers: { "content-type": "application/problem+json" } },
				),
			),
		);
		const queryClient = createTestQueryClient();
		// The two feeds the retry depends on, mounted so an invalidation is observable as a re-read.
		renderWithProviders(
			<>
				<MountedInstanceFeeds />
				<UpdateDialog instance={externalAppInstance({ version: 11 })} opened={true} onClose={vi.fn()} />
			</>,
			{ queryClient },
		);
		await waitFor(() => expect([instanceReads, listReads]).toEqual([1, 1]));
		await reachResources();

		fireEvent.click(screen.getByTestId("external-app-update-confirm"));

		await waitFor(() => expect([instanceReads, listReads]).toEqual([2, 2]));
	});

	// C19. A variable the target newly requires has nothing to seed it, so it renders empty and blocks Continue for a
	// reason that is otherwise invisible: the field says the new version needs it.
	it("flags a newly required target variable and leaves the settings the installed version already had alone", async () => {
		server.use(
			jsonRoute(
				"get",
				previewPath,
				externalAppUpdatePreview({
					variables: [
						externalAppVariable(),
						externalAppVariable({ name: "adminPassword", label: "Admin password", required: true, default: null }),
					],
				}),
			),
		);
		renderDialog();

		await waitFor(() => expect(screen.getByTestId("external-app-variable-adminPassword")).toBeDefined());
		expect(screen.getByTestId("external-app-variable-new-adminPassword").textContent).toBe("The new version needs this setting.");
		// `baseUrl` is declared by the installed manifest too, so it is not new and carries no sentence.
		expect(screen.queryByTestId("external-app-variable-new-baseUrl")).toBeNull();
	});
});
