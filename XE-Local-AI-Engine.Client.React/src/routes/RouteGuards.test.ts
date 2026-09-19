// @vitest-environment jsdom

import { QueryClient } from "@tanstack/react-query";
import { isRedirect } from "@tanstack/react-router";
import { readdir, readFile } from "node:fs/promises";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { getNodeSettingsQueryKey } from "@/core/api/generated/@tanstack/react-query.gen";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";

// Both guards are reached through their route module's exported `Route.options.beforeLoad` and called with a
// hand-built context — no render, no router. `restoreNodeAuthSession` is stubbed because these tests are about the
// PROFILE half of each guard; the authenticated case is set up by seeding the auth store instead.
const { restoreMock } = vi.hoisted(() => ({
	restoreMock: vi.fn<() => Promise<"authenticated" | "unauthenticated" | "setup-required">>(async () => "authenticated"),
}));

vi.mock("@/core/auth/utils/SessionRestore", () => ({
	restoreNodeAuthSession: restoreMock,
}));

// The layout route renders the whole app shell; the guard under test never touches the component, so stubbing it keeps
// this file from importing every page in the tree.
vi.mock("@/core/layout/components/Layout/Layout", () => ({ Layout: () => null }));

vi.mock("@/features/node-settings/pages/ExternalAccessSetup", () => ({ ExternalAccessSetup: () => null }));

vi.mock("@/features/node-settings/pages/UiModeSetup", () => ({ UiModeSetup: () => null }));

import { Route as ExternalAccessRoute } from "@/routes/external-access";
import { Route as LayoutRoute } from "@/routes/_layout";
import { Route as UiModeSetupRoute } from "@/routes/ui-mode-setup";

// A client whose node-settings entry is already resolved, so `ensureQueryData` answers from the cache; the queryFn is
// what a read FAILURE runs.
function clientWith(profile: string | null, uiMode: string | null = "advanced"): QueryClient {
	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
	queryClient.setQueryData(getNodeSettingsQueryKey(), { externalAccessProfile: profile, uiMode });
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
