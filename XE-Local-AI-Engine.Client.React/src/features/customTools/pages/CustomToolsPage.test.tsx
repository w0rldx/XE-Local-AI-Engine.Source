// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it } from "vitest";

import { ConfirmProvider } from "@/core/ui/components/ConfirmProvider/ConfirmProvider";
import { CustomToolsPage } from "@/features/customTools/pages/CustomToolsPage";
import { useCustomToolManagementStore } from "@/features/customTools/stores/CustomToolManagementStore";
import { jsonRoute, problemDetailsRoute } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

// Smoke coverage only — the page orchestrates a dialog, a confirm flow, an unsaved-changes guard and five query
// hooks, and unit-testing that orchestration would mostly re-test the libraries. What is worth pinning is that the
// page mounts against the real router/query/i18n stack, renders the standing danger banner, and routes the two
// list outcomes (rows vs error) to the right surface.
//
// `withRouter` is required, not decorative: useUnsavedChangesGuard calls TanStack Router's useBlocker, which throws
// without router context. The router mounts asynchronously, hence findBy* for the first assertion.

const toolId = "3a1f0f0e-0000-4000-8000-000000000001";

const listRoute = jsonRoute("get", "custom-tools", {
	items: [
		{
			id: toolId,
			name: "custom__fetch_status",
			description: "Fetches a status page.",
			kind: "HttpFetch",
			mode: "Fixed",
			enabled: true,
			acknowledged: true,
			version: 3,
			createdAtUtc: 1,
			updatedAtUtc: 2,
			parameters: [],
			http: { method: "GET", urlTemplate: "https://example.test", headers: [], bodyTemplate: null, allowedHosts: [] },
			command: null,
		},
	],
});

function renderPage() {
	return renderWithProviders(
		<ConfirmProvider>
			<CustomToolsPage />
		</ConfirmProvider>,
		{ withRouter: true },
	);
}

describe("CustomToolsPage", () => {
	beforeEach(() => {
		useCustomToolManagementStore.getState().actions.closeEditor();
	});

	afterEach(cleanup);

	it("renders the standing danger banner and the served tools", async () => {
		server.use(listRoute);

		renderPage();

		expect(await screen.findByTestId("custom-tools-danger-banner")).toBeTruthy();
		expect(await screen.findByTestId(`custom-tool-row-${toolId}`)).toBeTruthy();
		expect(screen.getByText("custom__fetch_status")).toBeTruthy();
	});

	// A query load-error belongs in an inline Alert, not a toast (agent-knowledge §5 "Error surfacing").
	it("shows the list error inline when the read fails", async () => {
		server.use(problemDetailsRoute("get", "custom-tools", 500, { title: "Server Error", detail: "The tool store is offline." }));

		renderPage();

		const alert = await screen.findByTestId("custom-tools-list-error");
		expect(alert.textContent).toContain("The tool store is offline.");
		expect(screen.queryByTestId("custom-tools-table")).toBeNull();
	});

	it("opens the create editor from the page action", async () => {
		server.use(listRoute);

		renderPage();

		fireEvent.click(await screen.findByTestId("custom-tool-create-button"));

		await waitFor(() => expect(screen.getByTestId("custom-tool-form")).toBeTruthy());
		expect(useCustomToolManagementStore.getState().editorTarget).toEqual({ mode: "create" });
	});

	// The empty state has to survive the same mount path as the populated one.
	it("renders the empty state when the node has no custom tools", async () => {
		server.use(jsonRoute("get", "custom-tools", { items: [] }));

		renderPage();

		expect(await screen.findByTestId("custom-tools-empty")).toBeTruthy();
	});
});
