// @vitest-environment jsdom

import { QueryClient } from "@tanstack/react-query";
import { isRedirect } from "@tanstack/react-router";
import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { getNodeSettingsQueryKey } from "@/core/api/generated/@tanstack/react-query.gen";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { ExternalAccessSetup } from "@/features/node-settings/pages/ExternalAccessSetup";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

// Renders through `renderWithProviders`, whose `import "@/i18n"` registers the app's real i18next instance — so `t()`
// resolves the REAL en bundle and the copy assertions below are a contract against `src/locales/en.json`, not against
// the inline defaults in the .tsx. That is why this file must NOT `vi.mock("react-i18next")`.
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
function savedSettings(profile: string): Record<string, unknown> {
	return {
		maxMessageRequestTimeoutSeconds: 600,
		minMessageRequestTimeoutSeconds: 5,
		maxAllowedMessageRequestTimeoutSeconds: 3600,
		externalAccessProfile: profile,
		autoCheckApplicationUpdates: profile === "recommended",
		autoCheckRuntimeUpdates: profile === "recommended",
		autoProvisionFirstRunModel: profile === "recommended",
	};
}

function captureSave(profile: string): { body: () => unknown } {
	let observed: unknown;
	server.use(
		http.put(settingsPath, async ({ request }) => {
			observed = await request.json();
			return HttpResponse.json(savedSettings(profile));
		}),
	);
	return { body: () => observed };
}

describe("ExternalAccessSetup", () => {
	beforeEach(() => {
		vi.clearAllMocks();
	});

	afterEach(() => cleanup());

	it("states exactly what the offline profile disables, and that it is not a network switch", () => {
		renderWithProviders(<ExternalAccessSetup />);

		const card = screen.getByTestId("external-access-offline-card");
		const text = card.textContent ?? "";

		// The three things the profile actually turns off.
		expect(text).toContain("application update checks");
		expect(text).toContain("llama.cpp runtime update checks");
		expect(text).toContain("the first-run model and runtime download");
		expect(text).toContain("You can still start every one of them yourself, at any time.");

		// The honesty clause, in the SAME card, naming the three things it does not block.
		expect(text).toContain("This is not a network switch.");
		expect(text).toContain("the C0re platform");
		expect(text).toContain("MCP servers you have configured");
		expect(text).toContain("model-catalog lookups");

		// Overclaiming a privacy guarantee is worse than shipping no switch at all.
		expect(text).not.toMatch(/no external connections|fully offline|airgapped/i);
	});

	it("sends only the recommended profile name and lets the server derive the switches", async () => {
		const save = captureSave("recommended");
		renderWithProviders(<ExternalAccessSetup />);

		fireEvent.click(screen.getByTestId("external-access-choose-recommended"));

		await waitFor(() => expect(save.body()).toEqual({ externalAccessProfile: "recommended" }));
	});

	it("sends only the offline profile name", async () => {
		const save = captureSave("offline");
		renderWithProviders(<ExternalAccessSetup />);

		fireEvent.click(screen.getByTestId("external-access-choose-offline"));

		await waitFor(() => expect(save.body()).toEqual({ externalAccessProfile: "offline" }));
	});

	it("navigates home only after the save succeeds", async () => {
		server.use(http.put(settingsPath, () => HttpResponse.json({ detail: "nope" }, { status: 500 })));
		renderWithProviders(<ExternalAccessSetup />);

		fireEvent.click(screen.getByTestId("external-access-choose-offline"));

		await screen.findByTestId("external-access-save-error");
		expect(navigateMock).not.toHaveBeenCalled();

		captureSave("offline");
		fireEvent.click(screen.getByTestId("external-access-choose-offline"));

		await waitFor(() => expect(navigateMock).toHaveBeenCalledWith({ to: "/" }));
	});

	it("seeds the settings cache so the layout guard passes on the next navigation", async () => {
		// The bounce-back loop this catches: `invalidateQueries` alone marks the entry stale but never refetches a query
		// with no observer, so the layout guard's `ensureQueryData` would read back the "pending" value and send the
		// operator straight back here.
		captureSave("recommended");
		// A real gcTime: the seeded entry has no observer, and the suite's default `gcTime: 0` would evict it before the
		// guard could read it — which is a property of the test client, not of the page.
		const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
		queryClient.setQueryData(getNodeSettingsQueryKey(), { externalAccessProfile: "pending" });
		useNodeAuthStore.getState().actions.setToken({ accessToken: "access-token", expiresAtUtc: "2099-01-01T00:00:00Z" });
		renderWithProviders(<ExternalAccessSetup />, { queryClient });

		fireEvent.click(screen.getByTestId("external-access-choose-recommended"));
		await waitFor(() => expect(navigateMock).toHaveBeenCalledWith({ to: "/" }));

		// The real layout guard, against the very client the page just wrote to. With `setQueryData` removed it reads the
		// stale "pending" entry and throws a redirect straight back to THIS page; with it, the fresh install moves on to
		// the second first-run step instead (its uiMode is still null), which is the whole sequencing contract.
		const beforeLoad = LayoutRoute.options.beforeLoad;
		if (beforeLoad === undefined) {
			throw new Error("the layout route declares no beforeLoad");
		}
		const guardResult =
			await // biome-ignore lint/suspicious/noExplicitAny: the guard reads only `context` and `location` off the router argument.
			(beforeLoad as any)({ context: { queryClient }, location: { href: "/" } }).then(
				() => undefined,
				(thrown: unknown) => thrown,
			);

		expect(isRedirect(guardResult) ? (guardResult as { options?: { to?: string } }).options?.to : undefined).toBe(
			"/ui-mode-setup",
		);
	});

	it("keeps both choices enabled and shows an error when the save fails", async () => {
		server.use(http.put(settingsPath, () => HttpResponse.json({ detail: "nope" }, { status: 500 })));
		renderWithProviders(<ExternalAccessSetup />);

		fireEvent.click(screen.getByTestId("external-access-choose-recommended"));

		await screen.findByTestId("external-access-save-error");
		// No dead end: either choice can still be retried.
		expect((screen.getByTestId("external-access-choose-recommended") as HTMLButtonElement).disabled).toBe(false);
		expect((screen.getByTestId("external-access-choose-offline") as HTMLButtonElement).disabled).toBe(false);
	});

	it("offers no way to continue without choosing a profile", () => {
		renderWithProviders(<ExternalAccessSetup />);

		// Exactly the two choices, and no link out. The language menu is a Mantine menu target, so buttons are counted
		// against the two choice test ids rather than by role alone.
		expect(screen.getByTestId("external-access-choose-recommended")).toBeTruthy();
		expect(screen.getByTestId("external-access-choose-offline")).toBeTruthy();
		expect(document.querySelectorAll("a[href='/']")).toHaveLength(0);
		expect(screen.queryByRole("button", { name: /skip|later|close/i })).toBeNull();
	});
});
