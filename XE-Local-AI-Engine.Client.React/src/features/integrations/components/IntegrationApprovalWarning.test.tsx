// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { IntegrationApprovalWarning } from "@/features/integrations/components/IntegrationApprovalWarning";
import type { IntegrationToolFacts } from "@/features/integrations/models/IntegrationModels";

vi.mock("react-i18next", () => ({
	useTranslation: () => ({
		t: (_key: string, defaultValue?: string, options?: Record<string, unknown>) => {
			let text = defaultValue ?? _key;
			if (options) {
				for (const [name, value] of Object.entries(options)) {
					text = text.replace(`{{${name}}}`, String(value));
				}
			}
			return text;
		},
	}),
}));

function installJsdomEnvironmentMocks(): void {
	Object.defineProperty(window, "matchMedia", {
		writable: true,
		value: vi.fn().mockImplementation((query: string) => ({
			matches: false,
			media: query,
			onchange: null,
			addEventListener: vi.fn(),
			removeEventListener: vi.fn(),
			dispatchEvent: vi.fn(),
		})),
	});
	Object.defineProperty(window, "ResizeObserver", {
		writable: true,
		value: class ResizeObserverMock {
			observe = vi.fn();

			unobserve = vi.fn();

			disconnect = vi.fn();
		},
	});
}

function catalog(entries: Record<string, IntegrationToolFacts>): ReadonlyMap<string, IntegrationToolFacts> {
	return new Map(Object.entries(entries));
}

function renderWarning(
	allowedToolNames: readonly string[],
	toolApprovals: Record<string, boolean>,
	toolsByName: ReadonlyMap<string, IntegrationToolFacts>,
) {
	return render(
		<MantineProvider>
			<IntegrationApprovalWarning
				allowedToolNames={allowedToolNames}
				toolApprovals={toolApprovals}
				toolsByName={toolsByName}
			/>
		</MantineProvider>,
	);
}

const readLocalNoApproval: IntegrationToolFacts = {
	effectiveRequiresApproval: false,
	category: "ReadLocal",
	unattendedBehaviour: "runs",
};

describe("IntegrationApprovalWarning", () => {
	beforeEach(installJsdomEnvironmentMocks);

	afterEach(cleanup);

	it("is absent when no resolved tool requires approval", () => {
		renderWarning(["read_file"], {}, catalog({ read_file: readLocalNoApproval }));

		expect(screen.queryByTestId("integration-approval-warning")).toBeNull();
	});

	it("names the tools the catalog reports as approval-requiring", () => {
		renderWarning(
			["read_file", "run_command"],
			{},
			catalog({
				read_file: readLocalNoApproval,
				run_command: { effectiveRequiresApproval: true, category: "WriteExecute", unattendedBehaviour: "fails" },
			}),
		);

		expect(screen.getByTestId("integration-approval-warning")).toBeTruthy();
		expect(screen.getByTestId("integration-approval-warning-tools").textContent).toContain("run_command");
		expect(screen.getByTestId("integration-approval-warning-tools").textContent).not.toContain("read_file");
	});

	it("still warns when the node policy requires approval and the agent stores false (tighten-only compose)", () => {
		// The fail-OPEN regression this rule exists to prevent: a `??` here would let a stale per-agent false hide a
		// tool the node policy still gates.
		renderWarning(
			["run_command"],
			{ run_command: false },
			catalog({ run_command: { effectiveRequiresApproval: true, category: "WriteExecute", unattendedBehaviour: "fails" } }),
		);

		expect(screen.getByTestId("integration-approval-warning")).toBeTruthy();
	});

	it("warns for a tool the live catalog does not know (fail-closed)", () => {
		renderWarning(["ghost_tool"], {}, catalog({ read_file: readLocalNoApproval }));

		expect(screen.getByTestId("integration-approval-warning-tools").textContent).toContain("ghost_tool");
	});

	it("warns when the catalog says false but the agent tightens the tool to true", () => {
		renderWarning(["read_file"], { read_file: true }, catalog({ read_file: readLocalNoApproval }));

		expect(screen.getByTestId("integration-approval-warning")).toBeTruthy();
	});

	// D-6: ask_user is approval-gated — that is how the call reaches a human — but an unattended run does NOT stop on
	// it; the coordinator stashes a "not answered" result and the turn continues. Warning about it named a tool that
	// would not have failed the run.
	it("says nothing about a tool an unattended run continues past, even though it is approval-gated", () => {
		renderWarning(
			["ask_user"],
			{},
			catalog({ ask_user: { effectiveRequiresApproval: true, category: "Unknown", unattendedBehaviour: "continuesUnanswered" } }),
		);

		expect(screen.queryByTestId("integration-approval-warning")).toBeNull();
	});

	// The per-agent override cannot turn a continuing tool into a failing one either: the behaviour is the runtime's,
	// not the approval flag's.
	it("keeps a continuing tool out of the warning even when the agent tightens it", () => {
		renderWarning(
			["ask_user", "run_command"],
			{ ask_user: true },
			catalog({
				ask_user: { effectiveRequiresApproval: true, category: "Unknown", unattendedBehaviour: "continuesUnanswered" },
				run_command: { effectiveRequiresApproval: true, category: "WriteExecute", unattendedBehaviour: "fails" },
			}),
		);

		const named = screen.getByTestId("integration-approval-warning-tools").textContent ?? "";
		expect(named).toContain("run_command");
		expect(named).not.toContain("ask_user");
	});

	// An unrecognised behaviour string must not open the gate: a tool that fails an unattended run is approval-gated
	// by construction, so the compose below it still catches it.
	it("still warns for an approval-gated tool whose behaviour value this client does not know", () => {
		renderWarning(
			["future_tool"],
			{},
			catalog({ future_tool: { effectiveRequiresApproval: true, category: "WriteExecute", unattendedBehaviour: "parksForever" } }),
		);

		expect(screen.getByTestId("integration-approval-warning")).toBeTruthy();
	});
});
