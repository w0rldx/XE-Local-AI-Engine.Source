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
import de from "@/locales/de.json";
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

		// The reason is reachable by keyboard: the wrapper takes the tab stop the disabled button cannot.
		const wrapper = screen.getByTestId(`external-app-card-install-${applicationId}`).parentElement as HTMLElement;
		expect(wrapper.getAttribute("tabindex")).toBe("0");
		fireEvent.focus(wrapper);
		expect((await screen.findByRole("tooltip")).textContent).toBe(
			"The container runtime has to be ready before you can install an application.",
		);
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

	it("says the shown catalog may be out of date when the most recent refresh failed", async () => {
		// The GET never attempts a refresh, so its `refreshFailureMessage` is always null; staleness is `lastRefreshFailure`.
		server.use(...baseRoutes(externalAppCatalog({ lastRefreshFailure: "Host unreachable." })));
		renderPage();

		await waitFor(() => expect(screen.getByTestId("external-app-catalog-refresh-failure")).toBeDefined());
		const notice = screen.getByTestId("external-app-catalog-refresh-failure");
		expect(notice.textContent).toContain("could not be refreshed");
		expect(notice.textContent).toContain("Host unreachable.");
	});

	it("reports a refused refresh instead of confirming it when the node keeps the last-good catalog", async () => {
		// The live shape: a 200 carrying the last-good document, the click's failure on the POST body, and the
		// following GET carrying the same sentence as the snapshot's most recent failure.
		const failure = "Catalog refresh failed: the document failed validation (1 errors).";
		let refused = false;
		server.use(
			jsonRoute("get", "external-apps/runtime", externalAppRuntime()),
			http.get(localApiPath("external-apps/catalog"), () =>
				HttpResponse.json(externalAppCatalog({ lastRefreshFailure: refused ? failure : null })),
			),
			http.post(localApiPath("external-apps/catalog/refresh"), () => {
				refused = true;
				return HttpResponse.json(externalAppCatalog({ refreshFailureMessage: failure, lastRefreshFailure: failure }));
			}),
		);
		toastMock.success.mockClear();
		toastMock.error.mockClear();
		renderPage();

		await waitFor(() => expect(screen.getByTestId(`external-app-card-${applicationId}`)).toBeDefined());
		expect(screen.queryByTestId("external-app-catalog-refresh-failure")).toBeNull();
		fireEvent.click(screen.getByTestId("external-app-catalog-refresh"));

		await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith(expect.stringContaining(failure)));
		await waitFor(() => expect(screen.getByTestId("external-app-catalog-refresh-failure").textContent).toContain(failure));
		expect(toastMock.success).not.toHaveBeenCalled();
	});

	it("renders no stale-catalog notice when the refresh succeeded", async () => {
		server.use(...baseRoutes());
		renderPage();

		await waitFor(() => expect(screen.getByTestId(`external-app-card-${applicationId}`)).toBeDefined());
		expect(screen.queryByTestId("external-app-catalog-refresh-failure")).toBeNull();
	});

	// Decision D6: an empty catalog reads as "off" with the way to turn it on, not as a bare empty list, and the
	// runtime card is not the next step there. Refresh stays, because it is how the gate lifts.
	it("shows the enable card instead of the runtime card for an empty catalog", async () => {
		server.use(...baseRoutes(externalAppCatalog({ applications: [], restartRequired: true })));
		renderPage();

		const card = await screen.findByTestId("external-app-catalog-disabled");
		expect(card.getAttribute("data-state")).toBe("emptyAtStartup");
		expect(card.textContent).toContain("External apps stay off until the catalog lists an application");
		expect(card.textContent).toContain("no online catalog is set up");
		expect(card.textContent).toContain("ExternalApps:Catalog:RefreshUrl");
		expect(screen.queryByTestId("external-app-runtime-card")).toBeNull();
		expect(screen.queryByTestId("external-app-catalog-empty")).toBeNull();
		expect(screen.getByTestId("external-app-catalog-refresh")).toBeDefined();
	});

	// The node started on an empty catalog and a refresh has since added entries: installing waits for a restart, so
	// the card says that, and no Install button invites a click the node will refuse.
	it("asks for a restart when the catalog gained entries after an empty-catalog start", async () => {
		server.use(...baseRoutes(externalAppCatalog({ restartRequired: true })));
		renderPage();

		const card = await screen.findByTestId("external-app-catalog-disabled");
		expect(card.textContent).toContain("Restart the node to enable External Apps");
		expect(screen.queryByTestId(`external-app-card-${applicationId}`)).toBeNull();
		expect(screen.queryByTestId("external-app-runtime-card")).toBeNull();
		expect(card.getAttribute("data-state")).toBe("restartRequired");
		// German carries the same instruction, not an untranslated copy of the English one.
		expect(de.pages.externalApps.catalog.restartTitle).toBe("Starten Sie den Node neu, um Externe Apps zu aktivieren");
	});

	// A refresh emptied the catalog after a populated start: the feature is off, but the bridge listener opened at
	// startup is still bound until a restart, and the card must not imply it closed with the catalog.
	it("says the bridge stays open until a restart when a refresh emptied the catalog", async () => {
		server.use(...baseRoutes(externalAppCatalog({ applications: [], bridgeOpenUntilRestart: true })));
		renderPage();

		const card = await screen.findByTestId("external-app-catalog-disabled");
		expect(card.getAttribute("data-state")).toBe("emptyNow");
		const warning = screen.getByTestId("external-app-catalog-bridge-open");
		expect(warning.textContent).toContain("The container bridge stays open until a restart");
		expect(screen.queryByTestId("external-app-runtime-card")).toBeNull();
	});

	// Codex round 2 (P3): a populated start whose bridge never opened, then a refresh to empty. The state is "empty now"
	// and clears on the next refresh that lists an application: it claims no missing online catalog, asks for no
	// restart, and shows no listener warning because no listener is open.
	it("explains a catalog emptied after a populated start without claiming a missing catalog or a restart", async () => {
		server.use(...baseRoutes(externalAppCatalog({ applications: [], refreshUrlConfigured: true })));
		renderPage();

		const card = await screen.findByTestId("external-app-catalog-disabled");
		expect(card.getAttribute("data-state")).toBe("emptyNow");
		expect(card.textContent).toContain("External apps come back without a restart");
		expect(card.textContent).not.toContain("no online catalog is set up");
		expect(card.textContent?.toLowerCase()).not.toContain("restart the node");
		expect(screen.queryByTestId("external-app-catalog-bridge-open")).toBeNull();
		expect(de.pages.externalApps.catalog.emptyNowTitle).toBe("Externe Apps sind aus, solange der Katalog leer ist");
	});

	it("names the configured online catalog when it was empty at startup", async () => {
		server.use(...baseRoutes(externalAppCatalog({ applications: [], restartRequired: true, refreshUrlConfigured: true })));
		renderPage();

		const card = await screen.findByTestId("external-app-catalog-disabled");
		expect(card.getAttribute("data-state")).toBe("emptyAtStartup");
		expect(card.textContent).toContain("the online catalog this node is set up with listed none");
		expect(card.textContent).not.toContain("no online catalog is set up");
	});
});
