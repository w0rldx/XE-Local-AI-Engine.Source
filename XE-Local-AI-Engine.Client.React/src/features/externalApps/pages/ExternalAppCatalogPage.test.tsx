// @vitest-environment jsdom

// The card's installed state is read off the catalog row itself. The assertion that matters here is the NEGATIVE one:
// no instances query is made to find out, because a join would turn an N-application catalog into N+1 reads.

import { fireEvent, screen, waitFor } from "@testing-library/react";
import { HttpResponse, http } from "msw";
import { describe, expect, it, vi } from "vitest";

import { ConfirmProvider } from "@/core/ui/components/ConfirmProvider/ConfirmProvider";
import { ExternalAppCatalogPage } from "@/features/externalApps/pages/ExternalAppCatalogPage";
import {
	externalAppCatalog,
	externalAppInstallPreview,
	externalAppInstance,
	externalAppInstanceSummary,
	externalAppRuntime,
	externalAppSummary,
	externalAppTestIds,
} from "@/features/externalApps/test/ExternalAppFixtures";
import { jsonRoute, localApiPath, problemDetailsRoute } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

const navigate = vi.hoisted(() => vi.fn());
const toastMock = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn(), info: vi.fn(), warning: vi.fn() }));

// Toasts render into the app shell's <Notifications/>, which a page unit test does not mount; the repo's pattern is
// to assert the call, which is also what pins WHICH message was chosen.
vi.mock("@/core/ui/notifications/Toast", () => ({ toast: toastMock }));

// The app router is built from routeTree.gen.ts; a page unit test only needs the navigate CALL, not a route match.
vi.mock("@tanstack/react-router", async (importOriginal) => ({
	...(await importOriginal<typeof import("@tanstack/react-router")>()),
	useNavigate: () => navigate,
}));

setupMswServer();

const applicationId = externalAppTestIds.application;

function renderPage() {
	// The runtime card inside this page asks for a confirmation before trusting a changed daemon.
	return renderWithProviders(
		<ConfirmProvider>
			<ExternalAppCatalogPage />
		</ConfirmProvider>,
	);
}

function baseRoutes(catalog = externalAppCatalog(), runtime = externalAppRuntime()) {
	return [jsonRoute("get", "external-apps/runtime", runtime), jsonRoute("get", "external-apps/catalog", catalog)];
}

describe("ExternalAppCatalogPage", () => {
	it("renders a card with name, summary, tested version and permission chips", async () => {
		server.use(...baseRoutes());
		renderPage();

		await waitFor(() => expect(screen.getByTestId(`external-app-card-${applicationId}`)).toBeDefined());
		const card = screen.getByTestId(`external-app-card-${applicationId}`);
		expect(card.textContent).toContain("ntfy");
		expect(card.textContent).toContain("Send yourself push notifications");
		expect(card.textContent).toContain("Tested version 2.11.0");
		// The fixture grants the internet and nothing else, so exactly one chip belongs on the card.
		expect(screen.getByTestId("external-app-card-permission-internet")).toBeDefined();
		expect(screen.queryByTestId("external-app-card-permission-gpu")).toBeNull();
	});

	it("disables Install while the runtime is not ready", async () => {
		server.use(...baseRoutes(externalAppCatalog(), externalAppRuntime({ status: "DaemonUnreachable", ready: false })));
		renderPage();

		await waitFor(() => expect(screen.getByTestId(`external-app-card-install-${applicationId}`)).toBeDefined());
		expect((screen.getByTestId(`external-app-card-install-${applicationId}`) as HTMLButtonElement).disabled).toBe(true);
	});

	it("shows the status badge and a Details link for an installed application, with no instances query in play", async () => {
		let instanceReads = 0;
		server.use(
			...baseRoutes(
				externalAppCatalog({
					applications: [externalAppSummary({ installedInstanceId: externalAppTestIds.instance, installedStatus: "Running" })],
				}),
			),
			// If the page ever joins against the instances list, this handler records it and the assertion below fails.
			http.get(localApiPath("external-apps/instances"), () => {
				instanceReads += 1;
				return HttpResponse.json({ items: [externalAppInstance()] });
			}),
		);
		renderPage();

		await waitFor(() => expect(screen.getByTestId(`external-app-card-details-${applicationId}`)).toBeDefined());
		expect(screen.getByTestId(`external-app-card-status-${applicationId}`).textContent).toContain("Running");
		expect(screen.queryByTestId(`external-app-card-install-${applicationId}`)).toBeNull();
		expect(instanceReads).toBe(0);

		fireEvent.click(screen.getByTestId(`external-app-card-details-${applicationId}`));
		expect(navigate).toHaveBeenCalledWith({
			to: "/external-apps/instances/$instanceId",
			params: { instanceId: externalAppTestIds.instance },
		});
	});

	it("navigates to the detail route after a successful install", async () => {
		server.use(
			...baseRoutes(),
			jsonRoute("get", `external-apps/catalog/${applicationId}/install-preview`, externalAppInstallPreview()),
			http.post(localApiPath("external-apps/instances"), () =>
				HttpResponse.json(externalAppInstanceSummary({ status: "Installing" }), { status: 202 }),
			),
		);
		navigate.mockClear();
		renderPage();

		await waitFor(() => expect(screen.getByTestId(`external-app-card-install-${applicationId}`)).toBeDefined());
		fireEvent.click(screen.getByTestId(`external-app-card-install-${applicationId}`));

		await waitFor(() => expect((screen.getByTestId("external-app-install-accept") as HTMLButtonElement).disabled).toBe(false));
		fireEvent.click(screen.getByTestId("external-app-install-accept"));
		await waitFor(() => expect(screen.getByTestId("external-app-install-next")).toBeDefined());
		fireEvent.click(screen.getByTestId("external-app-install-next"));
		await waitFor(() => expect(screen.getByTestId("external-app-install-confirm")).toBeDefined());
		fireEvent.click(screen.getByTestId("external-app-install-confirm"));

		await waitFor(() =>
			expect(navigate).toHaveBeenCalledWith({
				to: "/external-apps/instances/$instanceId",
				params: { instanceId: externalAppTestIds.instance },
			}),
		);
	});

	it("reports the server's own message when a catalog refresh fails", async () => {
		server.use(
			...baseRoutes(),
			problemDetailsRoute("post", "external-apps/catalog/refresh", 502, { detail: "The catalog host said no." }),
		);
		renderPage();

		await waitFor(() => expect(screen.getByTestId("external-app-catalog-refresh")).toBeDefined());
		fireEvent.click(screen.getByTestId("external-app-catalog-refresh"));

		// The server's own detail, not the local fallback sentence.
		await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith("The catalog host said no."));
	});

	it("confirms a successful refresh", async () => {
		server.use(...baseRoutes(), jsonRoute("post", "external-apps/catalog/refresh", externalAppCatalog()));
		renderPage();

		await waitFor(() => expect(screen.getByTestId("external-app-catalog-refresh")).toBeDefined());
		fireEvent.click(screen.getByTestId("external-app-catalog-refresh"));

		await waitFor(() => expect(toastMock.success).toHaveBeenCalledWith("Catalog updated."));
	});

	it("says the shown applications are the bundled ones when the online catalog could not be reached", async () => {
		server.use(...baseRoutes(externalAppCatalog({ fromBundledSeed: true, refreshFailureMessage: "Host unreachable." })));
		renderPage();

		await waitFor(() => expect(screen.getByTestId("external-app-catalog-refresh-failure")).toBeDefined());
		const notice = screen.getByTestId("external-app-catalog-refresh-failure");
		expect(notice.textContent).toContain("the online catalog could not be reached");
		expect(notice.textContent).toContain("Host unreachable.");
	});

	it("renders no stale-catalog notice when the refresh succeeded", async () => {
		server.use(...baseRoutes());
		renderPage();

		await waitFor(() => expect(screen.getByTestId(`external-app-card-${applicationId}`)).toBeDefined());
		expect(screen.queryByTestId("external-app-catalog-refresh-failure")).toBeNull();
	});

	it("shows an empty state for an empty catalog", async () => {
		server.use(...baseRoutes(externalAppCatalog({ applications: [] })));
		renderPage();

		await waitFor(() => expect(screen.getByTestId("external-app-catalog-empty")).toBeDefined());
		expect(screen.getByTestId("external-app-catalog-empty").textContent).toBe("No applications are available yet.");
	});
});
