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
				networkIsolationRequired: true,
				resourceLimits: false,
				readOnlyMounts: false,
				filesystemIsolationUnavailableReason: "the host is not Linux (the Windows Job Object path is not implemented)",
			},
		]);

		expect(screen.getByTestId("sandbox-isolation-level-run_python").textContent).toBe("None");

		// The combination that means the role will REFUSE TO START on this node rather than run unconfined: denial is
		// a precondition and this host cannot deliver it.
		expect(screen.getByTestId("sandbox-isolation-network-run_python").textContent).toBe("No (required)");
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
				networkIsolationRequired: false,
				resourceLimits: true,
				readOnlyMounts: true,
				filesystemIsolationUnavailableReason:
					"not requested by this role: 'AgentHome' declares no filesystem boundary, so its commands run in a working-directory jail on the host filesystem and can read whatever the account running the engine can read",
			},
			{
				role: "run_python",
				provider: "process",
				backend: "bwrap",
				level: "Isolated",
				filesystemIsolation: true,
				networkIsolation: true,
				networkIsolationRequired: true,
				resourceLimits: true,
				readOnlyMounts: true,
			},
			{
				role: "development",
				provider: "process",
				backend: "process",
				level: "Confined",
				filesystemIsolation: false,
				networkIsolation: true,
				networkIsolationRequired: false,
				readOnlyMounts: true,
				resourceLimits: true,
				filesystemIsolationUnavailableReason:
					"not requested by this role: 'DevelopmentMode (host toolchain)' declares no filesystem boundary, so its commands run in a working-directory jail on the host filesystem and can read whatever the account running the engine can read",
			},
			{
				role: "work-session",
				provider: "fake",
				backend: "none",
				level: "None",
				filesystemIsolation: false,
				networkIsolation: false,
				networkIsolationRequired: false,
				resourceLimits: false,
				readOnlyMounts: false,
				filesystemIsolationUnavailableReason:
					"not requested by this role: 'WorkSession' declares no filesystem boundary, so its commands run in a working-directory jail on the host filesystem and can read whatever the account running the engine can read",
				resourceLimitsUnavailableReason: "the 'fake' sandbox provider does not advertise resource ceilings",
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

		expect(screen.getByTestId("sandbox-isolation-level-agent-home").textContent).toBe("Confined");
		expect(screen.getByTestId("sandbox-isolation-level-work-session").textContent).toBe("None");

		// Egress IS denied for these roles wherever the backend advertises it, and the table says so rather than
		// letting the filesystem column stand for the whole posture. The qualifier is the other half of the sentence:
		// this node did not set Development:Sandbox:RequireEgressDenial, so denial is best-effort here and a host that
		// could not deny would still run the attempt.
		expect(screen.getByTestId("sandbox-isolation-network-development").textContent).toBe("Yes (where available)");

		// run_python needs no switch: its own declaration will not accept egress, so denial is a precondition and the
		// panel says so on the same host, in the same table, next to the role for which it is not.
		expect(screen.getByTestId("sandbox-isolation-network-run_python").textContent).toBe("Yes (required)");

		// A role that HAS the boundary has nothing to explain, so it gets no reason line; a role that lacks it says
		// which kind of "No" it is.
		expect(screen.queryByTestId("sandbox-isolation-reason-run_python")).toBeNull();
		expect(screen.getByTestId("sandbox-isolation-reason-development").textContent).toContain("not requested by this role");

		// The second axis, and the follow-up that changed it: every executing role asks for the node's ceilings now, so
		// on a host that can impose them Development Mode is bounded and carries no reason at all. A role on a backend
		// that cannot impose them still says which kind of No it is — the host's, not the role's.
		expect(screen.getByTestId("sandbox-isolation-limits-development").textContent).toBe("Yes");
		expect(screen.queryByTestId("sandbox-isolation-limits-reason-development")).toBeNull();
		expect(screen.getByTestId("sandbox-isolation-limits-run_python").textContent).toBe("Yes");
		expect(screen.queryByTestId("sandbox-isolation-limits-reason-run_python")).toBeNull();
		expect(screen.getByTestId("sandbox-isolation-limits-work-session").textContent).toBe("No");
		expect(screen.getByTestId("sandbox-isolation-limits-reason-work-session").textContent).toContain(
			"does not advertise resource ceilings",
		);
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
