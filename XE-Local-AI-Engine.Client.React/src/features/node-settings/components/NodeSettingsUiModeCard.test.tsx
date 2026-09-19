// @vitest-environment jsdom

import { QueryClient } from "@tanstack/react-query";
import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { afterEach, describe, expect, it, vi } from "vitest";

import { DesktopNavigationBar } from "@/core/layout/components/DesktopNavigationBar/DesktopNavigationBar";
import { NodeSettingsUiModeCard } from "@/features/node-settings/components/NodeSettingsUiModeCard";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

const settingsPath = localApiPath("node-settings");

function settingsResponse(uiMode: string | null): Record<string, unknown> {
	return {
		maxMessageRequestTimeoutSeconds: 600,
		minMessageRequestTimeoutSeconds: 5,
		maxAllowedMessageRequestTimeoutSeconds: 3600,
		externalAccessProfile: "recommended",
		uiMode,
	};
}

// The specific handlers must come BEFORE anything falls through to the spread of base handlers, or the base GET/PUT
// answers first and the node never sees a mode at all.
function stubSettings(initialMode: string | null): { saved: () => unknown } {
	let current = initialMode;
	let observed: unknown;
	server.use(
		http.get(settingsPath, () => HttpResponse.json(settingsResponse(current))),
		http.put(settingsPath, async ({ request }) => {
			observed = await request.json();
			current = (observed as { uiMode?: string }).uiMode ?? current;
			return HttpResponse.json(settingsResponse(current));
		}),
	);
	return { saved: () => observed };
}

// SegmentedControl renders one native radio input per option, so the selected mode is the input that is `checked` —
// there is no aria-checked to read.
function checkedMode(): string | undefined {
	return ["Simple", "Advanced"].find((label) => (screen.getByRole("radio", { name: label }) as HTMLInputElement).checked);
}

describe("NodeSettingsUiModeCard", () => {
	afterEach(() => cleanup());

	it("shows the stored mode without the operator saving anything", async () => {
		stubSettings("simple");
		renderWithProviders(<NodeSettingsUiModeCard />);

		await waitFor(() => expect(checkedMode()).toBe("Simple"));
	});

	it("reads a node that never answered as Advanced", async () => {
		stubSettings(null);
		renderWithProviders(<NodeSettingsUiModeCard />);

		await waitFor(() => expect(checkedMode()).toBe("Advanced"));
	});

	it("saves the moment the mode is picked, with no separate Save step", async () => {
		const stub = stubSettings("advanced");
		renderWithProviders(<NodeSettingsUiModeCard />);

		await screen.findByTestId("node-settings-ui-mode-control");
		fireEvent.click(screen.getByRole("radio", { name: "Simple" }));

		await waitFor(() => expect(stub.saved()).toEqual({ uiMode: "simple" }));
	});

	// The point of the whole feature's "no reload" promise: the card and the rail read the SAME node-settings query, so
	// seeding it from the save's response re-renders the rail on the next commit. Rendered together for that reason.
	it("changes the navigation rail immediately, without a reload", async () => {
		stubSettings("advanced");
		const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
		renderWithProviders(
			<>
				<NodeSettingsUiModeCard />
				<DesktopNavigationBar sideBarCollapsed={false} setSideBarCollapsed={vi.fn()} />
			</>,
			{ queryClient, route: "/" },
		);

		// Advanced first: Benchmarks is a top-level Advanced entry, so its presence is the before-state.
		await screen.findByRole("link", { name: "Benchmarks" });

		fireEvent.click(screen.getByRole("radio", { name: "Simple" }));

		await waitFor(() => expect(screen.queryByRole("link", { name: "Benchmarks" })).toBeNull());
		// ...and a promoted Simple entry has taken a top-level slot, i.e. the rail really re-rendered in Simple mode.
		expect(screen.getByRole("link", { name: "Agents" })).toBeTruthy();
	});

	it("keeps the operator on the page and shows an inline error when the save fails", async () => {
		stubSettings("advanced");
		server.use(http.put(settingsPath, () => HttpResponse.json({ detail: "nope" }, { status: 500 })));
		renderWithProviders(<NodeSettingsUiModeCard />);

		await screen.findByTestId("node-settings-ui-mode-control");
		fireEvent.click(screen.getByRole("radio", { name: "Simple" }));

		await screen.findByTestId("node-settings-ui-mode-error");
		// The failed save left the stored mode alone, so the control still shows it rather than a value nothing holds.
		expect(checkedMode()).toBe("Advanced");
	});
});
