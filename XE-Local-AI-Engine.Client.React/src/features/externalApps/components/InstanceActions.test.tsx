// @vitest-environment jsdom

// The concurrency contract, asserted on the wire. Every lifecycle call carries the `version` the row rendered — as a
// body field, and on uninstall as a QUERY parameter, because that request has no body. A dropped token is not a
// rendering bug: it is the one thing standing between two operators and a lost update.

import { fireEvent, screen, waitFor } from "@testing-library/react";
import { HttpResponse, http } from "msw";
import { useState } from "react";
import { describe, expect, it, vi } from "vitest";

import { ConfirmProvider } from "@/core/ui/components/ConfirmProvider/ConfirmProvider";
import { InstanceActions } from "@/features/externalApps/components/InstanceActions";
import type { ExternalAppInstanceSummaryView, ExternalAppInstanceView } from "@/features/externalApps/models/ExternalAppModels";
import {
	externalAppInstance,
	externalAppInstanceSummary,
	externalAppPublishedPort,
	externalAppTestIds,
} from "@/features/externalApps/test/ExternalAppFixtures";
import { localApiPath, problemDetailsRoute } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

const toastMock = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn(), info: vi.fn(), warning: vi.fn() }));
vi.mock("@/core/ui/notifications/Toast", () => ({ toast: toastMock }));

setupMswServer();

const instanceId = externalAppTestIds.instance;

interface CapturedRequest {
	readonly url: string;
	readonly body: unknown;
}

// The response is the 202 admission body (a summary view) or, for cancel, the empty object that route answers; typing
// it keeps a drifted fixture a compile error instead of an opaque JSON blob.
function capture(
	method: "post" | "delete",
	path: string,
	response: ExternalAppInstanceSummaryView | Record<string, never>,
): CapturedRequest[] {
	const requests: CapturedRequest[] = [];
	server.use(
		http[method](localApiPath(path), async ({ request }) => {
			const text = await request.text();
			requests.push({ url: request.url, body: text.length > 0 ? JSON.parse(text) : undefined });
			return HttpResponse.json(response);
		}),
	);
	return requests;
}

function renderActions(instance: ExternalAppInstanceView, onUpdate = vi.fn()) {
	renderWithProviders(
		<ConfirmProvider>
			<InstanceActions instance={instance} onUpdate={onUpdate} />
		</ConfirmProvider>,
	);
	return { onUpdate };
}

/**
 * A row whose version can be moved from the test, the way a hub ping moves it in the app. `rerender` cannot serve
 * here: it replaces the whole tree and would take the provider stack with it.
 */
function MovingRow() {
	const [version, setVersion] = useState(11);
	return (
		<ConfirmProvider>
			<button type="button" data-testid="move-row" onClick={() => setVersion(12)}>
				move
			</button>
			<InstanceActions instance={externalAppInstance({ status: "Stopped", version })} />
		</ConfirmProvider>
	);
}

describe("InstanceActions", () => {
	it("opens the single port that has a URL, not the first published port", () => {
		const open = vi.spyOn(window, "open").mockImplementation(() => null);
		renderActions(
			externalAppInstance({
				publishedPorts: [
					// A sidecar listed FIRST with no address: taking `publishedPorts[0]` would open nothing at all.
					externalAppPublishedPort({ service: "worker", containerPort: 9000, url: null }),
					externalAppPublishedPort({ service: "web", url: "http://127.0.0.1:45123/" }),
				],
			}),
		);

		fireEvent.click(screen.getByTestId("external-app-action-open"));

		expect(open).toHaveBeenCalledWith("http://127.0.0.1:45123/", "_blank", "noopener");
		open.mockRestore();
	});

	it("disables Open when no published port has a URL", () => {
		renderActions(externalAppInstance({ publishedPorts: [externalAppPublishedPort({ url: null })] }));

		expect((screen.getByTestId("external-app-action-open") as HTMLButtonElement).disabled).toBe(true);
	});

	// Stop follows the server's admission rule, not the happy path: `AdmittedStatusFor` takes it from Running, Failed
	// and StoppedUnexpectedly, and on the two failure states it is what tears the leftover containers down. Restart
	// stays Running-only.
	it("offers Start for a stopped, unexpectedly stopped or failed instance, and Stop/Restart while running", () => {
		const { unmount } = renderWithProviders(
			<ConfirmProvider>
				<InstanceActions instance={externalAppInstance({ status: "Stopped" })} />
			</ConfirmProvider>,
		);
		expect(screen.getByTestId("external-app-action-start")).toBeDefined();
		expect(screen.queryByTestId("external-app-action-stop")).toBeNull();
		unmount();

		for (const status of ["Failed", "StoppedUnexpectedly"] as const) {
			const failed = renderWithProviders(
				<ConfirmProvider>
					<InstanceActions instance={externalAppInstance({ status })} />
				</ConfirmProvider>,
			);
			expect(screen.getByTestId("external-app-action-start"), status).toBeDefined();
			expect(screen.getByTestId("external-app-action-stop"), status).toBeDefined();
			expect(screen.queryByTestId("external-app-action-restart"), status).toBeNull();
			failed.unmount();
		}

		renderActions(externalAppInstance({ status: "Running" }));
		expect(screen.getByTestId("external-app-action-stop")).toBeDefined();
		expect(screen.getByTestId("external-app-action-restart")).toBeDefined();
		expect(screen.queryByTestId("external-app-action-start")).toBeNull();
	});

	it("sends the rendered version as expectedVersion when stopping a failed instance", async () => {
		const requests = capture("post", `external-apps/instances/${instanceId}/stop`, externalAppInstanceSummary());
		renderActions(externalAppInstance({ status: "Failed", version: 11 }));

		fireEvent.click(screen.getByTestId("external-app-action-stop"));

		await waitFor(() => expect(requests).toHaveLength(1));
		expect(requests[0]?.body).toEqual({ expectedVersion: 11 });
	});

	it("sends the rendered version as expectedVersion on start", async () => {
		const requests = capture("post", `external-apps/instances/${instanceId}/start`, externalAppInstanceSummary());
		renderActions(externalAppInstance({ status: "Stopped", version: 11 }));

		fireEvent.click(screen.getByTestId("external-app-action-start"));

		await waitFor(() => expect(requests).toHaveLength(1));
		expect(requests[0]?.body).toEqual({ expectedVersion: 11 });
	});

	// hey-api types every member optional, so a row can arrive without a `version`. `?? 0` used to fill it in — and 0 is
	// a REAL version the store seeds rows at, so the guess would have addressed a row rather than being refused.
	it("sends nothing and says so when the row carries no version", async () => {
		const requests = capture("post", `external-apps/instances/${instanceId}/start`, externalAppInstanceSummary());
		toastMock.error.mockClear();
		renderActions(externalAppInstance({ status: "Stopped", version: undefined }));

		fireEvent.click(screen.getByTestId("external-app-action-start"));

		await waitFor(() => expect(toastMock.error).toHaveBeenCalled());
		expect(toastMock.error.mock.calls[0]?.[0]).toContain("has no version to send");
		expect(requests).toHaveLength(0);
	});

	// Same gate on the destructive path: no version means no confirmation dialog either, because it could not be honoured.
	it("does not open the uninstall confirmation when the row carries no version", async () => {
		const requests = capture("delete", `external-apps/instances/${instanceId}`, externalAppInstanceSummary());
		toastMock.error.mockClear();
		renderActions(externalAppInstance({ status: "Stopped", version: undefined }));

		fireEvent.click(screen.getByTestId("external-app-action-uninstall"));

		await waitFor(() => expect(toastMock.error).toHaveBeenCalled());
		expect(screen.queryByTestId("confirm-accept")).toBeNull();
		expect(requests).toHaveLength(0);
	});

	it("sends expectedVersion as a query parameter on uninstall, after the confirmation", async () => {
		const requests = capture("delete", `external-apps/instances/${instanceId}`, externalAppInstanceSummary());
		renderActions(externalAppInstance({ status: "Stopped", version: 11 }));

		fireEvent.click(screen.getByTestId("external-app-action-uninstall"));
		// The friction is the labelled button, verbatim: no typed-name confirmation.
		await waitFor(() => expect(screen.getByTestId("confirm-accept").textContent).toBe("Uninstall and delete data"));
		fireEvent.click(screen.getByTestId("confirm-accept"));

		await waitFor(() => expect(requests).toHaveLength(1));
		expect(new URL(requests[0]?.url ?? "").searchParams.get("expectedVersion")).toBe("11");
		expect(requests[0]?.body).toBeUndefined();
	});

	// The confirmation is AWAITED, so a hub refresh can repaint the row while it is open. The handler that resolves
	// captured the version of the render that opened it: sending that token would post a guaranteed 409, against a
	// row that is no longer the one the dialog described.
	it("refuses a confirmed reset when the row moved while the confirmation was open", async () => {
		const requests = capture("post", `external-apps/instances/${instanceId}/reset`, externalAppInstanceSummary());
		toastMock.error.mockClear();
		renderWithProviders(<MovingRow />);

		fireEvent.click(screen.getByTestId("external-app-action-reset"));
		await waitFor(() => expect(screen.getByTestId("confirm-accept")).toBeDefined());
		fireEvent.click(screen.getByTestId("move-row"));
		fireEvent.click(screen.getByTestId("confirm-accept"));

		await waitFor(() => expect(toastMock.error).toHaveBeenCalled());
		expect(toastMock.error.mock.calls[0]?.[0]).toContain("changed while the confirmation was open");
		expect(requests).toHaveLength(0);
	});

	it("sends the version the row holds when the confirmation resolves, not a captured one", async () => {
		const requests = capture("delete", `external-apps/instances/${instanceId}`, externalAppInstanceSummary());
		renderWithProviders(<MovingRow />);

		fireEvent.click(screen.getByTestId("external-app-action-uninstall"));
		await waitFor(() => expect(screen.getByTestId("confirm-accept")).toBeDefined());
		fireEvent.click(screen.getByTestId("confirm-accept"));

		await waitFor(() => expect(requests).toHaveLength(1));
		expect(new URL(requests[0]?.url ?? "").searchParams.get("expectedVersion")).toBe("11");
	});

	it("sends nothing when the reset confirmation is declined", async () => {
		const requests = capture("post", `external-apps/instances/${instanceId}/reset`, externalAppInstanceSummary());
		renderActions(externalAppInstance({ status: "Stopped" }));

		fireEvent.click(screen.getByTestId("external-app-action-reset"));
		await waitFor(() => expect(screen.getByTestId("confirm-cancel")).toBeDefined());
		fireEvent.click(screen.getByTestId("confirm-cancel"));

		// Nothing asked for, nothing sent — and the assertion is the request log, not the button state.
		await waitFor(() => expect(screen.queryByTestId("confirm-cancel")).toBeNull());
		expect(requests).toHaveLength(0);
	});

	it("offers Cancel only while the instance is busy, and disables the destructive pair there", () => {
		const { unmount } = renderWithProviders(
			<ConfirmProvider>
				<InstanceActions instance={externalAppInstance({ status: "Installing" })} />
			</ConfirmProvider>,
		);
		expect(screen.getByTestId("external-app-action-cancel")).toBeDefined();
		expect((screen.getByTestId("external-app-action-uninstall") as HTMLButtonElement).disabled).toBe(true);
		expect((screen.getByTestId("external-app-action-reset") as HTMLButtonElement).disabled).toBe(true);
		unmount();

		renderActions(externalAppInstance({ status: "Running" }));
		expect(screen.queryByTestId("external-app-action-cancel")).toBeNull();
	});

	it("posts a cancel that carries no expectedVersion, because it targets the operation", async () => {
		const requests = capture("post", `external-apps/instances/${instanceId}/cancel`, {});
		renderActions(externalAppInstance({ status: "Installing", version: 11 }));

		fireEvent.click(screen.getByTestId("external-app-action-cancel"));

		await waitFor(() => expect(requests).toHaveLength(1));
		expect(requests[0]?.body).toBeUndefined();
	});

	it("opens the update dialog through its callback and posts nothing itself", async () => {
		let updatePosts = 0;
		server.use(
			http.post(localApiPath(`external-apps/instances/${instanceId}/update`), () => {
				updatePosts += 1;
				return HttpResponse.json(externalAppInstanceSummary(), { status: 202 });
			}),
		);
		const { onUpdate } = renderActions(externalAppInstance({ status: "Stopped", updateAvailable: true }));

		fireEvent.click(screen.getByTestId("external-app-action-update"));

		expect(onUpdate).toHaveBeenCalledTimes(1);
		expect(updatePosts).toBe(0);
	});

	it("toasts the version-conflict sentence when the rendered token is stale", async () => {
		server.use(
			problemDetailsRoute("post", `external-apps/instances/${instanceId}/start`, 409, {
				detail: "stale",
				// biome-ignore lint/suspicious/noExplicitAny: ProblemDetails does not declare conflictType, which is the field under test.
				...({ conflictType: "ExternalAppVersionConflict" } as any),
			}),
		);
		toastMock.error.mockClear();
		renderActions(externalAppInstance({ status: "Stopped" }));

		fireEvent.click(screen.getByTestId("external-app-action-start"));

		await waitFor(() => expect(toastMock.error).toHaveBeenCalled());
		// The conflict map's sentence, not the server's `detail`: the operator is told what to do, not what broke.
		expect(toastMock.error.mock.calls[0]?.[0]).toContain("changed while this page was open");
	});

	it("hides the destructive pair and the update action in the table's compact row", () => {
		renderWithProviders(
			<ConfirmProvider>
				<InstanceActions instance={externalAppInstance({ status: "Stopped", updateAvailable: true })} compact={true} />
			</ConfirmProvider>,
		);

		expect(screen.getByTestId("external-app-action-start")).toBeDefined();
		expect(screen.queryByTestId("external-app-action-uninstall")).toBeNull();
		expect(screen.queryByTestId("external-app-action-reset")).toBeNull();
		expect(screen.queryByTestId("external-app-action-update")).toBeNull();
	});

	// The installed table's four actions are Open / Start / Stop / Details. Restart belongs to the detail page, where
	// the operator can see what they are restarting.
	it("offers Stop but not Restart on a running instance in the compact row", () => {
		renderWithProviders(
			<ConfirmProvider>
				<InstanceActions instance={externalAppInstance({ status: "Running" })} compact={true} />
			</ConfirmProvider>,
		);

		expect(screen.getByTestId("external-app-action-stop")).toBeDefined();
		expect(screen.queryByTestId("external-app-action-restart")).toBeNull();
	});
});
