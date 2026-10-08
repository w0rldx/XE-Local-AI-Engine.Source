// @vitest-environment jsdom

import { QueryClient } from "@tanstack/react-query";
import { isRedirect } from "@tanstack/react-router";
import { readdir, readFile } from "node:fs/promises";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { getNodeSettingsQueryKey } from "@/core/api/generated/@tanstack/react-query.gen";
import type { NodeAuthStatusResponse, NodeVaultState } from "@/core/auth/models/NodeAuthModels";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import type { NodeAuthRestoreResult } from "@/core/auth/utils/SessionRestore";

// Both guards are reached through their route module's exported `Route.options.beforeLoad` and called with a
// hand-built context — no render, no router. `restoreNodeAuthSession` is stubbed because these tests are about the
// PROFILE half of each guard; the authenticated case is set up by seeding the auth store instead.
const { restoreMock, pendingVaultStepMock, statusMock } = vi.hoisted(() => ({
	restoreMock: vi.fn<() => Promise<NodeAuthRestoreResult>>(async () => "authenticated"),
	pendingVaultStepMock: vi.fn<() => Promise<"vault-locked" | "vault-setup-required" | undefined>>(async () => undefined),
	statusMock: vi.fn<() => Promise<NodeAuthStatusResponse>>(async () => ({
		setupRequired: false,
		authenticated: false,
		vault: "unlocked",
	})),
}));

vi.mock("@/core/auth/utils/SessionRestore", () => ({
	restoreNodeAuthSession: restoreMock,
	getPendingVaultStep: pendingVaultStepMock,
}));

vi.mock("@/core/auth/api/NodeAuthApi", () => ({ getNodeAuthStatus: statusMock }));

vi.mock("@/core/auth/pages/Login", () => ({ Login: () => null }));
vi.mock("@/core/auth/pages/Setup", () => ({ Setup: () => null }));
vi.mock("@/core/auth/pages/VaultUnlock", () => ({ VaultUnlock: () => null }));
vi.mock("@/core/auth/pages/VaultSetup", () => ({ VaultSetup: () => null }));

// The layout route renders the whole app shell; the guard under test never touches the component, so stubbing it keeps
// this file from importing every page in the tree.
vi.mock("@/core/layout/components/Layout/Layout", () => ({ Layout: () => null }));

vi.mock("@/features/node-settings/pages/ExternalAccessSetup", () => ({ ExternalAccessSetup: () => null }));

vi.mock("@/features/node-settings/pages/UiModeSetup", () => ({ UiModeSetup: () => null }));

vi.mock("@/features/node-settings/pages/SandboxProfileSetup", () => ({ SandboxProfileSetup: () => null }));

import { Route as ExternalAccessRoute } from "@/routes/external-access";
import { Route as LayoutRoute } from "@/routes/_layout";
import { Route as LoginRoute } from "@/routes/login";
import { Route as SandboxProfileSetupRoute } from "@/routes/sandbox-profile-setup";
import { Route as SetupRoute } from "@/routes/setup";
import { Route as UiModeSetupRoute } from "@/routes/ui-mode-setup";
import { Route as VaultRoute } from "@/routes/vault";
import { Route as VaultSetupRoute } from "@/routes/vault-setup";

// A client whose node-settings entry is already resolved, so `ensureQueryData` answers from the cache; the queryFn is
// what a read FAILURE runs.
function clientWith(profile: string | null, uiMode: string | null = "advanced", sandboxSecurityProfile = "high"): QueryClient {
	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
	queryClient.setQueryData(getNodeSettingsQueryKey(), { externalAccessProfile: profile, uiMode, sandboxSecurityProfile });
	return queryClient;
}

function failingClient(): QueryClient {
	return new QueryClient({
		defaultOptions: {
			queries: {
				retry: false,
				queryFn: async () => {
					throw new Error("settings unavailable");
				},
			},
		},
	});
}

const location = { href: "/chat" } as Parameters<NonNullable<typeof LayoutRoute.options.beforeLoad>>[0]["location"];

async function runLayoutGuard(queryClient: QueryClient): Promise<unknown> {
	const beforeLoad = LayoutRoute.options.beforeLoad;
	if (beforeLoad === undefined) {
		throw new Error("the layout route declares no beforeLoad");
	}
	// biome-ignore lint/suspicious/noExplicitAny: the guard reads only `context` and `location` off the router's argument.
	return await (beforeLoad as any)({ context: { queryClient }, location });
}

async function runUiModeGuard(queryClient: QueryClient): Promise<unknown> {
	const beforeLoad = UiModeSetupRoute.options.beforeLoad;
	if (beforeLoad === undefined) {
		throw new Error("the ui-mode-setup route declares no beforeLoad");
	}
	// biome-ignore lint/suspicious/noExplicitAny: same hand-built context as the layout guard.
	return await (beforeLoad as any)({ context: { queryClient }, location });
}

async function runExternalAccessGuard(queryClient: QueryClient): Promise<unknown> {
	const beforeLoad = ExternalAccessRoute.options.beforeLoad;
	if (beforeLoad === undefined) {
		throw new Error("the external-access route declares no beforeLoad");
	}
	// biome-ignore lint/suspicious/noExplicitAny: same hand-built context as the layout guard.
	return await (beforeLoad as any)({ context: { queryClient }, location });
}

// Every guard below reads at most `context`, `location` and `search`; one argument shape serves them all.
async function runGuard(route: { options: { beforeLoad?: unknown } }, queryClient: QueryClient = clientWith("recommended")) {
	const beforeLoad = route.options.beforeLoad;
	if (typeof beforeLoad !== "function") {
		throw new Error("the route declares no beforeLoad");
	}
	return await beforeLoad({ context: { queryClient }, location, search: {} });
}

// Resolves to the redirect target, or "stay" when the guard let the navigation through.
async function guardOutcome(run: Promise<unknown>): Promise<string | undefined> {
	return await run.then(
		() => "stay",
		(thrown: unknown) => redirectTarget(thrown),
	);
}

function statusWith(vault: NodeVaultState, setupRequired = false): NodeAuthStatusResponse {
	return { setupRequired, authenticated: false, vault };
}

function redirectTarget(error: unknown): string | undefined {
	if (!isRedirect(error)) {
		return undefined;
	}
	// TanStack's `redirect()` throws a wrapper carrying its target under `options`, not on the error itself.
	return (error as { options?: { to?: string } }).options?.to;
}

describe("route guards for the first-run external-access and interface-mode choices", () => {
	beforeEach(() => {
		vi.clearAllMocks();
		restoreMock.mockResolvedValue("authenticated");
		pendingVaultStepMock.mockResolvedValue(undefined);
		statusMock.mockResolvedValue(statusWith("unlocked"));
		useNodeAuthStore.getState().actions.setToken({ accessToken: "access-token", expiresAtUtc: "2099-01-01T00:00:00Z" });
	});

	it("redirects an authenticated operator with a pending profile to the external-access route", async () => {
		const error = await runLayoutGuard(clientWith("pending")).then(
			() => undefined,
			(thrown: unknown) => thrown,
		);

		expect(redirectTarget(error)).toBe("/external-access");
	});

	it("lets an authenticated operator with a decided profile through the layout", async () => {
		// Both a chosen preset and the server-stamped "custom" are decisions.
		await expect(runLayoutGuard(clientWith("recommended"))).resolves.toBeUndefined();
		await expect(runLayoutGuard(clientWith("custom"))).resolves.toBeUndefined();
	});

	it("lets the operator through the layout when the settings read fails", async () => {
		// A throw here would make every authenticated page unreachable, which is far worse than a skipped profile screen.
		await expect(runLayoutGuard(failingClient())).resolves.toBeUndefined();
	});

	it("renders the chooser when the external-access route's settings read fails", async () => {
		// The opposite fail-open: this screen has no skip control, so an error boundary here would be a dead end.
		await expect(runExternalAccessGuard(failingClient())).resolves.toBeUndefined();
	});

	it("redirects away from the external-access route once the profile is decided", async () => {
		const error = await runExternalAccessGuard(clientWith("offline")).then(
			() => undefined,
			(thrown: unknown) => thrown,
		);

		expect(redirectTarget(error)).toBe("/");
	});

	it("keeps the chooser open while the profile is still pending", async () => {
		await expect(runExternalAccessGuard(clientWith("pending"))).resolves.toBeUndefined();
	});

	it("sends an unauthenticated visitor of the external-access route to login", async () => {
		useNodeAuthStore.getState().actions.clear();
		restoreMock.mockResolvedValue("unauthenticated");

		const error = await runExternalAccessGuard(clientWith("pending")).then(
			() => undefined,
			(thrown: unknown) => thrown,
		);

		expect(redirectTarget(error)).toBe("/login");
	});

	it("redirects an authenticated operator whose interface mode is unanswered to the ui-mode route", async () => {
		const error = await runLayoutGuard(clientWith("recommended", null)).then(
			() => undefined,
			(thrown: unknown) => thrown,
		);

		expect(redirectTarget(error)).toBe("/ui-mode-setup");
	});

	// Order, not just presence: the two first-run steps must be answered in sequence, or an operator could bounce
	// between them. A node with BOTH unanswered goes to the external-access step first.
	it("answers the external-access question before the interface-mode one", async () => {
		const error = await runLayoutGuard(clientWith("pending", null)).then(
			() => undefined,
			(thrown: unknown) => thrown,
		);

		expect(redirectTarget(error)).toBe("/external-access");
	});

	it("redirects an authenticated operator with a pending sandbox profile to the sandbox-profile route", async () => {
		await expect(guardOutcome(runLayoutGuard(clientWith("recommended", "advanced", "pending")))).resolves.toBe(
			"/sandbox-profile-setup",
		);
	});

	// The sandbox profile is the middle question: after external access, before the interface mode.
	it("asks the sandbox profile after external access and before the interface mode", async () => {
		await expect(guardOutcome(runLayoutGuard(clientWith("pending", null, "pending")))).resolves.toBe("/external-access");
		await expect(guardOutcome(runLayoutGuard(clientWith("recommended", null, "pending")))).resolves.toBe(
			"/sandbox-profile-setup",
		);
		await expect(guardOutcome(runLayoutGuard(clientWith("recommended", null, "low")))).resolves.toBe("/ui-mode-setup");
	});

	it("keeps the sandbox-profile chooser open while the profile is pending", async () => {
		await expect(guardOutcome(runGuard(SandboxProfileSetupRoute, clientWith("recommended", null, "pending")))).resolves.toBe(
			"stay",
		);
	});

	it("redirects away from the sandbox-profile route once the profile is decided", async () => {
		await expect(guardOutcome(runGuard(SandboxProfileSetupRoute, clientWith("recommended", null, "low")))).resolves.toBe("/");
		await expect(guardOutcome(runGuard(SandboxProfileSetupRoute, clientWith("recommended", null, "high")))).resolves.toBe("/");
	});

	it("sends a visitor of the sandbox-profile route back to external access while that is still pending", async () => {
		await expect(guardOutcome(runGuard(SandboxProfileSetupRoute, clientWith("pending", null, "pending")))).resolves.toBe(
			"/external-access",
		);
	});

	it("renders the sandbox-profile chooser when its settings read fails", async () => {
		await expect(guardOutcome(runGuard(SandboxProfileSetupRoute, failingClient()))).resolves.toBe("stay");
	});

	it("sends an unauthenticated visitor of the sandbox-profile route to login", async () => {
		useNodeAuthStore.getState().actions.clear();
		restoreMock.mockResolvedValue("unauthenticated");

		await expect(guardOutcome(runGuard(SandboxProfileSetupRoute, clientWith("recommended", null, "pending")))).resolves.toBe(
			"/login",
		);
	});

	it("lets an authenticated operator with a decided interface mode through the layout", async () => {
		await expect(runLayoutGuard(clientWith("recommended", "simple"))).resolves.toBeUndefined();
		await expect(runLayoutGuard(clientWith("recommended", "advanced"))).resolves.toBeUndefined();
	});

	it("keeps the ui-mode chooser open while the mode is still unanswered", async () => {
		await expect(runUiModeGuard(clientWith("recommended", null))).resolves.toBeUndefined();
	});

	it("redirects away from the ui-mode route once the mode is decided", async () => {
		const error = await runUiModeGuard(clientWith("recommended", "simple")).then(
			() => undefined,
			(thrown: unknown) => thrown,
		);

		expect(redirectTarget(error)).toBe("/");
	});

	it("renders the chooser when the ui-mode route's settings read fails", async () => {
		// The same fail-TOWARD-the-chooser as the external-access route: this screen has no skip control, so an error
		// boundary here would be a dead end.
		await expect(runUiModeGuard(failingClient())).resolves.toBeUndefined();
	});

	it("sends an unauthenticated visitor of the ui-mode route to login", async () => {
		useNodeAuthStore.getState().actions.clear();
		restoreMock.mockResolvedValue("unauthenticated");

		const error = await runUiModeGuard(clientWith("recommended", null)).then(
			() => undefined,
			(thrown: unknown) => thrown,
		);

		expect(redirectTarget(error)).toBe("/login");
	});

	// Above the router the tour provider is alive on /setup, /login and /external-access, and opened its welcome dialog the
	// instant setup stamped a token — on top of the still-unanswered profile chooser. Behind this route's guard it cannot.
	// The mount point is read from source rather than rendered: `autoCodeSplitting` turns the route's `component` into a
	// lazy wrapper whose `?tsr-split` import never resolves under this suite's network stubs.
	it("mounts the onboarding provider inside the guarded layout route, not above the router", async () => {
		// Every file that could mount it above the guard: `App.tsx` and each top-level route module, `__root.tsx` included.
		const routesDir = dirname(fileURLToPath(import.meta.url));
		const routeFiles = (await readdir(routesDir, { withFileTypes: true }))
			.filter((entry) => entry.isFile() && entry.name.endsWith(".tsx"))
			.map((entry) => join(routesDir, entry.name));
		const candidates = [...routeFiles, join(routesDir, "..", "App.tsx")];
		const sources = new Map(await Promise.all(candidates.map(async (path) => [path, await readFile(path, "utf8")] as const)));

		const layoutPath = join(routesDir, "_layout.tsx");
		const mountedIn = [...sources]
			.filter(([, source]) => source.includes("OnboardingProvider"))
			.map(([path]) => path)
			.sort();
		expect(mountedIn).toEqual([layoutPath]);
		expect(sources.get(layoutPath)).toMatch(/<OnboardingProvider>\s*<Layout \/>\s*<\/OnboardingProvider>/);
	});
});

describe("route guards for the vault states", () => {
	beforeEach(() => {
		vi.clearAllMocks();
		pendingVaultStepMock.mockResolvedValue(undefined);
		statusMock.mockResolvedValue(statusWith("unlocked"));
		useNodeAuthStore.getState().actions.clear();
	});

	describe("locked", () => {
		beforeEach(() => {
			restoreMock.mockResolvedValue("vault-locked");
			statusMock.mockResolvedValue(statusWith("locked"));
		});

		it("sends every entry route to the unlock page", async () => {
			await expect(guardOutcome(runGuard(LayoutRoute))).resolves.toBe("/vault");
			await expect(guardOutcome(runGuard(LoginRoute))).resolves.toBe("/vault");
			await expect(guardOutcome(runGuard(SetupRoute))).resolves.toBe("/vault");
			await expect(guardOutcome(runGuard(ExternalAccessRoute, clientWith("pending")))).resolves.toBe("/vault");
			await expect(guardOutcome(runGuard(UiModeSetupRoute, clientWith("recommended", null)))).resolves.toBe("/vault");
			await expect(guardOutcome(runGuard(VaultSetupRoute))).resolves.toBe("/vault");
		});

		it("opens the unlock page", async () => {
			await expect(guardOutcome(runGuard(VaultRoute))).resolves.toBe("stay");
		});

		it("sends a token-holding session to the unlock page from the layout", async () => {
			useNodeAuthStore.getState().actions.setToken({ accessToken: "access-token", expiresAtUtc: "2099-01-01T00:00:00Z" });
			pendingVaultStepMock.mockResolvedValue("vault-locked");

			await expect(guardOutcome(runGuard(LayoutRoute))).resolves.toBe("/vault");
		});
	});

	describe("pending on a node that already has an admin", () => {
		beforeEach(() => {
			restoreMock.mockResolvedValue("vault-setup-required");
			statusMock.mockResolvedValue(statusWith("pending"));
		});

		// Order, not just presence: the vault step comes before the external-access and ui-mode choices.
		it("sends the layout to vault setup before the first-run choices", async () => {
			await expect(guardOutcome(runGuard(LayoutRoute, clientWith("pending", null)))).resolves.toBe("/vault-setup");
		});

		it("sends a session that just signed in to vault setup", async () => {
			useNodeAuthStore.getState().actions.setToken({ accessToken: "access-token", expiresAtUtc: "2099-01-01T00:00:00Z" });
			pendingVaultStepMock.mockResolvedValue("vault-setup-required");

			await expect(guardOutcome(runGuard(LayoutRoute, clientWith("pending", null)))).resolves.toBe("/vault-setup");
			await expect(guardOutcome(runGuard(VaultSetupRoute))).resolves.toBe("stay");
		});

		it("routes login, setup and the first-run choices to vault setup", async () => {
			await expect(guardOutcome(runGuard(LoginRoute))).resolves.toBe("/vault-setup");
			await expect(guardOutcome(runGuard(SetupRoute))).resolves.toBe("/vault-setup");
			await expect(guardOutcome(runGuard(ExternalAccessRoute, clientWith("pending")))).resolves.toBe("/vault-setup");
			await expect(guardOutcome(runGuard(UiModeSetupRoute, clientWith("recommended", null)))).resolves.toBe("/vault-setup");
		});

		it("opens vault setup and keeps the unlock page closed", async () => {
			await expect(guardOutcome(runGuard(VaultSetupRoute))).resolves.toBe("stay");
			await expect(guardOutcome(runGuard(VaultRoute))).resolves.toBe("/");
		});

		it("sends a signed-out visitor of vault setup to login", async () => {
			restoreMock.mockResolvedValue("unauthenticated");

			await expect(guardOutcome(runGuard(VaultSetupRoute))).resolves.toBe("/login");
		});
	});

	describe("unlocked", () => {
		it("closes both vault pages", async () => {
			restoreMock.mockResolvedValue("authenticated");

			await expect(guardOutcome(runGuard(VaultRoute))).resolves.toBe("/");
			await expect(guardOutcome(runGuard(VaultSetupRoute))).resolves.toBe("/");
		});

		it("lets the layout through", async () => {
			restoreMock.mockResolvedValue("authenticated");

			await expect(guardOutcome(runGuard(LayoutRoute))).resolves.toBe("stay");
		});

		it("sends a fresh install's vault setup visitor to setup", async () => {
			restoreMock.mockResolvedValue("setup-required");

			await expect(guardOutcome(runGuard(VaultSetupRoute))).resolves.toBe("/setup");
		});
	});
});
