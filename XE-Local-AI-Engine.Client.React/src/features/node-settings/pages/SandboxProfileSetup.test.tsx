// @vitest-environment jsdom

import { QueryClient } from "@tanstack/react-query";
import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { getNodeSettingsQueryKey } from "@/core/api/generated/@tanstack/react-query.gen";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { SandboxProfileSetup } from "@/features/node-settings/pages/SandboxProfileSetup";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

// Same shape as UiModeSetup.test.tsx: the real i18next instance (so copy is asserted against en.json), the save through
// the MSW seam, and the real layout guard for the cache-seeding case.
const { navigateMock } = vi.hoisted(() => ({ navigateMock: vi.fn(async () => undefined) }));

vi.mock("@tanstack/react-router", async (importOriginal) => ({
	...(await importOriginal<typeof import("@tanstack/react-router")>()),
	useNavigate: () => navigateMock,
}));

vi.mock("@/core/layout/components/Layout/Layout", () => ({ Layout: () => null }));

import { Route as LayoutRoute } from "@/routes/_layout";

setupMswServer();

const settingsPath = localApiPath("node-settings");
const capabilityPath = localApiPath("development/capability");

function capability(highProfileRefusals: string[]) {
	server.use(
		http.get(capabilityPath, () =>
			HttpResponse.json({
				enabled: false,
				sandboxProvider: "process",
				containerRuntime: null,
				isolation: [],
				sandboxSecurityProfile: "pending",
				highProfileRefusals,
			}),
		),
	);
}

function savedSettings(sandboxSecurityProfile: string): Record<string, unknown> {
	return {
		maxMessageRequestTimeoutSeconds: 600,
		minMessageRequestTimeoutSeconds: 5,
		maxAllowedMessageRequestTimeoutSeconds: 3600,
		externalAccessProfile: "recommended",
		uiMode: "advanced",
		sandboxSecurityProfile,
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

describe("SandboxProfileSetup", () => {
	beforeEach(() => {
		vi.clearAllMocks();
	});

	afterEach(() => cleanup());

	it("preselects neither profile and keeps the continue button disabled until one is chosen", () => {
		capability([]);
		renderWithProviders(<SandboxProfileSetup />);

		const radios = screen.getAllByRole("radio");
		expect(radios).toHaveLength(2);
		for (const radio of radios) {
			expect(radio.getAttribute("aria-checked")).toBe("false");
		}
		expect((screen.getByTestId("sandbox-profile-continue") as HTMLButtonElement).disabled).toBe(true);
	});

	it("recommends High when this host would refuse nothing under it", async () => {
		capability([]);
		renderWithProviders(<SandboxProfileSetup />);

		await waitFor(() =>
			expect(screen.getByTestId("sandbox-profile-high-card").textContent).toContain("Recommended for this host"),
		);
		expect(screen.getByTestId("sandbox-profile-low-card").textContent).not.toContain("Recommended");
		expect(screen.getByTestId("sandbox-profile-high-card").getAttribute("aria-checked")).toBe("false");
		expect(screen.queryByTestId("sandbox-profile-refusals")).toBeNull();
	});

	it("recommends Low and lists the refused workloads under High when this host would refuse some", async () => {
		capability(["run_python", "mcp-stdio"]);
		renderWithProviders(<SandboxProfileSetup />);

		await waitFor(() =>
			expect(screen.getByTestId("sandbox-profile-low-card").textContent).toContain("Recommended for this host"),
		);
		const highCard = screen.getByTestId("sandbox-profile-high-card");
		expect(highCard.textContent).not.toContain("Recommended");
		const refusals = screen.getByTestId("sandbox-profile-refusals");
		expect(highCard.contains(refusals)).toBe(true);
		expect(refusals.textContent).toContain("These workloads will refuse on this host:");
		expect(refusals.textContent).toContain("run_python");
		expect(refusals.textContent).toContain("mcp-stdio");
	});

	it("still offers both profiles, without a badge, when the capability read fails", async () => {
		const capabilityRequested = vi.fn();
		server.use(
			http.get(capabilityPath, () => {
				capabilityRequested();
				return HttpResponse.json({ detail: "nope" }, { status: 500 });
			}),
		);
		renderWithProviders(<SandboxProfileSetup />);

		await waitFor(() => expect(capabilityRequested).toHaveBeenCalled());
		expect(screen.getAllByRole("radio")).toHaveLength(2);
		expect(screen.queryByTestId("sandbox-profile-recommended")).toBeNull();
	});

	it("says execution previews stay a separate setting", () => {
		capability([]);
		renderWithProviders(<SandboxProfileSetup />);

		expect(document.body.textContent).toContain("Execution previews stay a separate setting: neither profile turns them on.");
	});

	it("sends only the chosen profile", async () => {
		capability([]);
		const save = captureSave("high");
		renderWithProviders(<SandboxProfileSetup />);

		fireEvent.click(screen.getByTestId("sandbox-profile-high-card"));
		fireEvent.click(screen.getByTestId("sandbox-profile-continue"));

		await waitFor(() => expect(save.body()).toEqual({ sandboxSecurityProfile: "high" }));
	});

	it("sends low when Low is chosen", async () => {
		capability([]);
		const save = captureSave("low");
		renderWithProviders(<SandboxProfileSetup />);

		fireEvent.click(screen.getByTestId("sandbox-profile-low-card"));
		fireEvent.click(screen.getByTestId("sandbox-profile-continue"));

		await waitFor(() => expect(save.body()).toEqual({ sandboxSecurityProfile: "low" }));
	});

	it("navigates on only after the save succeeds", async () => {
		capability([]);
		server.use(http.put(settingsPath, () => HttpResponse.json({ detail: "nope" }, { status: 500 })));
		renderWithProviders(<SandboxProfileSetup />);

		fireEvent.click(screen.getByTestId("sandbox-profile-low-card"));
		fireEvent.click(screen.getByTestId("sandbox-profile-continue"));

		await screen.findByTestId("sandbox-profile-save-error");
		expect(navigateMock).not.toHaveBeenCalled();

		captureSave("low");
		fireEvent.click(screen.getByTestId("sandbox-profile-continue"));

		await waitFor(() => expect(navigateMock).toHaveBeenCalledWith({ to: "/" }));
	});

	it("seeds the settings cache so the layout guard moves on to the next step", async () => {
		capability([]);
		captureSave("high");
		const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
		queryClient.setQueryData(getNodeSettingsQueryKey(), {
			externalAccessProfile: "recommended",
			uiMode: "advanced",
			sandboxSecurityProfile: "pending",
		});
		useNodeAuthStore.getState().actions.setToken({ accessToken: "access-token", expiresAtUtc: "2099-01-01T00:00:00Z" });
		server.use(
			http.get(localApiPath("auth/status"), () =>
				HttpResponse.json({ setupRequired: false, authenticated: true, vault: "unlocked" }),
			),
		);
		renderWithProviders(<SandboxProfileSetup />, { queryClient });

		fireEvent.click(screen.getByTestId("sandbox-profile-high-card"));
		fireEvent.click(screen.getByTestId("sandbox-profile-continue"));
		await waitFor(() => expect(navigateMock).toHaveBeenCalledWith({ to: "/" }));

		// Without `setQueryData` the guard would read the cached "pending" and send the operator straight back here.
		const beforeLoad = LayoutRoute.options.beforeLoad;
		if (beforeLoad === undefined) {
			throw new Error("the layout route declares no beforeLoad");
		}
		// biome-ignore lint/suspicious/noExplicitAny: the guard reads only `context` and `location` off the router argument.
		await expect((beforeLoad as any)({ context: { queryClient }, location: { href: "/" } })).resolves.toBeUndefined();
	});

	it("offers no way to continue without choosing a profile", () => {
		capability([]);
		renderWithProviders(<SandboxProfileSetup />);

		expect(document.querySelectorAll("a[href='/']")).toHaveLength(0);
		expect(screen.queryByRole("button", { name: /skip|later|close/i })).toBeNull();
	});
});
