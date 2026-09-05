// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { DevelopmentAttemptLiveState } from "@/features/development/hooks/useDevelopmentAttemptHub";
import type { DevelopmentArtifact, DevelopmentAttempt } from "@/features/development/models/DevelopmentModels";
import type {
	DevelopmentTestOutcome,
	DevelopmentValidationCommand,
	DevelopmentValidationReportBody,
} from "@/features/development/models/DevelopmentValidationReport";

const queriesMock = vi.hoisted(() => ({ useDevelopmentArtifactContent: vi.fn() }));

vi.mock("react-i18next", () => ({
	// Interpolates, unlike the previous fallback-only stub: the prompt rows name their attempt's role through
	// `{{role}}`, and a stub that returned the raw template would have passed while the operator saw literal braces.
	useTranslation: () => ({
		t: (key: string, fallback?: string, options?: Record<string, unknown>) =>
			(fallback ?? key).replace(/{{(\w+)}}/g, (token: string, name: string) => String(options?.[name] ?? token)),
	}),
}));
vi.mock("@/features/development/queries/useDevelopment", () => queriesMock);
// The real viewer lazily boots Monaco, which jsdom cannot host; what this file pins is that the panel hands the
// artifact body and the right language to it.
vi.mock("@/core/ui/components/CodeEditor/CodeEditor", () => ({
	CodeEditor: ({ value, language, "data-testid": testId }: { value: string; language?: string; "data-testid"?: string }) => (
		<pre data-testid={testId} data-language={language}>
			{value}
		</pre>
	),
}));

import { DevelopmentLivePanel } from "@/features/development/components/DevelopmentLivePanel";

const live: DevelopmentAttemptLiveState = {
	connectionState: "idle",
	watermark: 0,
	droppedOrCoalescedUpdateCount: 0,
	latest: null,
	updates: [],
};

const validationArtifact: DevelopmentArtifact = {
	id: "artifact-1",
	projectId: "project-1",
	taskId: "task-1",
	attemptId: "attempt-1",
	kind: "ValidationReport",
	contentHash: "hash",
	byteCount: 512,
	createdAtUtc: 1700,
	isValid: true,
};

const coderAttempt: DevelopmentAttempt = {
	id: "attempt-1",
	taskId: "task-1",
	role: "Coder",
	modelId: "model-1",
	provider: "provider-1",
	status: "Running",
};

function testOutcome(overrides: Partial<DevelopmentTestOutcome> = {}): DevelopmentTestOutcome {
	return {
		adapter: "dotnet",
		parsed: true,
		discovered: 314,
		executed: 311,
		passed: 309,
		failed: 2,
		parseFailureCode: null,
		parseFailureDetail: null,
		...overrides,
	};
}

function command(
	commandId: string,
	overrides: Partial<DevelopmentValidationCommand> = {},
): DevelopmentValidationCommand {
	return {
		commandId,
		exitCode: 0,
		completed: true,
		outputTruncated: false,
		durationMilliseconds: 2400,
		standardOutput: "",
		standardError: "",
		testOutcome: null,
		...overrides,
	};
}

function report(overrides: Partial<DevelopmentValidationReportBody> = {}): DevelopmentValidationReportBody {
	return {
		passed: true,
		baseCommit: "abcdef0123456789",
		subjectHash: "subject",
		manifestHash: "manifest",
		expectedResultHash: "expected",
		commandProfileVersion: "development-validation-v2",
		commandProfileId: "dotnet-slnx",
		commandProfileDigest: "digest",
		failureCode: null,
		failureDetail: null,
		commands: [
			command("dotnet_restore"),
			command("dotnet_build_release_no_restore"),
			command("dotnet_test_release_no_build", { testOutcome: testOutcome() }),
		],
		completedAtUtc: 1700,
		...overrides,
	};
}

function installDomMocks(): void {
	Object.defineProperty(window, "matchMedia", {
		writable: true,
		value: vi.fn().mockImplementation((query: string) => ({
			matches: false,
			media: query,
			addEventListener: vi.fn(),
			removeEventListener: vi.fn(),
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

function renderPanel(
	artifacts: readonly DevelopmentArtifact[] = [validationArtifact],
	attempt: DevelopmentAttempt | null = null,
): void {
	render(
		<MantineProvider>
			<DevelopmentLivePanel attempt={attempt} live={live} artifacts={artifacts} events={[]} />
		</MantineProvider>,
	);
}

function contentQuery(content: string): void {
	queriesMock.useDevelopmentArtifactContent.mockReturnValue({
		data: { artifact: validationArtifact, content },
		isPending: false,
		error: null,
	});
}

describe("DevelopmentLivePanel validation evidence", () => {
	beforeEach(() => {
		installDomMocks();
		vi.clearAllMocks();
		queriesMock.useDevelopmentArtifactContent.mockReturnValue({ data: undefined, isPending: true, error: null });
	});

	afterEach(() => {
		cleanup();
	});

	it("shows the executed, passed and failed counts of a parsed suite to the operator", () => {
		contentQuery(JSON.stringify(report()));

		renderPanel();

		const counts = screen.getByTestId("development-validation-test-counts");
		expect(within(counts).getByText("Tests discovered").nextSibling?.textContent).toBe("314");
		expect(within(counts).getByText("Tests executed").nextSibling?.textContent).toBe("311");
		expect(within(counts).getByText("Tests passed").nextSibling?.textContent).toBe("309");
		expect(within(counts).getByText("Tests failed").nextSibling?.textContent).toBe("2");
		expect(screen.getByTestId("development-validation-result").textContent).toBe("Validation passed");
		expect(screen.getByTestId("development-validation-command-dotnet_test_release_no_build")).toBeTruthy();
	});

	it("reads the newest report whether or not it is still valid", () => {
		// This asserted the opposite until the fix: selecting on `isValid` dropped every FAILED report, because a failed
		// gate invalidates the task's approval evidence. Validity is a presentation decision made from the report's
		// own verdict; it is not a reason to refuse to read the newest report.
		contentQuery(JSON.stringify(report()));

		renderPanel([
			{ ...validationArtifact, id: "artifact-old", createdAtUtc: 1000 },
			{ ...validationArtifact, id: "artifact-newest-invalid", createdAtUtc: 9000, isValid: false },
			{ ...validationArtifact, id: "artifact-older-valid", createdAtUtc: 5000 },
		]);

		expect(queriesMock.useDevelopmentArtifactContent).toHaveBeenCalledWith("project-1", "task-1", "artifact-newest-invalid");
	});

	it("renders a failed validation as failed, with its reason, rather than as nothing having run", () => {
		// The live failure, verbatim: the container gate failed, the timeline read `ValidationFinalized — Failed`, and the
		// Validation tab said "No deterministic validation has run for this task yet." A failed gate invalidates the
		// approval evidence, so `isValid` is false on the very report that carries the answer.
		contentQuery(
			JSON.stringify(
				report({
					passed: false,
					failureCode: "command_failed",
					failureDetail: "Command dotnet_restore exited with code 1.",
					commands: [
						command("dotnet_restore", {
							exitCode: 1,
							standardError:
								'One or more system calls failed: mkdir("[REDACTED:development-path]/shm/session1", AllUsers_ReadWriteExecute) == -1; errno == EROFS;',
						}),
					],
				}),
			),
		);

		renderPanel([{ ...validationArtifact, isValid: false }]);

		expect(screen.queryByTestId("development-validation-no-report")).toBeNull();
		expect(screen.getByTestId("development-validation-result").textContent).toBe("Validation failed");

		const failure = screen.getByTestId("development-validation-failure");
		expect(within(failure).getByText("A validation command exited non-zero.")).toBeTruthy();
		expect(within(failure).getByText("Command dotnet_restore exited with code 1.")).toBeTruthy();

		// The stderr is the only account of WHY, and it was previously withheld even once the report was reachable.
		expect(screen.getByTestId("development-validation-command-output-dotnet_restore").textContent).toContain(
			"errno == EROFS",
		);

		// Staleness is still reported — as its own axis, next to the failure rather than instead of it.
		expect(screen.getByTestId("development-validation-failed-invalidated-note")).toBeTruthy();
	});

	it("keeps a superseded PASS out of the current result instead of presenting its counts", () => {
		// The other half of the same separation. A pass that is no longer current would assert something untrue about
		// the tree it is displayed beside, so its counts stay hidden — but the panel says so explicitly rather than
		// falling through to "nothing ran".
		contentQuery(JSON.stringify(report()));

		renderPanel([{ ...validationArtifact, isValid: false }]);

		expect(screen.getByTestId("development-validation-superseded")).toBeTruthy();
		expect(screen.queryByTestId("development-validation-no-report")).toBeNull();
		expect(screen.queryByTestId("development-validation-test-counts")).toBeNull();
	});

	it("renders the parse failure instead of counts when the adapter could not read the results", () => {
		contentQuery(
			JSON.stringify(
				report({
					passed: false,
					failureCode: "test_results_unparsed",
					failureDetail: "The summary line was absent.",
					commands: [
						command("dotnet_test_release_no_build", {
							testOutcome: testOutcome({ parsed: false, parseFailureCode: "summary_not_found", discovered: 0 }),
						}),
					],
				}),
			),
		);

		renderPanel();

		const parseFailure = screen.getByTestId("development-validation-test-parse-failure");
		expect(within(parseFailure).getByText("summary_not_found")).toBeTruthy();
		expect(within(parseFailure).getByText("No test summary was found in the command output.")).toBeTruthy();
		expect(screen.queryByTestId("development-validation-test-counts")).toBeNull();
		expect(screen.getByTestId("development-validation-failure")).toBeTruthy();
		expect(screen.getByTestId("development-validation-result").textContent).toBe("Validation failed");
	});

	it("reads the no-tests policy case as a reduced guarantee rather than a generic failure", () => {
		contentQuery(
			JSON.stringify(
				report({
					passed: false,
					failureCode: "no_tests_executed",
					failureDetail: null,
					commands: [
						command("dotnet_test_release_no_build", {
							testOutcome: testOutcome({
								parsed: false,
								parseFailureCode: "no_test_projects",
								discovered: 0,
								executed: 0,
								passed: 0,
								failed: 0,
							}),
						}),
					],
				}),
			),
		);

		renderPanel();

		const reason = screen.getByTestId("development-validation-no-tests-reason");
		expect(
			within(reason).getByText(
				"The suite ran but executed no tests, so this run evidences the build and nothing about behaviour.",
			),
		).toBeTruthy();
		const outcome = screen.getByTestId("development-validation-no-tests");
		expect(
			within(outcome).getByText("This repository registers no test project, so there is nothing for validation to execute."),
		).toBeTruthy();
		expect(within(outcome).getByText("A green run here evidences the build only — never behaviour.")).toBeTruthy();
		expect(screen.queryByTestId("development-validation-failure")).toBeNull();
		expect(screen.queryByTestId("development-validation-test-counts")).toBeNull();
	});

	it("fails loud when the stored report body is not readable JSON", () => {
		contentQuery("not-json");

		renderPanel();

		expect(screen.getByTestId("development-validation-unreadable")).toBeTruthy();
		expect(screen.queryByTestId("development-validation-report")).toBeNull();
	});

	it("surfaces a load error inline and reports the absence of any validation run", () => {
		queriesMock.useDevelopmentArtifactContent.mockReturnValue({
			data: undefined,
			isPending: false,
			error: new Error("boom"),
		});

		renderPanel();
		expect(screen.getByTestId("development-validation-load-error")).toBeTruthy();

		cleanup();
		renderPanel([]);
		expect(screen.getByTestId("development-validation-no-report")).toBeTruthy();
		expect(queriesMock.useDevelopmentArtifactContent).toHaveBeenLastCalledWith(undefined, undefined, undefined);
	});
});

describe("DevelopmentLivePanel artifact viewer", () => {
	const patchArtifact: DevelopmentArtifact = { ...validationArtifact, id: "artifact-patch", kind: "Patch" };

	beforeEach(() => {
		installDomMocks();
		vi.clearAllMocks();
		queriesMock.useDevelopmentArtifactContent.mockReturnValue({ data: undefined, isPending: true, error: null });
	});

	afterEach(() => {
		cleanup();
	});

	it("opens the patch artifact as a unified diff in the code viewer on demand", async () => {
		queriesMock.useDevelopmentArtifactContent.mockImplementation((_project, _task, artifactId) => ({
			data: artifactId === "artifact-patch" ? { artifact: patchArtifact, content: "+added line" } : undefined,
			isPending: artifactId !== "artifact-patch",
			error: null,
		}));

		renderPanel([validationArtifact, patchArtifact]);
		expect(screen.queryByTestId("development-artifact-content-artifact-patch")).toBeNull();

		fireEvent.click(screen.getByTestId("development-artifact-view-artifact-patch"));

		const viewer = await screen.findByTestId("development-artifact-content-artifact-patch");
		expect(viewer.getAttribute("data-language")).toBe("diff");
		expect(viewer.textContent).toBe("+added line");

		fireEvent.click(screen.getByTestId("development-artifact-view-artifact-patch"));
		expect(screen.queryByTestId("development-artifact-content-artifact-patch")).toBeNull();
	});

	it("opens stored validation evidence as JSON", async () => {
		contentQuery(JSON.stringify(report()));

		renderPanel();
		fireEvent.click(screen.getByTestId("development-artifact-view-artifact-1"));

		const viewer = await screen.findByTestId("development-artifact-content-artifact-1");
		expect(viewer.getAttribute("data-language")).toBe("json");
	});

	it("opens a recorded prompt from the details tab as plain text", async () => {
		// FU4-1: what the model was TOLD is reachable without knowing an artifact id. It is neither a changed file nor
		// evidence, so it lives in details rather than widening either curated tab's kind pair.
		const promptArtifact: DevelopmentArtifact = { ...validationArtifact, id: "artifact-prompt", kind: "Prompt" };
		queriesMock.useDevelopmentArtifactContent.mockImplementation((_project, _task, artifactId) => ({
			data: artifactId === "artifact-prompt" ? { artifact: promptArtifact, content: "Task: implement the feature" } : undefined,
			isPending: artifactId !== "artifact-prompt",
			error: null,
		}));

		renderPanel([validationArtifact, promptArtifact], coderAttempt);
		fireEvent.click(within(screen.getByTestId("development-prompt-artifacts")).getByTestId("development-artifact-view-artifact-prompt"));

		const viewer = await screen.findByTestId("development-artifact-content-artifact-prompt");
		expect(viewer.getAttribute("data-language")).toBe("plaintext");
		expect(viewer.textContent).toBe("Task: implement the feature");
	});

	it("lists only the displayed attempt's prompt, named by that attempt's role", () => {
		// The live defect: `artifacts` is the whole task's history, so after a coder round and two reviewer rounds the
		// details tab — which identifies ONE attempt — listed every round's prompt as an indistinguishable "Prompt" row
		// beside that attempt's id. Only the displayed attempt's prompt belongs here, and it says whose it is.
		renderPanel(
			[
				validationArtifact,
				{ ...validationArtifact, id: "prompt-coder", kind: "Prompt", attemptId: "attempt-1" },
				{ ...validationArtifact, id: "prompt-reviewer-failed", kind: "Prompt", attemptId: "attempt-2" },
				{ ...validationArtifact, id: "prompt-reviewer-passed", kind: "Prompt", attemptId: "attempt-3" },
			],
			{ ...coderAttempt, id: "attempt-3", role: "Reviewer" },
		);

		const prompts = screen.getByTestId("development-prompt-artifacts");
		expect(within(prompts).getByText("Reviewer prompt")).toBeTruthy();
		expect(within(prompts).getByTestId("development-artifact-view-prompt-reviewer-passed")).toBeTruthy();
		expect(within(prompts).queryByTestId("development-artifact-view-prompt-coder")).toBeNull();
		expect(within(prompts).queryByTestId("development-artifact-view-prompt-reviewer-failed")).toBeNull();
	});

	it("numbers the rows when one attempt recorded more than one prompt", () => {
		renderPanel(
			[
				{ ...validationArtifact, id: "prompt-first", kind: "Prompt", attemptId: "attempt-1" },
				{ ...validationArtifact, id: "prompt-second", kind: "Prompt", attemptId: "attempt-1" },
			],
			coderAttempt,
		);

		const prompts = screen.getByTestId("development-prompt-artifacts");
		expect(within(prompts).getByText("Coder prompt 1")).toBeTruthy();
		expect(within(prompts).getByText("Coder prompt 2")).toBeTruthy();
	});

	it("says so when the displayed attempt recorded no prompt, rather than showing an empty section", () => {
		renderPanel([validationArtifact, { ...validationArtifact, id: "prompt-other", kind: "Prompt", attemptId: "attempt-2" }], coderAttempt);

		const prompts = screen.getByTestId("development-prompt-artifacts");
		expect(within(prompts).getByText("No prompt was recorded for this attempt yet.")).toBeTruthy();
	});
});
