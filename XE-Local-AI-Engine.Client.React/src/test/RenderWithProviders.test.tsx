// @vitest-environment jsdom

import { useMantineTheme } from "@mantine/core";
import { useQueryClient } from "@tanstack/react-query";
import { useBlocker } from "@tanstack/react-router";
import { cleanup, screen } from "@testing-library/react";
import { useTranslation } from "react-i18next";
import { afterEach, describe, expect, it } from "vitest";

import { createTestQueryClient, renderWithProviders } from "@/test/RenderWithProviders";

// The harness is load-bearing for many suites now, so its own contract gets a check: each provider is really in the
// tree, the QueryClient really has test-safe defaults, and the opt-in router really satisfies useBlocker (the reason
// `withRouter` exists at all — useUnsavedChangesGuard throws without router context).

describe("createTestQueryClient", () => {
	it("disables retries and caching so a test never waits on a backoff or inherits a stale entry", () => {
		const defaults = createTestQueryClient().getDefaultOptions();

		expect(defaults.queries?.retry).toBe(false);
		expect(defaults.queries?.gcTime).toBe(0);
		expect(defaults.mutations?.retry).toBe(false);
	});
});

describe("renderWithProviders", () => {
	/** Probes every context the harness claims to supply and prints what it found. */
	function ContextProbe() {
		const theme = useMantineTheme();
		const queryClient = useQueryClient();
		const { t } = useTranslation();
		return (
			<div data-testid="probe">
				<span data-testid="mantine">{theme.fontFamily ? "mantine" : ""}</span>
				<span data-testid="query">{queryClient.getDefaultOptions().queries?.retry === false ? "query" : ""}</span>
				{/* A key that exists in the real en bundle: proves the app's i18next instance is the active one. */}
				<span data-testid="i18n">{t("common.cancel")}</span>
			</div>
		);
	}

	function BlockerProbe() {
		const { status } = useBlocker({ shouldBlockFn: () => false, withResolver: true, disabled: true });
		return <span data-testid="blocker">{status}</span>;
	}

	afterEach(cleanup);

	it("supplies Mantine, TanStack Query and the app's i18next instance", () => {
		renderWithProviders(<ContextProbe />);

		expect(screen.getByTestId("mantine").textContent).toBe("mantine");
		expect(screen.getByTestId("query").textContent).toBe("query");
		expect(screen.getByTestId("i18n").textContent).toBe("Cancel");
	});

	it("returns the QueryClient it used, and reuses one that was supplied", () => {
		const supplied = createTestQueryClient();

		const fresh = renderWithProviders(<ContextProbe />);
		expect(fresh.queryClient).toBeDefined();
		fresh.unmount();

		const reused = renderWithProviders(<ContextProbe />, { queryClient: supplied });

		expect(reused.queryClient).toBe(supplied);
	});

	// The whole reason the router option exists: useBlocker throws outside router context.
	it("supplies router context only when asked", async () => {
		expect(() => renderWithProviders(<BlockerProbe />)).toThrow();
		cleanup();

		renderWithProviders(<BlockerProbe />, { withRouter: true });

		expect((await screen.findByTestId("blocker")).textContent).toBe("idle");
	});

	it("renders the component at the requested memory-history route", async () => {
		renderWithProviders(<ContextProbe />, { route: "/settings/models" });

		expect(await screen.findByTestId("probe")).toBeTruthy();
	});
});
