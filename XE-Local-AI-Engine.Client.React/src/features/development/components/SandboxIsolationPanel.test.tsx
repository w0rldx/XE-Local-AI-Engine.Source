// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("react-i18next", () => ({
	useTranslation: () => ({ t: (_key: string, fallback?: string) => fallback ?? _key }),
}));

import type { XeLocalAiEngineClientEndpointsDevelopmentV1SandboxIsolationSummaryResponse as SandboxIsolation } from "@/core/api/generated/types.gen";
import { SandboxIsolationPanel } from "@/features/development/components/SandboxIsolationPanel";

function renderPanel(roles: readonly SandboxIsolation[] | undefined) {
	render(
		<MantineProvider>
			<SandboxIsolationPanel roles={roles} />
		</MantineProvider>,
	);
}

// Mantine's provider reads the color scheme from matchMedia, which jsdom does not implement.
beforeEach(() => {
	Object.defineProperty(window, "matchMedia", {
		writable: true,
		value: vi.fn().mockImplementation((query: string) => ({
			matches: false,
			media: query,
			addEventListener: vi.fn(),
			removeEventListener: vi.fn(),
		})),
	});
	// Table.ScrollContainer wraps Mantine's ScrollArea, which observes its own size.
	Object.defineProperty(window, "ResizeObserver", {
		writable: true,
		value: class ResizeObserverMock {
			observe = vi.fn();

			unobserve = vi.fn();

			disconnect = vi.fn();
		},
	});
});

afterEach(cleanup);

describe("SandboxIsolationPanel", () => {
	it("reports an unisolated role with the measured reason rather than a generic unavailable", () => {
		renderPanel([
			{
				role: "agent-home",
				provider: "process",
				backend: "process",
				level: "None",
				filesystemIsolation: false,
				networkIsolation: false,
				resourceLimits: false,
				readOnlyMounts: false,
				filesystemIsolationUnavailableReason: "the host is not Linux (the Windows Job Object path is not implemented)",
			},
		]);

		expect(screen.getByTestId("sandbox-isolation-level-agent-home").textContent).toBe("None");
		expect(screen.getByTestId("sandbox-isolation-backend-agent-home").textContent).toBe("process");
		expect(screen.getByTestId("sandbox-isolation-filesystem-agent-home").textContent).toBe("No");
		expect(screen.getByTestId("sandbox-isolation-reason-agent-home").textContent).toContain("the host is not Linux");
	});

	it("renders one row per role and does not collapse them into a single node-wide claim", () => {
		renderPanel([
			{
				role: "agent-home",
				provider: "process",
				backend: "process",
				level: "Confined",
				filesystemIsolation: false,
				networkIsolation: true,
				resourceLimits: true,
				readOnlyMounts: false,
				filesystemIsolationUnavailableReason: "bwrap is not installed",
			},
			{
				role: "development",
				provider: "docker",
				backend: "docker",
				level: "Isolated",
				filesystemIsolation: true,
				networkIsolation: true,
				resourceLimits: true,
				readOnlyMounts: true,
			},
			{
				role: "work-session",
				provider: "fake",
				backend: "none",
				level: "None",
				filesystemIsolation: false,
				networkIsolation: false,
				resourceLimits: false,
				readOnlyMounts: false,
				filesystemIsolationUnavailableReason: "the deterministic in-memory provider has no mount namespace and never will",
			},
		]);

		expect(screen.getByTestId("sandbox-isolation-level-agent-home").textContent).toBe("Confined");
		expect(screen.getByTestId("sandbox-isolation-level-development").textContent).toBe("Isolated");
		expect(screen.getByTestId("sandbox-isolation-level-work-session").textContent).toBe("None");

		// The mixed-node case the per-role shape exists for: one provider per role, not one posture per node.
		expect(screen.getByTestId("sandbox-isolation-provider-agent-home").textContent).toBe("process");
		expect(screen.getByTestId("sandbox-isolation-provider-development").textContent).toBe("docker");
		expect(screen.getByTestId("sandbox-isolation-readonly-development").textContent).toBe("Yes");

		// An isolated role has nothing to explain, so it gets no reason line. The container-served role is the isolated
		// one here: a hardened container has the host-filesystem boundary the level is derived from, and the process
		// role on a host without bwrap is the one that has to explain itself.
		expect(screen.queryByTestId("sandbox-isolation-reason-development")).toBeNull();
	});

	it("renders nothing rather than an empty table when the backend reported no roles", () => {
		renderPanel([]);

		expect(screen.queryByTestId("sandbox-isolation")).toBeNull();
	});

	it("renders nothing while the capability query is still in flight", () => {
		renderPanel(undefined);

		expect(screen.queryByTestId("sandbox-isolation")).toBeNull();
	});
});
