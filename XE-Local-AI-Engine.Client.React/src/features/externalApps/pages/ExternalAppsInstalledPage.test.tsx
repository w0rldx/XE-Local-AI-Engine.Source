// @vitest-environment jsdom

// The list now returns FULL instance views, so a row's address comes off the row itself. The negative assertion is
// the one that matters: no per-instance read is made to fill a column, because removing that fan-out is exactly what
// the widened list is for.

import { fireEvent, screen, waitFor } from "@testing-library/react";
import { HttpResponse, http } from "msw";
import { describe, expect, it, vi } from "vitest";

import { ConfirmProvider } from "@/core/ui/components/ConfirmProvider/ConfirmProvider";
import type { ExternalAppInstanceView } from "@/features/externalApps/models/ExternalAppModels";
import { ExternalAppsInstalledPage } from "@/features/externalApps/pages/ExternalAppsInstalledPage";
import {
	externalAppInstance,
	externalAppInstancesResponse,
	externalAppInstanceSummary,
	externalAppPublishedPort,
	externalAppTestIds,
} from "@/features/externalApps/test/ExternalAppFixtures";
import { jsonRoute, localApiPath } from "@/test/msw/Handlers";
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

setupMswServer();

const instanceId = externalAppTestIds.instance;

function renderPage() {
	renderWithProviders(
		<ConfirmProvider>
			<ExternalAppsInstalledPage />
		</ConfirmProvider>,
	);
}

/** The list route, answered with the generated envelope: a row that is not a full view fails tsc. */
function listRoute(items: readonly ExternalAppInstanceView[]) {
	return jsonRoute("get", "external-apps/instances", externalAppInstancesResponse(items));
}

describe("ExternalAppsInstalledPage", () => {
	it("renders a row's badge, version and address without reading the instance again", async () => {
		let instanceReads = 0;
		server.use(
			listRoute([externalAppInstance({ publishedPorts: [externalAppPublishedPort({ url: "http://127.0.0.1:45123/" })] })]),
			// If a column is ever filled by a per-instance GET, this handler records it and the assertion below fails.
			http.get(localApiPath(`external-apps/instances/${instanceId}`), () => {
				instanceReads += 1;
				return HttpResponse.json(externalAppInstance());
			}),
		);
		renderPage();

		await waitFor(() => expect(screen.getByTestId(`external-app-row-${instanceId}`)).toBeDefined());
		expect(screen.getByTestId(`external-app-row-status-${instanceId}`).textContent).toContain("Running");
		expect(screen.getByTestId(`external-app-row-address-${instanceId}`).textContent).toContain("127.0.0.1:45123");
		expect(screen.getByTestId(`external-app-row-${instanceId}`).textContent).toContain("ntfy");
		expect(instanceReads).toBe(0);
	});

	it("falls back to the pending line for a row with no address yet", async () => {
		server.use(listRoute([externalAppInstance({ status: "Installing", publishedPorts: [] })]));
		renderPage();

		await waitFor(() => expect(screen.getByTestId(`external-app-row-address-${instanceId}`)).toBeDefined());
		expect(screen.getByTestId(`external-app-row-address-${instanceId}`).textContent).toContain("Assigned when");
	});

	it("fires the start mutation from a row action", async () => {
		let startRequests = 0;
		server.use(
			listRoute([externalAppInstance({ status: "Stopped", version: 11 })]),
			http.post(localApiPath(`external-apps/instances/${instanceId}/start`), async ({ request }) => {
				expect(await request.json()).toEqual({ expectedVersion: 11 });
				startRequests += 1;
				return HttpResponse.json(externalAppInstanceSummary(), { status: 202 });
			}),
		);
		renderPage();
		await waitFor(() => expect(screen.getByTestId("external-app-action-start")).toBeDefined());

		fireEvent.click(screen.getByTestId("external-app-action-start"));

		await waitFor(() => expect(startRequests).toBe(1));
	});

	it("navigates to the detail route from a row", async () => {
		server.use(listRoute([externalAppInstance()]));
		navigate.mockClear();
		renderPage();
		await waitFor(() => expect(screen.getByTestId(`external-app-row-details-${instanceId}`)).toBeDefined());

		fireEvent.click(screen.getByTestId(`external-app-row-details-${instanceId}`));

		expect(navigate).toHaveBeenCalledWith({ to: "/external-apps/instances/$instanceId", params: { instanceId } });
	});

	it("offers the catalog from the empty state", async () => {
		server.use(listRoute([]));
		navigate.mockClear();
		renderPage();
		await waitFor(() => expect(screen.getByTestId("external-apps-installed-empty")).toBeDefined());

		fireEvent.click(screen.getByTestId("external-apps-installed-browse"));

		expect(navigate).toHaveBeenCalledWith({ to: "/external-apps/catalog" });
	});
});
