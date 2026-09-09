// @vitest-environment jsdom

import { cleanup, screen, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import {
	ValidationCommandCard,
	type ValidationCommandCardLabels,
	type ValidationCommandCardTestIds,
	type ValidationCommandSummary,
} from "@/core/ui/components/ValidationCommandCard/ValidationCommandCard";
import { installJsdomEnvironmentMocks, renderWithMantine as renderWithProviders } from "@/test/MantineTestRender";

const labels: ValidationCommandCardLabels = {
	incomplete: "Did not complete",
	truncated: "Output truncated",
	exitCode: "exit 0",
	capturedOutput: "Captured output",
	testDiscovered: "Tests discovered",
	testExecuted: "Tests executed",
	testPassed: "Tests passed",
	testFailed: "Tests failed",
};

const testIds: ValidationCommandCardTestIds = {
	card: "validation-command-dotnet_test",
	output: "validation-output-dotnet_test",
	testCounts: "validation-test-counts",
	testCountValuePrefix: "validation-test",
};

function command(overrides: Partial<ValidationCommandSummary> = {}): ValidationCommandSummary {
	return {
		commandId: "dotnet_test",
		exitCode: 0,
		completed: true,
		outputTruncated: false,
		durationMilliseconds: 4200,
		standardOutput: "",
		standardError: "",
		testOutcome: null,
		...overrides,
	};
}

describe("ValidationCommandCard", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it("renders the command id, its exit code and its duration", () => {
		renderWithProviders(<ValidationCommandCard command={command()} labels={labels} testIds={testIds} />);

		const card = screen.getByTestId("validation-command-dotnet_test");
		expect(within(card).getByText("dotnet_test")).toBeTruthy();
		expect(within(card).getByText("exit 0")).toBeTruthy();
		expect(within(card).getByText("4.2s")).toBeTruthy();
		expect(within(card).queryByText("Did not complete")).toBeNull();
		expect(within(card).queryByText("Output truncated")).toBeNull();
	});

	it("marks a command that never completed, whatever its exit code says", () => {
		renderWithProviders(<ValidationCommandCard command={command({ completed: false })} labels={labels} testIds={testIds} />);

		expect(screen.getByText("Did not complete")).toBeTruthy();
	});

	it("marks truncated output, because a summary can be missing for that reason alone", () => {
		renderWithProviders(<ValidationCommandCard command={command({ outputTruncated: true })} labels={labels} testIds={testIds} />);

		expect(screen.getByText("Output truncated")).toBeTruthy();
	});

	it("shows a failing command's captured output, which is the only record of why it failed", () => {
		renderWithProviders(
			<ValidationCommandCard
				command={command({ exitCode: 1, standardError: "errno == EROFS", standardOutput: "restoring…" })}
				labels={labels}
				testIds={testIds}
			/>,
		);

		const output = screen.getByTestId("validation-output-dotnet_test");
		expect(output.textContent).toContain("errno == EROFS");
		expect(output.textContent).toContain("restoring…");
		expect(screen.getByText("Captured output")).toBeTruthy();
	});

	it("withholds a passing command's output, where it is noise rather than evidence", () => {
		renderWithProviders(
			<ValidationCommandCard command={command({ standardOutput: "everything is fine" })} labels={labels} testIds={testIds} />,
		);

		expect(screen.queryByTestId("validation-output-dotnet_test")).toBeNull();
	});

	it("puts each parsed count on its own value test id, so four zeroes cannot pass for a real suite", () => {
		renderWithProviders(
			<ValidationCommandCard
				command={command({
					testOutcome: { parsed: true, discovered: 314, executed: 311, passed: 309, failed: 2 },
				})}
				labels={labels}
				testIds={testIds}
			/>,
		);

		expect(screen.getByTestId("validation-test-counts")).toBeTruthy();
		expect(screen.getByTestId("validation-test-discovered").textContent).toBe("314");
		expect(screen.getByTestId("validation-test-executed").textContent).toBe("311");
		expect(screen.getByTestId("validation-test-passed").textContent).toBe("309");
		expect(screen.getByTestId("validation-test-failed").textContent).toBe("2");
	});

	it("renders the caller's parse failure INSTEAD of the counts, never beside them", () => {
		// An unparsed outcome still carries zeroes on the wire. Rendering the grid anyway would print "0 executed,
		// 0 failed" over a run whose results were never read — a false green, which is the whole point of the branch.
		renderWithProviders(
			<ValidationCommandCard
				command={command({
					testOutcome: { parsed: false, discovered: 0, executed: 0, passed: 0, failed: 0 },
				})}
				labels={labels}
				testIds={testIds}
				unparsedOutcome={<div data-testid="parse-failure">summary_not_found</div>}
			/>,
		);

		expect(screen.getByTestId("parse-failure")).toBeTruthy();
		expect(screen.queryByTestId("validation-test-counts")).toBeNull();
		expect(screen.queryByTestId("validation-test-executed")).toBeNull();
	});

	it("renders no counts at all for a command that ran no tests", () => {
		renderWithProviders(<ValidationCommandCard command={command()} labels={labels} testIds={testIds} />);

		expect(screen.queryByTestId("validation-test-counts")).toBeNull();
	});

	it("omits the captured-output caption when the caller supplies none", () => {
		const { capturedOutput: _omitted, ...withoutCaption } = labels;

		renderWithProviders(
			<ValidationCommandCard
				command={command({ exitCode: 1, standardError: "boom" })}
				labels={withoutCaption}
				testIds={testIds}
			/>,
		);

		expect(screen.getByTestId("validation-output-dotnet_test").textContent).toContain("boom");
		expect(screen.queryByText("Captured output")).toBeNull();
	});
});
