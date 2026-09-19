// @vitest-environment jsdom

import { QueryClient } from "@tanstack/react-query";
import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { getNodeSettingsQueryKey } from "@/core/api/generated/@tanstack/react-query.gen";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { UiModeSetup } from "@/features/node-settings/pages/UiModeSetup";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

// Renders through `renderWithProviders`, whose `import "@/i18n"` registers the app's real i18next instance — so the
// copy assertions below are a contract against `src/locales/en.json`, not against the inline defaults in the .tsx.
// That is why this file must NOT `vi.mock("react-i18next")`.
const { navigateMock } = vi.hoisted(() => ({ navigateMock: vi.fn(async () => undefined) }));

vi.mock("@tanstack/react-router", async (importOriginal) => ({
	...(await importOriginal<typeof import("@tanstack/react-router")>()),
	useNavigate: () => navigateMock,
}));

// The layout route is imported for the cache-seeding test only, to run the REAL guard against the client this page
// just wrote to. Its component renders the whole app shell and is never touched by `beforeLoad`.
vi.mock("@/core/layout/components/Layout/Layout", () => ({ Layout: () => null }));

import { Route as LayoutRoute } from "@/routes/_layout";

setupMswServer();

const settingsPath = localApiPath("node-settings");

// The save goes through the repo's MSW seam rather than a module mock, so these tests assert the JSON that actually
// leaves the browser — which is the contract the server's mapper reads.
function savedSettings(uiMode: string): Record<string, unknown> {
	return {
		maxMessageRequestTimeoutSeconds: 600,
		minMessageRequestTimeoutSeconds: 5,
		maxAllowedMessageRequestTimeoutSeconds: 3600,
		externalAccessProfile: "recommended",
		uiMode,
	};
}

function captureSave(uiMode: string): { body: () => unknown } {
	let observed: unknown;
	// This specific handler must be registered BEFORE anything falls through to the base handlers, or the base PUT
	// answers first and nothing is captured.
	server.use(
		http.put(settingsPath, async ({ request }) => {
			observed = await request.json();
			return HttpResponse.json(savedSettings(uiMode));
		}),
	);
	return { body: () => observed };
}

describe("UiModeSetup", () => {
	beforeEach(() => {
		vi.clearAllMocks();
	});

	afterEach(() => cleanup());

	it("preselects neither option and keeps the continue button disabled until one is chosen", () => {
		renderWithProviders(<UiModeSetup />);

		for (const radio of screen.getAllByRole("radio")) {
			expect(radio.getAttribute("aria-checked")).toBe("false");
		}

		expect((screen.getByTestId("ui-mode-continue") as HTMLButtonElement).disabled).toBe(true);
	});

	it("marks Simple as the recommendation without selecting it", () => {
		renderWithProviders(<UiModeSetup />);

		const simpleCard = screen.getByTestId("ui-mode-simple-card");
		expect(simpleCard.textContent).toContain("Recommended for most people");
		expect(simpleCard.getAttribute("aria-checked")).toBe("false");

		// The badge is a recommendation, not a decision: the Advanced card carries no counter-badge either.
		expect(screen.getByTestId("ui-mode-advanced-card").textContent).not.toContain("Recommended");
	});

	it("says the mode hides nothing permanently and can be changed in Node settings", () => {
		renderWithProviders(<UiModeSetup />);

		const text = document.body.textContent ?? "";
		expect(text).toContain("Nothing is deleted either way, and you can change this later in Node settings.");
	});

	it("exposes both options as one keyboard-operable radio group", () => {
		renderWithProviders(<UiModeSetup />);

		const radios = screen.getAllByRole("radio");
		expect(radios).toHaveLength(2);
		// Focusability is the assertion, not the markup: a card the keyboard cannot reach is a card only a mouse can
		// answer with, and this is a question nobody may skip.
		for (const radio of radios) {
			(radio as HTMLElement).focus();
			expect(document.activeElement).toBe(radio);
		}
	});

	it("sends only the chosen mode", async () => {
		const save = captureSave("simple");
		renderWithProviders(<UiModeSetup />);

		fireEvent.click(screen.getByTestId("ui-mode-simple-card"));
		fireEvent.click(screen.getByTestId("ui-mode-continue"));

		await waitFor(() => expect(save.body()).toEqual({ uiMode: "simple" }));
	});

	it("sends advanced when Advanced is chosen", async () => {
		const save = captureSave("advanced");
		renderWithProviders(<UiModeSetup />);

		fireEvent.click(screen.getByTestId("ui-mode-advanced-card"));
		fireEvent.click(screen.getByTestId("ui-mode-continue"));

		await waitFor(() => expect(save.body()).toEqual({ uiMode: "advanced" }));
	});

	it("navigates home only after the save succeeds", async () => {
		server.use(http.put(settingsPath, () => HttpResponse.json({ detail: "nope" }, { status: 500 })));
		renderWithProviders(<UiModeSetup />);

		fireEvent.click(screen.getByTestId("ui-mode-simple-card"));
		fireEvent.click(screen.getByTestId("ui-mode-continue"));

		await screen.findByTestId("ui-mode-save-error");
		expect(navigateMock).not.toHaveBeenCalled();

		captureSave("simple");
		fireEvent.click(screen.getByTestId("ui-mode-continue"));

		await waitFor(() => expect(navigateMock).toHaveBeenCalledWith({ to: "/" }));
	});

	it("seeds the settings cache so the layout guard passes on the next navigation", async () => {
		// The bounce-back loop this catches: `invalidateQueries` alone marks the entry stale but never refetches a query
		// with no observer, so the layout guard's `ensureQueryData` would read back the null mode and send the operator
		// straight back here.
		captureSave("simple");
		// A real gcTime: the seeded entry has no observer, and the suite's default `gcTime: 0` would evict it before the
		// guard could read it — which is a property of the test client, not of the page.
		const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
		queryClient.setQueryData(getNodeSettingsQueryKey(), { externalAccessProfile: "recommended", uiMode: null });
		useNodeAuthStore.getState().actions.setToken({ accessToken: "access-token", expiresAtUtc: "2099-01-01T00:00:00Z" });
		renderWithProviders(<UiModeSetup />, { queryClient });

		fireEvent.click(screen.getByTestId("ui-mode-simple-card"));
		fireEvent.click(screen.getByTestId("ui-mode-continue"));
		await waitFor(() => expect(navigateMock).toHaveBeenCalledWith({ to: "/" }));

		// The real layout guard, against the very client the page just wrote to. With `setQueryData` removed it reads the
		// stale null mode and throws a redirect straight back to this page.
		const beforeLoad = LayoutRoute.options.beforeLoad;
		if (beforeLoad === undefined) {
			throw new Error("the layout route declares no beforeLoad");
		}
		// biome-ignore lint/suspicious/noExplicitAny: the guard reads only `context` and `location` off the router argument.
		await expect((beforeLoad as any)({ context: { queryClient }, location: { href: "/" } })).resolves.toBeUndefined();
	});

	it("offers no way to continue without choosing a mode", () => {
		renderWithProviders(<UiModeSetup />);

		expect(document.querySelectorAll("a[href='/']")).toHaveLength(0);
		expect(screen.queryByRole("button", { name: /skip|later|close/i })).toBeNull();
	});
});
