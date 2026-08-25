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
	// run_python is the role this case belongs to: it is the one that ASKS for a filesystem boundary, so it is the one
	// whose "No" carries a measured host reason rather than "the role never requested one".
	it("reports a requested-but-unavailable boundary with the measured reason rather than a generic unavailable", () => {
		renderPanel([
			{
				role: "run_python",
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

		expect(screen.getByTestId("sandbox-isolation-level-run_python").textContent).toBe("None");
		expect(screen.getByTestId("sandbox-isolation-backend-run_python").textContent).toBe("process");
		expect(screen.getByTestId("sandbox-isolation-filesystem-run_python").textContent).toBe("No");
		expect(screen.getByTestId("sandbox-isolation-reason-run_python").textContent).toContain("the host is not Linux");
	});

	// This fixture is what the backend actually returns on a Linux host with a working bubblewrap chain — verbatim
	// shapes from DevelopmentContractMapper.ToIsolationSummary, not invented ones. Two roles share ONE provider
	// instance and report different postures, which is the whole reason run_python has a row of its own.
	it("renders one row per role and does not collapse two roles on one provider into one posture", () => {
		renderPanel([
			{
				role: "agent-home",
				provider: "process",
				backend: "process",
				level: "Confined",
				filesystemIsolation: false,
				networkIsolation: true,
				resourceLimits: false,
				readOnlyMounts: true,
				filesystemIsolationUnavailableReason:
					"not requested by this role: 'AgentHome' declares no filesystem boundary, so its commands run in a working-directory jail on the host filesystem and can read whatever the account running the engine can read",
				resourceLimitsUnavailableReason:
					"not requested by this role: 'AgentHome' declares no CPU, memory or process-count ceilings, so a runaway command is bounded only by its timeout and the machine",
			},
			{
				role: "run_python",
				provider: "process",
				backend: "bwrap",
				level: "Isolated",
				filesystemIsolation: true,
				networkIsolation: true,
				resourceLimits: true,
				readOnlyMounts: true,
			},
			{
				role: "mcp-stdio",
				provider: "process",
				backend: "bwrap",
				level: "Isolated",
				filesystemIsolation: true,
				networkIsolation: true,
				// The row that separates the two isolated roles: an MCP server gets the SAME boundary run_python gets
				// and NO ceilings, so a reader cannot take one column as standing for the whole posture.
				resourceLimits: false,
				readOnlyMounts: true,
				resourceLimitsUnavailableReason:
					"not requested by this role: 'McpStdio (sandboxed)' declares no CPU, memory or process-count ceilings, so a runaway command is bounded only by its timeout and the machine",
			},
			{
				role: "development",
				provider: "process",
				backend: "process",
				level: "Confined",
				filesystemIsolation: false,
				networkIsolation: true,
				readOnlyMounts: true,
				resourceLimits: false,
				filesystemIsolationUnavailableReason:
					"not requested by this role: 'DevelopmentMode (host toolchain)' declares no filesystem boundary, so its commands run in a working-directory jail on the host filesystem and can read whatever the account running the engine can read",
				resourceLimitsUnavailableReason:
					"not requested by this role: 'DevelopmentMode (host toolchain)' declares no CPU, memory or process-count ceilings, so a runaway command is bounded only by its timeout and the machine",
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
				filesystemIsolationUnavailableReason:
					"not requested by this role: 'WorkSession' declares no filesystem boundary, so its commands run in a working-directory jail on the host filesystem and can read whatever the account running the engine can read",
				resourceLimitsUnavailableReason:
					"not requested by this role: 'WorkSession' declares no CPU, memory or process-count ceilings, so a runaway command is bounded only by its timeout and the machine",
			},
		]);

		// The regression this fixture pins: Development Mode ran the host toolchain with the worktree mounted while
		// this table said Filesystem "Yes" and Isolated, because it read the provider's flags instead of the role's.
		expect(screen.getByTestId("sandbox-isolation-filesystem-development").textContent).toBe("No");
		expect(screen.getByTestId("sandbox-isolation-level-development").textContent).toBe("Confined");
		expect(screen.getByTestId("sandbox-isolation-backend-development").textContent).toBe("process");

		// Same provider instance, stronger role: only run_python asks for the boundary, so only run_python has one.
		expect(screen.getByTestId("sandbox-isolation-provider-run_python").textContent).toBe("process");
		expect(screen.getByTestId("sandbox-isolation-provider-development").textContent).toBe("process");
		expect(screen.getByTestId("sandbox-isolation-filesystem-run_python").textContent).toBe("Yes");
		expect(screen.getByTestId("sandbox-isolation-level-run_python").textContent).toBe("Isolated");
		expect(screen.getByTestId("sandbox-isolation-backend-run_python").textContent).toBe("bwrap");

		// Two roles on one provider, both isolated, differing on the ceilings axis alone.
		expect(screen.getByTestId("sandbox-isolation-filesystem-mcp-stdio").textContent).toBe("Yes");
		expect(screen.getByTestId("sandbox-isolation-level-mcp-stdio").textContent).toBe("Isolated");
		expect(screen.getByTestId("sandbox-isolation-backend-mcp-stdio").textContent).toBe("bwrap");
		expect(screen.getByTestId("sandbox-isolation-limits-reason-mcp-stdio").textContent).toContain("not requested by this role");

		expect(screen.getByTestId("sandbox-isolation-level-agent-home").textContent).toBe("Confined");
		expect(screen.getByTestId("sandbox-isolation-level-work-session").textContent).toBe("None");

		// Egress IS denied for these roles wherever the backend advertises it, and the table says so rather than
		// letting the filesystem column stand for the whole posture.
		expect(screen.getByTestId("sandbox-isolation-network-development").textContent).toBe("Yes");

		// A role that HAS the boundary has nothing to explain, so it gets no reason line; a role that lacks it says
		// which kind of "No" it is.
		expect(screen.queryByTestId("sandbox-isolation-reason-run_python")).toBeNull();
		expect(screen.getByTestId("sandbox-isolation-reason-development").textContent).toContain("not requested by this role");

		// The second axis the panel used to report as advertised rather than served: this host CAN impose ceilings, and
		// Development Mode asks for none, so the column is No and the reason says which kind of No it is.
		expect(screen.getByTestId("sandbox-isolation-limits-development").textContent).toBe("No");
		expect(screen.getByTestId("sandbox-isolation-limits-reason-development").textContent).toContain(
			"bounded only by its timeout and the machine",
		);
		expect(screen.getByTestId("sandbox-isolation-limits-run_python").textContent).toBe("Yes");
		expect(screen.queryByTestId("sandbox-isolation-limits-reason-run_python")).toBeNull();
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
