import { Alert, Badge, Code, Group, Loader, Stack, Text } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import { useMemo } from "react";
import { useTranslation } from "react-i18next";

import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { ValidationCommandCard } from "@/core/ui/components/ValidationCommandCard/ValidationCommandCard";
import type { DevelopmentArtifact } from "@/features/development/models/DevelopmentModels";
import {
	type DevelopmentTestOutcome,
	type DevelopmentValidationCommand,
	isDevelopmentNoTestsCode,
	parseDevelopmentValidationReport,
} from "@/features/development/models/DevelopmentValidationReport";
import { useDevelopmentArtifactContent } from "@/features/development/queries/useDevelopment";

/**
 * The stable report-level failure codes, paired with the sentence an operator can act on. The raw code is always shown
 * next to the sentence, and an unknown code falls back to the code itself rather than to silence.
 */
const validationFailureLabels: Readonly<Record<string, readonly [string, string]>> = {
	command_failed: ["pages.development.validation.failure.commandFailed", "A validation command exited non-zero."],
	command_did_not_complete: [
		"pages.development.validation.failure.commandDidNotComplete",
		"A validation command did not run to completion.",
	],
	missing_command_evidence: [
		"pages.development.validation.failure.missingCommandEvidence",
		"The report is missing evidence for a command this profile requires.",
	],
	test_results_unparsed: [
		"pages.development.validation.failure.testResultsUnparsed",
		"The test results could not be parsed, so no test count in this run can be trusted.",
	],
	no_tests_executed: [
		"pages.development.validation.failure.noTestsExecuted",
		"The suite ran but executed no tests, so this run evidences the build and nothing about behaviour.",
	],
	tests_failed: ["pages.development.validation.failure.testsFailed", "At least one test failed."],
};

/** The per-outcome parse failure codes. A parse failure is a validation failure, never missing data. */
const validationParseFailureLabels: Readonly<Record<string, readonly [string, string]>> = {
	no_test_projects: [
		"pages.development.validation.parseFailure.noTestProjects",
		"This repository registers no test project, so there is nothing for validation to execute.",
	],
	summary_not_found: [
		"pages.development.validation.parseFailure.summaryNotFound",
		"No test summary was found in the command output.",
	],
	summary_incomplete: ["pages.development.validation.parseFailure.summaryIncomplete", "The test summary was incomplete."],
	summary_inconsistent: [
		"pages.development.validation.parseFailure.summaryInconsistent",
		"The test summary counts did not add up.",
	],
	output_truncated: [
		"pages.development.validation.parseFailure.outputTruncated",
		"The command output was truncated before the test summary was written.",
	],
};

/** Defensive: the report body is parsed from an opaque string, so a hash field can be absent at runtime. */
function shortHash(value: string | undefined): string {
	return typeof value === "string" && value.length > 12 ? `${value.slice(0, 12)}…` : (value ?? "—");
}

/**
 * The parse-failure half of a test outcome. The counts half is the shared `ValidationCommandCard`; this stays local
 * because "no test project" is the registered-repository policy case, not a broken adapter: it reads as a reduced
 * guarantee, whereas every other parse failure reads as a failed run.
 */
function ValidationTestParseFailure({ outcome }: { readonly outcome: DevelopmentTestOutcome }) {
	const { t } = useTranslation();
	const noTests = isDevelopmentNoTestsCode(outcome.parseFailureCode);
	const label = outcome.parseFailureCode === null ? undefined : validationParseFailureLabels[outcome.parseFailureCode];

	return (
		<Alert
			mt="sm"
			color={noTests ? "yellow" : "red"}
			icon={<IconAlertTriangle size={16} />}
			title={
				noTests
					? t("pages.development.validation.tests.noTestsTitle", "No tests to execute")
					: t("pages.development.validation.tests.unparsedTitle", "Test results could not be parsed")
			}
			data-testid={noTests ? "development-validation-no-tests" : "development-validation-test-parse-failure"}
		>
			<Stack gap={4}>
				<Text size="sm">{label ? t(label[0], label[1]) : (outcome.parseFailureCode ?? "")}</Text>
				<Text size="xs" c="dimmed">
					{noTests
						? t(
								"pages.development.validation.tests.noTestsConsequence",
								"A green run here evidences the build only — never behaviour.",
							)
						: t(
								"pages.development.validation.tests.unparsedConsequence",
								"No executed, passed or failed count is available for this run.",
							)}
				</Text>
				<Code>{outcome.parseFailureCode ?? "unknown"}</Code>
				{outcome.parseFailureDetail ? (
					<Text size="xs" c="dimmed">
						{outcome.parseFailureDetail}
					</Text>
				) : null}
			</Stack>
		</Alert>
	);
}

function ValidationFailureAlert({ code, detail }: { readonly code: string; readonly detail: string | null }) {
	const { t } = useTranslation();
	const noTests = isDevelopmentNoTestsCode(code);
	const label = validationFailureLabels[code];

	return (
		<Alert
			color={noTests ? "yellow" : "red"}
			icon={<IconAlertTriangle size={16} />}
			title={
				noTests
					? t("pages.development.validation.noTestsTitle", "Validation executed no tests")
					: t("pages.development.validation.failedTitle", "Validation failed")
			}
			data-testid={noTests ? "development-validation-no-tests-reason" : "development-validation-failure"}
		>
			<Stack gap={4}>
				<Text size="sm">{label ? t(label[0], label[1]) : code}</Text>
				<Code>{code}</Code>
				{detail ? (
					<Text size="xs" c="dimmed">
						{detail}
					</Text>
				) : null}
			</Stack>
		</Alert>
	);
}

function DevelopmentValidationCommandCard({ command }: { readonly command: DevelopmentValidationCommand }) {
	const { t } = useTranslation();

	return (
		<ValidationCommandCard
			command={command}
			labels={{
				incomplete: t("pages.development.validation.command.incomplete", "Did not complete"),
				truncated: t("pages.development.validation.command.truncated", "Output truncated"),
				exitCode: `${t("pages.development.validation.command.exitCode", "exit")} ${command.exitCode}`,
				capturedOutput: t("pages.development.validation.command.capturedOutput", "Captured output"),
				testDiscovered: t("pages.development.validation.tests.discovered", "Tests discovered"),
				testExecuted: t("pages.development.validation.tests.executed", "Tests executed"),
				testPassed: t("pages.development.validation.tests.passed", "Tests passed"),
				testFailed: t("pages.development.validation.tests.failed", "Tests failed"),
			}}
			testIds={{
				card: `development-validation-command-${command.commandId}`,
				output: `development-validation-command-output-${command.commandId}`,
				testCounts: "development-validation-test-counts",
				testCountValuePrefix: "development-validation-test",
			}}
			unparsedOutcome={command.testOutcome ? <ValidationTestParseFailure outcome={command.testOutcome} /> : null}
		/>
	);
}

/**
 * The reachability half of deterministic validation: the report body is an encrypted artifact blob, so the operator
 * only ever sees what this renders. Every terminal state is rendered explicitly — an unreadable or unfetchable report
 * must never look like an empty panel, because "nothing shown" reads as "nothing wrong".
 *
 * FAILURE and STALENESS are two axes, and the backend already reports both separately. Keeping them apart is the
 * whole contract of this view:
 *
 * - `report.passed` is the GATE'S VERDICT on the run it describes. A failed run is not missing data; it is the
 *   answer, and it is authoritative about its own subject forever.
 * - `artifact.isValid` is CURRENCY — whether the working tree has since moved away from that subject.
 *
 * Conflating them is what dropped every failed report: a failed gate invalidates the approval evidence (correctly — a failed
 * validation must not stay approvable), which flips `isValid` to false, and the panel then dropped the report and
 * told the operator "no deterministic validation has run for this task yet" while the timeline beside it read
 * `ValidationFinalized — Failed`. The most prominent statement on the screen was the false one.
 */
export function ValidationReportView({ artifact }: { readonly artifact: DevelopmentArtifact | null }) {
	const { t } = useTranslation();
	const reportQuery = useDevelopmentArtifactContent(artifact?.projectId, artifact?.taskId, artifact?.id);
	const content = reportQuery.data?.content;
	const report = useMemo(() => parseDevelopmentValidationReport(content), [content]);

	if (artifact === null) {
		return (
			<Text c="dimmed" data-testid="development-validation-no-report">
				{t("pages.development.validation.noReport", "No deterministic validation has run for this task yet.")}
			</Text>
		);
	}

	if (reportQuery.isPending) {
		return <Loader size="sm" aria-label={t("pages.development.validation.loading", "Loading the validation report")} />;
	}

	if (reportQuery.error) {
		return (
			<InlineErrorAlert
				message={t("pages.development.validation.loadError", "Could not load the validation report.")}
				data-testid="development-validation-load-error"
			/>
		);
	}

	if (report === null) {
		return (
			<InlineErrorAlert
				message={t(
					"pages.development.validation.unreadable",
					"The stored validation report could not be read, so its result cannot be trusted.",
				)}
				data-testid="development-validation-unreadable"
			/>
		);
	}

	// A report that PASSED but is no longer current is the only case where showing the body would assert something
	// untrue: its green counts would read as the state of a tree they were never measured against. A report that
	// FAILED is shown whatever its currency — a failure is a fact about the run, and suppressing it is what left the
	// operator with no account of the fault at all.
	const superseded = artifact.isValid === false;
	if (superseded && report.passed) {
		return (
			<Alert
				color="yellow"
				icon={<IconAlertTriangle size={16} />}
				title={t("pages.development.validation.supersededTitle", "This validation result is no longer current")}
				data-testid="development-validation-superseded"
			>
				<Text size="sm">
					{t(
						"pages.development.validation.supersededBody",
						"A validation run passed against an earlier state of this task, but the working tree has moved since. Its counts are not the current result — run deterministic validation again.",
					)}
				</Text>
			</Alert>
		);
	}

	return (
		<Stack gap="sm" data-testid="development-validation-report">
			<Group justify="space-between" wrap="nowrap" align="flex-start">
				<Group gap="xs">
					<Badge color={report.passed ? "green" : "red"} data-testid="development-validation-result">
						{report.passed
							? t("pages.development.validation.passed", "Validation passed")
							: t("pages.development.validation.failed", "Validation failed")}
					</Badge>
					<Badge variant="outline" color={superseded ? "red" : "green"}>
						{superseded
							? t("pages.development.validation.invalidated", "Invalidated")
							: t("pages.development.validation.current", "Current")}
					</Badge>
				</Group>
				<Text size="xs" c="dimmed">
					{report.commandProfileId} · {report.commandProfileVersion} · {t("pages.development.validation.base", "base")}{" "}
					{shortHash(report.baseCommit)}
				</Text>
			</Group>

			{superseded ? (
				<Text size="xs" c="dimmed" data-testid="development-validation-failed-invalidated-note">
					{t(
						"pages.development.validation.failedInvalidated",
						"The failed gate invalidated this task's approval evidence, which is why the report is marked invalidated. The failure below is still what happened.",
					)}
				</Text>
			) : null}

			{report.failureCode ? <ValidationFailureAlert code={report.failureCode} detail={report.failureDetail} /> : null}

			<Stack gap="xs">
				{report.commands.map((command) => (
					<DevelopmentValidationCommandCard key={command.commandId} command={command} />
				))}
			</Stack>
		</Stack>
	);
}
