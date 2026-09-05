import { Alert, Badge, Button, Code, Group, Loader, Paper, ScrollArea, SimpleGrid, Stack, Text } from "@mantine/core";
import { IconAlertTriangle, IconEye, IconEyeOff } from "@tabler/icons-react";
import { useMemo } from "react";
import { useTranslation } from "react-i18next";

import { CodeEditor } from "@/core/ui/components/CodeEditor/CodeEditor";
import { StatTile } from "@/core/ui/components/StatTile/StatTile";
import type { DevelopmentArtifact } from "@/features/development/models/DevelopmentModels";
import {
	type DevelopmentTestOutcome,
	type DevelopmentValidationCommand,
	isDevelopmentNoTestsCode,
	parseDevelopmentValidationReport,
} from "@/features/development/models/DevelopmentValidationReport";
import { useDevelopmentArtifactContent } from "@/features/development/queries/useDevelopment";

export function Metric({
	label,
	value,
	valueTestId,
}: {
	readonly label: string;
	readonly value: string | number;
	/**
	 * Put on the VALUE, not the tile. The validation counts are an acceptance criterion, and a test that can only
	 * locate the tile can assert that a number rendered but not which one — so it would still pass against four
	 * zeroes, which is the exact false green this panel exists to expose.
	 */
	readonly valueTestId?: string;
}) {
	return <StatTile variant="paper" label={label} value={value} valueTestId={valueTestId} />;
}

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

function ValidationTestOutcomeView({ outcome }: { readonly outcome: DevelopmentTestOutcome }) {
	const { t } = useTranslation();

	if (!outcome.parsed) {
		// "No test project" is the registered-repository policy case, not a broken adapter: it reads as a reduced
		// guarantee, whereas every other parse failure reads as a failed run.
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

	return (
		<SimpleGrid cols={{ base: 2, sm: 4 }} mt="sm" data-testid="development-validation-test-counts">
			<Metric
				label={t("pages.development.validation.tests.discovered", "Tests discovered")}
				value={outcome.discovered}
				valueTestId="development-validation-test-discovered"
			/>
			<Metric
				label={t("pages.development.validation.tests.executed", "Tests executed")}
				value={outcome.executed}
				valueTestId="development-validation-test-executed"
			/>
			<Metric
				label={t("pages.development.validation.tests.passed", "Tests passed")}
				value={outcome.passed}
				valueTestId="development-validation-test-passed"
			/>
			<Metric
				label={t("pages.development.validation.tests.failed", "Tests failed")}
				value={outcome.failed}
				valueTestId="development-validation-test-failed"
			/>
		</SimpleGrid>
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

function ValidationCommandCard({ command }: { readonly command: DevelopmentValidationCommand }) {
	const { t } = useTranslation();
	const failed = !command.completed || command.exitCode !== 0;
	// Only a failing command's captured output is rendered. On a pass it is noise; on a failure it is the ONLY record
	// of why — the live evaluation had to read the artifact blob out of the API by hand to find `errno == EROFS`,
	// because the panel showed an exit code and nothing else. It is already sanitized server-side.
	const capturedOutput = failed ? [command.standardError, command.standardOutput].filter((text) => !!text?.trim()) : [];

	return (
		<Paper withBorder={true} p="sm" data-testid={`development-validation-command-${command.commandId}`}>
			<Group justify="space-between" wrap="nowrap" align="flex-start">
				<Code>{command.commandId}</Code>
				<Group gap="xs">
					{command.completed ? null : (
						<Badge color="red">{t("pages.development.validation.command.incomplete", "Did not complete")}</Badge>
					)}
					{command.outputTruncated ? (
						<Badge color="yellow" variant="light">
							{t("pages.development.validation.command.truncated", "Output truncated")}
						</Badge>
					) : null}
					<Badge color={failed ? "red" : "green"} variant="light">
						{t("pages.development.validation.command.exitCode", "exit")} {command.exitCode}
					</Badge>
					<Text size="xs" c="dimmed">
						{(command.durationMilliseconds / 1000).toFixed(1)}s
					</Text>
				</Group>
			</Group>
			{command.testOutcome ? <ValidationTestOutcomeView outcome={command.testOutcome} /> : null}
			{capturedOutput.length > 0 ? (
				<Stack gap={4} mt="sm">
					<Text size="xs" c="dimmed">
						{t("pages.development.validation.command.capturedOutput", "Captured output")}
					</Text>
					<ScrollArea.Autosize mah={220}>
						<Code block={true} data-testid={`development-validation-command-output-${command.commandId}`}>
							{capturedOutput.join("\n")}
						</Code>
					</ScrollArea.Autosize>
				</Stack>
			) : null}
		</Paper>
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
			<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="development-validation-load-error">
				{t("pages.development.validation.loadError", "Could not load the validation report.")}
			</Alert>
		);
	}

	if (report === null) {
		return (
			<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="development-validation-unreadable">
				{t(
					"pages.development.validation.unreadable",
					"The stored validation report could not be read, so its result cannot be trusted.",
				)}
			</Alert>
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
					<ValidationCommandCard key={command.commandId} command={command} />
				))}
			</Stack>
		</Stack>
	);
}

/** Every artifact kind other than the git patch is a JSON document (manifest, validation/review reports). */
function artifactLanguage(kind: string | undefined): string {
	if (kind === "Patch") {
		return "diff";
	}

	// A prompt is plain text, not a document. Falling through to the JSON arm rendered it unhighlighted and, worse,
	// as something an operator would read as malformed JSON.
	if (kind === "Prompt") {
		return "plaintext";
	}

	return "json";
}

/**
 * The raw body of one stored artifact in the shared code viewer — the patch as a unified diff, everything else as
 * JSON. This is the inspection path for what the engine produced: the same decrypted, blob-verified read the
 * validation view uses, shown verbatim instead of interpreted.
 */
export function ArtifactContentView({ artifact }: { readonly artifact: DevelopmentArtifact }) {
	const { t } = useTranslation();
	const contentQuery = useDevelopmentArtifactContent(artifact.projectId, artifact.taskId, artifact.id);

	if (contentQuery.isPending) {
		return <Loader size="sm" aria-label={t("pages.development.artifacts.loading", "Loading the artifact")} />;
	}
	if (contentQuery.error) {
		return (
			<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="development-artifact-load-error">
				{t("pages.development.artifacts.loadError", "Could not load the artifact.")}
			</Alert>
		);
	}
	return (
		<CodeEditor
			value={contentQuery.data?.content ?? ""}
			language={artifactLanguage(artifact.kind)}
			readOnly={true}
			height={360}
			aria-label={t("pages.development.artifacts.viewerLabel", "{{kind}} artifact", { kind: artifact.kind })}
			data-testid={`development-artifact-content-${artifact.id}`}
		/>
	);
}

/** "View" toggle for one artifact row; the selection lives in the panel so only one viewer is open at a time. */
export function ArtifactViewButton({
	artifact,
	open,
	onToggle,
}: {
	readonly artifact: DevelopmentArtifact;
	readonly open: boolean;
	readonly onToggle: (artifact: DevelopmentArtifact) => void;
}) {
	const { t } = useTranslation();
	return (
		<Button
			size="compact-xs"
			variant="subtle"
			leftSection={open ? <IconEyeOff size={14} /> : <IconEye size={14} />}
			onClick={() => onToggle(artifact)}
			data-testid={`development-artifact-view-${artifact.id}`}
		>
			{open ? t("pages.development.artifacts.hide", "Hide") : t("pages.development.artifacts.view", "View")}
		</Button>
	);
}
