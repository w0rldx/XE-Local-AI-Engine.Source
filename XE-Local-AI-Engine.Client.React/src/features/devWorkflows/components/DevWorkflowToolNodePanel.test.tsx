// @vitest-environment jsdom

import { cleanup, screen, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { afterEach, describe, expect, it } from "vitest";

import { DevWorkflowToolNodePanel } from "@/features/devWorkflows/components/DevWorkflowToolNodePanel";
import type { DevWorkflowApplyReportBody } from "@/features/devWorkflows/models/DevWorkflowApplyReport";
import type { DevWorkflowValidationReportBody } from "@/features/devWorkflows/models/DevWorkflowValidationReport";
import { devWorkflowNodeRunDetail, devWorkflowTestIds } from "@/features/devWorkflows/test/DevWorkflowFixtures";
import { localApiPath } from "@/test/msw/Handlers";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

const server = setupMswServer();
const artifactId = "77777777-7777-4777-8777-777777777777";

function report(overrides: Partial<DevWorkflowValidationReportBody> = {}): DevWorkflowValidationReportBody {
	return {
		passed: false,
		nodeKey: "validate",
		attempt: 1,
		baseCommit: "abcdef0123456789abcdef",
		commandProfileId: "dotnet-default",
		commandProfileDigest: "0".repeat(64),
		failureCode: null,
		failureDetail: null,
		commands: [],
		completedAtUtc: 1_700_000_000_000,
		...overrides,
	};
}

/** The other document a Tool node can leave behind: an apply node's `<nodeKey>-apply.json` (R-C3). */
function applyReport(overrides: Partial<DevWorkflowApplyReportBody> = {}): DevWorkflowApplyReportBody {
	return {
		passed: true,
		nodeKey: "integrate",
		attempt: 1,
		tasksApplied: 1,
		tasks: [{ nodeKey: "implement#0", taskId: "t-0", title: "Slice one", outcome: "applied", detail: null }],
		completedAtUtc: 1_700_000_000_000,
		...overrides,
	};
}

/** The artifact-content route, answering the body a Tool node's report artifact carries. */
function serveContent(body: unknown): void {
	server.use(
		http.get(localApiPath(`development-workflows/runs/${devWorkflowTestIds.run}/artifacts/${artifactId}/content`), () =>
			HttpResponse.json({ content: typeof body === "string" ? body : JSON.stringify(body), isBase64: false }),
		),
	);
}

function renderPanel(nodeRun = devWorkflowNodeRunDetail({ nodeType: "Tool", primaryArtifactId: artifactId })) {
	renderWithProviders(<DevWorkflowToolNodePanel nodeRun={nodeRun} onShowArtifacts={() => undefined} />);
}

describe("DevWorkflowToolNodePanel", () => {
	afterEach(() => {
		cleanup();
	});

	it("renders a manifest-refusal report's own sentence, never a count that reads as a clean run", async () => {
		// A refusal is a report with no command evidence. "0 commands · 0 tests" over it would read as a validation
		// that ran and found nothing wrong, which is the exact opposite of what happened.
		serveContent(
			report({
				failureCode: "missing_command_evidence",
				failureDetail:
					"Validation was refused: this change touches a dependency manifest and the sandbox has no network to restore it.",
			}),
		);

		renderPanel();

		const failure = await screen.findByTestId("dev-workflow-validation-failure");
		expect(failure.textContent).toContain("dependency manifest");
		expect(screen.getByTestId("dev-workflow-validation-no-commands")).toBeDefined();
		expect(screen.queryByTestId("dev-workflow-validation-tests")).toBeNull();
		expect(screen.queryByTestId("dev-workflow-validation-tests-passed")).toBeNull();
	});

	it("says a node refused before it wrote any report was refused, rather than 'no report yet'", async () => {
		// Nothing is stored when the pass is refused before a command runs, so the row's sanitized sentence is the only
		// account there is — and silence here reads as "nothing wrong".
		renderPanel(
			devWorkflowNodeRunDetail({
				nodeType: "Tool",
				status: "Failed",
				primaryArtifactId: null,
				failureClass: "Policy",
				terminalReason: "The repository has not been acknowledged as trusted.",
			}),
		);

		expect(await screen.findByTestId("dev-workflow-validation-refused")).toBeDefined();
		expect(screen.getByTestId("dev-workflow-validation-refused-reason").textContent).toContain("acknowledged as trusted");
		expect(screen.queryByTestId("dev-workflow-validation-none")).toBeNull();
	});

	it("reads a timed-out node's report as partial and names the clock", async () => {
		serveContent(
			report({
				failureCode: "missing_command_evidence",
				failureDetail: "No evidence was recorded for command dotnet_test_release_no_build.",
				commands: [
					{
						commandId: "dotnet_build_release_no_restore",
						exitCode: 0,
						completed: true,
						outputTruncated: false,
						durationMilliseconds: 4200,
						standardOutput: "Build succeeded.",
						standardError: "",
						testOutcome: null,
					},
				],
			}),
		);

		renderPanel(
			devWorkflowNodeRunDetail({
				nodeType: "Tool",
				primaryArtifactId: artifactId,
				failureClass: "Timeout",
				terminalReason: "This node run did not finish its validation commands within the 900 seconds it was given.",
			}),
		);

		const partial = await screen.findByTestId("dev-workflow-validation-partial");
		expect(partial.textContent).toContain("partial");
		expect(screen.getByTestId("dev-workflow-validation-partial-reason").textContent).toContain("900 seconds");
		// The command that DID run is still evidence and is still shown.
		expect(screen.getByTestId("dev-workflow-validation-command-dotnet_build_release_no_restore")).toBeDefined();
	});

	it("shows per-command exit codes and the parsed test counts", async () => {
		serveContent(
			report({
				passed: true,
				commands: [
					{
						commandId: "dotnet_test_release_no_build",
						exitCode: 0,
						completed: true,
						outputTruncated: false,
						durationMilliseconds: 61_500,
						standardOutput: "Passed!",
						standardError: "",
						testOutcome: {
							adapter: "dotnet-trx",
							parsed: true,
							discovered: 12,
							executed: 12,
							passed: 11,
							failed: 1,
							parseFailureCode: null,
							parseFailureDetail: null,
						},
					},
				],
			}),
		);

		renderPanel();

		expect((await screen.findByTestId("dev-workflow-validation-result")).textContent).toContain("passed");
		expect(screen.getByTestId("dev-workflow-validation-command-dotnet_test_release_no_build").textContent).toContain("exit 0");
		expect(screen.getByTestId("dev-workflow-validation-tests-executed").textContent).toBe("12");
		expect(screen.getByTestId("dev-workflow-validation-tests-failed").textContent).toBe("1");
	});

	it("renders an unreadable body as unreadable instead of as an empty panel", async () => {
		serveContent("not a report at all");

		renderPanel();

		expect(await screen.findByTestId("dev-workflow-validation-unreadable")).toBeDefined();
		expect(screen.queryByTestId("dev-workflow-validation-report")).toBeNull();
		// The raw document is still one click away, which is what the fallback promises.
		expect(screen.getByTestId("dev-workflow-node-tool-report")).toBeDefined();
	});

	it("never prints a test count for an outcome that could not be parsed", async () => {
		serveContent(
			report({
				failureCode: "test_results_unparsed",
				failureDetail: "Command dotnet_test_release_no_build produced no readable test result.",
				commands: [
					{
						commandId: "dotnet_test_release_no_build",
						exitCode: 1,
						completed: true,
						outputTruncated: true,
						durationMilliseconds: 3000,
						standardOutput: "(The captured output was too large for one artifact and was left out of this report.)",
						standardError: "",
						testOutcome: {
							adapter: "dotnet-trx",
							parsed: false,
							discovered: 0,
							executed: 0,
							passed: 0,
							failed: 0,
							parseFailureCode: "summary_not_found",
							parseFailureDetail: "No test summary was found in the command output.",
						},
					},
				],
			}),
		);

		renderPanel();

		expect(await screen.findByTestId("dev-workflow-validation-tests-unparsed")).toBeDefined();
		expect(screen.queryByTestId("dev-workflow-validation-tests")).toBeNull();
		// The server's own elision sentence is what a failing command's captured output says, rendered verbatim.
		await waitFor(() =>
			expect(screen.getByTestId("dev-workflow-validation-output-dotnet_test_release_no_build").textContent).toContain(
				"too large for one artifact",
			),
		);
	});

	it("never paints a previous attempt's report as the current result", async () => {
		// `primaryArtifactId` still points at attempt 1's report until attempt 2's commands land, so without the guard
		// a node that is re-validating renders a green "Validation passed" it has not earned.
		serveContent(
			report({
				passed: true,
				attempt: 1,
				commands: [
					{
						commandId: "dotnet_test_release_no_build",
						exitCode: 0,
						completed: true,
						outputTruncated: false,
						durationMilliseconds: 1000,
						standardOutput: "Passed!",
						standardError: "",
						testOutcome: null,
					},
				],
			}),
		);

		renderPanel(devWorkflowNodeRunDetail({ nodeType: "Tool", attempt: 2, primaryArtifactId: artifactId }));

		expect(await screen.findByTestId("dev-workflow-validation-stale-attempt")).toBeDefined();
		expect(screen.queryByTestId("dev-workflow-validation-result")).toBeNull();
		expect(screen.queryByTestId("dev-workflow-validation-report")).toBeNull();
		// The earlier evidence is not destroyed, only demoted: it stays one click away.
		expect(screen.getByTestId("dev-workflow-node-tool-report")).toBeDefined();
	});
	it("decodes an APPLY node's report instead of calling perfectly good evidence unreadable", async () => {
		// Both documents are artifact kind `Report` and the discriminator is not on the wire, so the body decides. Before
		// this, an apply report reached the validation reader, failed to parse, and rendered as "could not be read".
		serveContent(applyReport());

		renderPanel();

		expect(await screen.findByTestId("dev-workflow-apply-report")).toBeDefined();
		expect(screen.queryByTestId("dev-workflow-validation-unreadable")).toBeNull();
		expect(screen.queryByTestId("dev-workflow-validation-report")).toBeNull();
		// And the panel is named after what it is: this node applied patches, it did not validate anything.
		expect(screen.getByTestId("dev-workflow-node-tool-report").textContent).toBe("Open the apply report");
	});

	it("never reads a partial apply as a whole one — the count is always N of M, and a stop is named", async () => {
		// The applies run in SEQUENCE and stop at the first refusal: after one, the repository is not in the state the
		// next patch was approved against. "1 applied" over that is the false green this panel exists to prevent.
		serveContent(
			applyReport({
				passed: false,
				tasksApplied: 1,
				tasks: [
					{ nodeKey: "implement#0", taskId: "t-0", title: "Slice one", outcome: "applied", detail: null },
					{ nodeKey: "implement#1", taskId: "t-1", title: null, outcome: "blocked", detail: "the patch no longer applies" },
					{ nodeKey: "implement#2", taskId: "t-2", title: "Slice three", outcome: "cancelled", detail: null },
				],
			}),
		);

		renderPanel();

		expect((await screen.findByTestId("dev-workflow-apply-count")).textContent).toBe("1 of 3 patches applied");
		expect(screen.getByTestId("dev-workflow-apply-result").textContent).toBe("The apply did not complete");
		expect(screen.getByTestId("dev-workflow-apply-detail-t-1").textContent).toBe("the patch no longer applies");
		// A task the sequence never reached is not a failed one, and the panel says which it was.
		expect(screen.getByTestId("dev-workflow-apply-stopped")).toBeDefined();
		// A null title falls back to the node key that produced the patch, never to an empty row.
		expect(screen.getByTestId("dev-workflow-apply-task-t-1").textContent).toContain("implement#1");
	});

	it("says a zero-task apply found nothing, rather than showing an empty list under a green badge", async () => {
		// Zero tasks is a PASS server-side: a decomposition may honestly answer "no work needed".
		serveContent(applyReport({ passed: true, tasksApplied: 0, tasks: [] }));

		renderPanel();

		expect(await screen.findByTestId("dev-workflow-apply-no-tasks")).toBeDefined();
		expect(screen.getByTestId("dev-workflow-apply-count").textContent).toBe("0 of 0 patches applied");
	});
});
