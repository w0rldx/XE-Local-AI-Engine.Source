// @vitest-environment jsdom

// The install body is a consent record: `acceptPermissions: true` plus the manifest fingerprint the operator was
// actually shown. Both are asserted off the wire here, not off the UI, because either one dropped is a silent
// failure — the node would install something nobody agreed to, and no rendering would look wrong.

import { fireEvent, screen, waitFor } from "@testing-library/react";
import { HttpResponse, http } from "msw";
import { describe, expect, it, vi } from "vitest";

import { InstallDialog } from "@/features/externalApps/components/InstallDialog";
import {
	externalAppInstallPreview,
	externalAppInstanceSummary,
	externalAppSummary,
	externalAppTestIds,
	externalAppVariable,
} from "@/features/externalApps/test/ExternalAppFixtures";
import { jsonRoute, localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

const applicationId = externalAppTestIds.application;
const previewPath = `external-apps/catalog/${applicationId}/install-preview`;

/** Records every install body posted, answering 202 with the summary a real install returns. */
function captureInstall(): { body: unknown }[] {
	const requests: { body: unknown }[] = [];
	server.use(
		http.post(localApiPath("external-apps/instances"), async ({ request }) => {
			requests.push({ body: await request.json() });
			return HttpResponse.json(externalAppInstanceSummary({ status: "Installing" }), { status: 202 });
		}),
	);
	return requests;
}

/**
 * An install that stays in flight until the test releases it — a gate the test owns, not a timer. The dialog closes
 * itself on success, so anything asserted about the installing step needs the request still open.
 */
function pendingInstall(): { release: () => void } {
	let release = (): void => undefined;
	const gate = new Promise<void>((resolve) => {
		release = () => resolve();
	});
	server.use(
		http.post(localApiPath("external-apps/instances"), async () => {
			await gate;
			return HttpResponse.json(externalAppInstanceSummary({ status: "Installing" }), { status: 202 });
		}),
	);
	return { release: () => release() };
}

function renderDialog(overrides: { readonly onInstalled?: (instanceId: string) => void } = {}) {
	const onInstalled = overrides.onInstalled ?? vi.fn();
	const onClose = vi.fn();
	renderWithProviders(
		<InstallDialog application={externalAppSummary()} opened={true} onClose={onClose} onInstalled={onInstalled} />,
	);
	return { onInstalled, onClose };
}

/** Accepts the permissions. The button is disabled until the preview lands, which is the signal to click it. */
async function acceptPermissions(): Promise<void> {
	await waitFor(() => expect((screen.getByTestId("external-app-install-accept") as HTMLButtonElement).disabled).toBe(false));
	fireEvent.click(screen.getByTestId("external-app-install-accept"));
}

/** Accepts the permissions and continues past the settings step, leaving the dialog on its way to the resource step. */
async function advanceToResources(): Promise<void> {
	await acceptPermissions();
	await waitFor(() => expect(screen.getByTestId("external-app-install-next")).toBeDefined());
	fireEvent.click(screen.getByTestId("external-app-install-next"));
	await waitFor(() => expect(screen.getByTestId("external-app-install-confirm")).toBeDefined());
}

describe("InstallDialog", () => {
	it("runs permissions → variables → resources → installing, and step 1 accepts rather than navigates", async () => {
		server.use(jsonRoute("get", previewPath, externalAppInstallPreview()));
		const install = pendingInstall();
		renderDialog();

		await waitFor(() => expect(screen.getByTestId("external-app-install-step-permissions")).toBeDefined());
		// The label IS the acceptance. "Continue" would make consent a side effect of navigation.
		expect(screen.getByTestId("external-app-install-accept").textContent).toBe("Accept and continue");

		await acceptPermissions();
		await waitFor(() => expect(screen.getByTestId("external-app-install-step-variables")).toBeDefined());

		fireEvent.click(screen.getByTestId("external-app-install-next"));
		await waitFor(() => expect(screen.getByTestId("external-app-install-step-resources")).toBeDefined());

		fireEvent.click(screen.getByTestId("external-app-install-confirm"));
		await waitFor(() => expect(screen.getByTestId("external-app-install-step-installing")).toBeDefined());
		install.release();
	});

	it("posts acceptPermissions and the fingerprint copied from the preview the operator was shown", async () => {
		server.use(jsonRoute("get", previewPath, externalAppInstallPreview({ manifestVersion: 9, manifestSha256: "c".repeat(64) })));
		const requests = captureInstall();
		renderDialog();

		await advanceToResources();
		fireEvent.click(screen.getByTestId("external-app-install-confirm"));

		await waitFor(() => expect(requests).toHaveLength(1));
		expect(requests[0]?.body).toEqual({
			applicationId,
			manifestVersion: 9,
			manifestSha256: "c".repeat(64),
			variables: { baseUrl: "http://127.0.0.1" },
			acceptPermissions: true,
		});
	});

	it("renders the permission step's per-part block from the preview's effectivePermissions", async () => {
		server.use(jsonRoute("get", previewPath, externalAppInstallPreview()));
		renderDialog();

		await waitFor(() => expect(screen.getByTestId("external-app-permission-part-web")).toBeDefined());
		expect(screen.getByTestId("external-app-permission-part-worker")).toBeDefined();
	});

	it("shows optional variables as well as required ones and blocks Continue while a required one is empty", async () => {
		server.use(
			jsonRoute(
				"get",
				previewPath,
				externalAppInstallPreview({
					variables: [
						externalAppVariable({ name: "baseUrl", label: "Base URL", required: true, default: null }),
						externalAppVariable({ name: "note", label: "Note", required: false, default: null }),
					],
				}),
			),
		);
		renderDialog();

		await acceptPermissions();
		await waitFor(() => expect(screen.getByTestId("external-app-variable-baseUrl")).toBeDefined());

		// Optional is rendered, not hidden: the operator sees everything the application declares.
		expect(screen.getByTestId("external-app-variable-note")).toBeDefined();
		expect((screen.getByTestId("external-app-install-next") as HTMLButtonElement).disabled).toBe(true);

		fireEvent.change(screen.getByTestId("external-app-variable-baseUrl"), { target: { value: "http://127.0.0.1" } });
		await waitFor(() => expect((screen.getByTestId("external-app-install-next") as HTMLButtonElement).disabled).toBe(false));
	});

	it("disables Install on canInstall false and labels it with the blocked reason, resources notwithstanding", async () => {
		// The resource check passes; the refusal is the GPU. `canInstall` is the gate, not the arithmetic.
		server.use(jsonRoute("get", previewPath, externalAppInstallPreview({ canInstall: false, blockedReason: "GpuNotSupported" })));
		renderDialog();

		await advanceToResources();
		expect((screen.getByTestId("external-app-install-confirm") as HTMLButtonElement).disabled).toBe(true);
		expect(screen.getByTestId("external-app-install-blocked").textContent).toContain("This application needs a graphics card");
		// The resource numbers stay visible as the detail under the reason.
		expect(screen.getByTestId("external-app-install-resources").textContent).toContain("MB needed");
	});

	it("re-fetches the preview on entering the resource step", async () => {
		let reads = 0;
		server.use(
			http.get(localApiPath(previewPath), () => {
				reads += 1;
				return HttpResponse.json(externalAppInstallPreview());
			}),
		);
		renderDialog();

		await advanceToResources();
		await waitFor(() => expect(reads).toBeGreaterThan(1));
	});

	it("clears the acceptance and returns to step 1 when a successful refetch changes the fingerprint", async () => {
		let reads = 0;
		server.use(
			http.get(localApiPath(previewPath), () => {
				reads += 1;
				// The second read is the resource step's refetch, and the catalog moved underneath it.
				return HttpResponse.json(
					reads === 1
						? externalAppInstallPreview()
						: externalAppInstallPreview({ manifestVersion: 4, manifestSha256: "d".repeat(64) }),
				);
			}),
		);
		renderDialog();

		await acceptPermissions();
		await waitFor(() => expect(screen.getByTestId("external-app-install-next")).toBeDefined());
		fireEvent.click(screen.getByTestId("external-app-install-next"));

		await waitFor(() => expect(screen.getByTestId("external-app-install-step-permissions")).toBeDefined());
		expect(screen.getByTestId("external-app-install-accept")).toBeDefined();
	});

	// Resetting the acceptance was only half the repair. The values map still held the OLD manifest's names, so the
	// form showed fields the new manifest declares while the body kept sending ones it does not — a 400 on every retry.
	it("reconciles the variables against the definitions a moved manifest brought with it", async () => {
		let reads = 0;
		server.use(
			http.get(localApiPath(previewPath), () => {
				reads += 1;
				return HttpResponse.json(
					reads === 1
						? externalAppInstallPreview()
						: externalAppInstallPreview({
								manifestVersion: 4,
								manifestSha256: "d".repeat(64),
								// `baseUrl` is gone and `apiUrl` is new and required: nothing seeds it, so it blocks.
								variables: [externalAppVariable({ name: "apiUrl", label: "API URL", required: true, default: null })],
							}),
				);
			}),
		);
		const bodies = captureInstall();
		renderDialog();

		await acceptPermissions();
		await waitFor(() => expect(screen.getByTestId("external-app-install-next")).toBeDefined());
		fireEvent.click(screen.getByTestId("external-app-install-next"));
		await waitFor(() => expect(screen.getByTestId("external-app-install-step-permissions")).toBeDefined());

		// Step 2 now renders the NEW manifest's fields, and the newly required one starts empty and blocks Continue.
		await acceptPermissions();
		await waitFor(() => expect(screen.getByTestId("external-app-variable-apiUrl")).toBeDefined());
		expect(screen.queryByTestId("external-app-variable-baseUrl")).toBeNull();
		expect((screen.getByTestId("external-app-install-next") as HTMLButtonElement).disabled).toBe(true);

		fireEvent.change(screen.getByTestId("external-app-variable-apiUrl"), { target: { value: "http://127.0.0.1:8080" } });
		await waitFor(() => expect((screen.getByTestId("external-app-install-next") as HTMLButtonElement).disabled).toBe(false));
		fireEvent.click(screen.getByTestId("external-app-install-next"));
		await waitFor(() => expect(screen.getByTestId("external-app-install-confirm")).toBeDefined());
		fireEvent.click(screen.getByTestId("external-app-install-confirm"));

		// The dropped name is not in the body: the node rejects a variable the manifest does not declare.
		await waitFor(() => expect(bodies).toHaveLength(1));
		expect((bodies[0]?.body as { variables?: Record<string, string> } | undefined)?.variables).toEqual({
			apiUrl: "http://127.0.0.1:8080",
		});
	});

	it("stays on the resource step when the refetched fingerprint is unchanged", async () => {
		let reads = 0;
		server.use(
			http.get(localApiPath(previewPath), () => {
				reads += 1;
				return HttpResponse.json(externalAppInstallPreview());
			}),
		);
		renderDialog();

		await advanceToResources();
		await waitFor(() => expect(reads).toBeGreaterThan(1));
		expect(screen.getByTestId("external-app-install-step-resources")).toBeDefined();
	});

	it("returns to step 1 on a 409 ExternalAppManifestChanged", async () => {
		server.use(
			jsonRoute("get", previewPath, externalAppInstallPreview()),
			// A conflict body carries `conflictType`, which the shared ProblemDetails shape does not declare, so the
			// route is written out rather than built by `problemDetailsRoute` — the same way the Dev Workflows tests do.
			http.post(localApiPath("external-apps/instances"), () =>
				HttpResponse.json(
					{
						type: "about:blank",
						title: "Conflict",
						status: 409,
						detail: "The manifest changed.",
						conflictType: "ExternalAppManifestChanged",
					},
					{ status: 409, headers: { "content-type": "application/problem+json" } },
				),
			),
		);
		renderDialog();

		await advanceToResources();
		fireEvent.click(screen.getByTestId("external-app-install-confirm"));

		await waitFor(() => expect(screen.getByTestId("external-app-install-step-permissions")).toBeDefined());
		expect(screen.getByTestId("external-app-install-accept")).toBeDefined();
	});

	it("calls onInstalled with the new instance id", async () => {
		server.use(jsonRoute("get", previewPath, externalAppInstallPreview()));
		captureInstall();
		const onInstalled = vi.fn();
		renderDialog({ onInstalled });

		await advanceToResources();
		fireEvent.click(screen.getByTestId("external-app-install-confirm"));

		await waitFor(() => expect(onInstalled).toHaveBeenCalledWith(externalAppTestIds.instance));
	});

	it("moves focus to each step's primary action", async () => {
		server.use(jsonRoute("get", previewPath, externalAppInstallPreview()));
		renderDialog();

		await waitFor(() => expect(document.activeElement).toBe(screen.getByTestId("external-app-install-accept")));

		await acceptPermissions();
		await waitFor(() => expect(document.activeElement).toBe(screen.getByTestId("external-app-install-next")));
	});

	it("closes without a confirmation while an install is in flight", async () => {
		server.use(jsonRoute("get", previewPath, externalAppInstallPreview()));
		const install = pendingInstall();
		const { onClose } = renderDialog();

		await advanceToResources();
		fireEvent.click(screen.getByTestId("external-app-install-confirm"));
		await waitFor(() => expect(screen.getByTestId("external-app-install-close")).toBeDefined());

		fireEvent.click(screen.getByTestId("external-app-install-close"));
		// No ConfirmProvider is mounted here at all: a dialog that asked to confirm would throw rather than close.
		expect(onClose).toHaveBeenCalled();
		install.release();
	});

	it("offers a way to the existing instance instead of a second install", async () => {
		server.use(
			jsonRoute(
				"get",
				previewPath,
				externalAppInstallPreview({
					canInstall: false,
					blockedReason: "AlreadyInstalled",
					existingInstanceId: externalAppTestIds.otherInstance,
				}),
			),
		);
		const onInstalled = vi.fn();
		renderDialog({ onInstalled });

		await acceptPermissions();
		await waitFor(() => expect(screen.getByTestId("external-app-install-next")).toBeDefined());
		fireEvent.click(screen.getByTestId("external-app-install-next"));

		await waitFor(() => expect(screen.getByTestId("external-app-install-open-existing")).toBeDefined());
		fireEvent.click(screen.getByTestId("external-app-install-open-existing"));

		expect(onInstalled).toHaveBeenCalledWith(externalAppTestIds.otherInstance);
	});
});
