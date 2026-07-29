// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, render, screen, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { DevelopmentAttemptLiveState } from "@/features/development/hooks/useDevelopmentAttemptHub";
import type { DevelopmentArtifact } from "@/features/development/models/DevelopmentModels";
import type {
	DevelopmentTestOutcome,
	DevelopmentValidationCommand,
	DevelopmentValidationReportBody,
} from "@/features/development/models/DevelopmentValidationReport";

const queriesMock = vi.hoisted(() => ({ useDevelopmentArtifactContent: vi.fn() }));

vi.mock("react-i18next", () => ({
	useTranslation: () => ({ t: (_key: string, fallback?: string) => fallback ?? _key }),
}));
vi.mock("@/features/development/queries/useDevelopment", () => queriesMock);

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

function renderPanel(artifacts: readonly DevelopmentArtifact[] = [validationArtifact]): void {
	render(
		<MantineProvider>
			<DevelopmentLivePanel attempt={null} live={live} artifacts={artifacts} events={[]} />
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

	it("reads the report from the latest valid artifact only", () => {
		contentQuery(JSON.stringify(report()));

		renderPanel([
			{ ...validationArtifact, id: "artifact-old", createdAtUtc: 1000 },
			{ ...validationArtifact, id: "artifact-newest-invalid", createdAtUtc: 9000, isValid: false },
			{ ...validationArtifact, id: "artifact-newest-valid", createdAtUtc: 5000 },
		]);

		expect(queriesMock.useDevelopmentArtifactContent).toHaveBeenCalledWith("project-1", "task-1", "artifact-newest-valid");
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
