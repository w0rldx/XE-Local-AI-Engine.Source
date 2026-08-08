import { Alert, Badge, Code, Divider, Group, Loader, Paper, ScrollArea, SimpleGrid, Stack, Tabs, Text } from "@mantine/core";
import {
	IconAlertTriangle,
	IconCode,
	IconFile,
	IconInfoCircle,
	IconListCheck,
	IconTerminal2,
	IconTool,
} from "@tabler/icons-react";
import { useMemo } from "react";
import { useTranslation } from "react-i18next";

import type { DevelopmentArtifact, DevelopmentAttempt, DevelopmentEvent } from "@/features/development/models/DevelopmentModels";
import type { DevelopmentAttemptLiveState } from "@/features/development/hooks/useDevelopmentAttemptHub";
import {
	type DevelopmentTestOutcome,
	type DevelopmentValidationCommand,
	isDevelopmentNoTestsCode,
	parseDevelopmentValidationReport,
} from "@/features/development/models/DevelopmentValidationReport";
import { useDevelopmentArtifactContent } from "@/features/development/queries/useDevelopment";

interface DevelopmentLivePanelProps {
	readonly attempt: DevelopmentAttempt | null;
	readonly live: DevelopmentAttemptLiveState;
	readonly artifacts: readonly DevelopmentArtifact[];
	readonly events: readonly DevelopmentEvent[];
}

function Metric({
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
	return (
		<Paper withBorder={true} p="sm">
			<Text size="xs" c="dimmed">
				{label}
			</Text>
			<Text fw={600} data-testid={valueTestId}>
				{value}
			</Text>
		</Paper>
	);
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
	summary_incomplete: [
		"pages.development.validation.parseFailure.summaryIncomplete",
		"The test summary was incomplete.",
	],
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
 * Conflating them is what produced F-056: a failed gate invalidates the approval evidence (correctly — a failed
 * validation must not stay approvable), which flips `isValid` to false, and the panel then dropped the report and
 * told the operator "no deterministic validation has run for this task yet" while the timeline beside it read
 * `ValidationFinalized — Failed`. The most prominent statement on the screen was the false one.
 */
function ValidationReportView({ artifact }: { readonly artifact: DevelopmentArtifact | null }) {
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

export function DevelopmentLivePanel({ attempt, live, artifacts, events }: DevelopmentLivePanelProps) {
	const { t } = useTranslation();
	const latest = live.latest;
	const warnings = live.updates.filter((update) => update.kind === "Warning");
	const tools = live.updates.filter((update) => update.kind === "Tool");
	const commands = live.updates.filter((update) => update.kind === "Command");
	const output = live.updates.filter((update) => update.kind === "Output" || update.kind === "Activity");
	const validationArtifacts = artifacts.filter(
		(artifact) => artifact.kind === "ValidationReport" || artifact.kind === "ReviewReport",
	);
	// The newest validation report, WHATEVER its validity. Selecting on `isValid` was the F-056 defect: a failed gate
	// invalidates the approval evidence, so every failed report was dropped here and the panel fell through to "no
	// deterministic validation has run for this task yet" — the report that existed, was fetchable, and named the
	// fault. Currency is a presentation decision made in ValidationReportView from the report's own verdict; it is
	// not a reason to refuse to look at the report.
	const latestValidationReport = useMemo(() => {
		let latest: DevelopmentArtifact | null = null;
		for (const artifact of artifacts) {
			if (artifact.kind !== "ValidationReport") {
				continue;
			}
			if (latest === null || (artifact.createdAtUtc ?? 0) >= (latest.createdAtUtc ?? 0)) {
				latest = artifact;
			}
		}

		return latest;
	}, [artifacts]);

	return (
		<Stack gap="md" data-testid="development-live-panel">
			<Group justify="space-between">
				<Group gap="xs">
					<Badge variant="light">{attempt?.role ?? t("pages.development.live.noAttempt", "No active attempt")}</Badge>
					<Badge color={attempt?.status === "Running" ? "green" : "gray"}>
						{attempt?.status ?? t("pages.development.live.idle", "Idle")}
					</Badge>
					<Badge variant="outline">{live.connectionState}</Badge>
				</Group>
				<Text size="xs" c="dimmed">
					{t("pages.development.live.watermark", "Watermark")}: {live.watermark} ·{" "}
					{t("pages.development.live.dropped", "coalesced")}: {live.droppedOrCoalescedUpdateCount}
				</Text>
			</Group>

			<SimpleGrid cols={{ base: 2, sm: 3, lg: 6 }}>
				<Metric label={t("pages.development.live.model", "Model")} value={latest?.modelId ?? attempt?.modelId ?? "—"} />
				<Metric label={t("pages.development.live.provider", "Provider")} value={latest?.provider ?? attempt?.provider ?? "—"} />
				<Metric
					label={t("pages.development.live.speed", "Output tokens/s")}
					value={latest?.outputTokensPerSecond?.toFixed(1) ?? "—"}
				/>
				<Metric label={t("pages.development.live.rounds", "Provider rounds")} value={latest?.providerRoundCount ?? 0} />
				<Metric label={t("pages.development.live.tools", "Tool calls")} value={latest?.toolCallCount ?? 0} />
				<Metric label={t("pages.development.live.commands", "Commands")} value={latest?.commandCount ?? 0} />
				<Metric label={t("pages.development.live.files", "Files changed")} value={latest?.changedFileCount ?? 0} />
				<Metric
					label={t("pages.development.live.context", "Context headroom")}
					value={latest?.contextHeadroomPercent == null ? "—" : `${latest.contextHeadroomPercent.toFixed(0)}%`}
				/>
				<Metric
					label={t("pages.development.live.noProgress", "No-progress age")}
					value={`${latest?.secondsSinceMeaningfulProgress ?? 0}s`}
				/>
				<Metric
					label={t("pages.development.live.inputTokens", "Input tokens")}
					value={latest?.inputTokens ?? attempt?.inputTokens ?? 0}
				/>
				<Metric
					label={t("pages.development.live.outputTokens", "Output tokens")}
					value={latest?.outputTokens ?? attempt?.outputTokens ?? 0}
				/>
				<Metric label={t("pages.development.live.reasoningTokens", "Reasoning tokens")} value={latest?.reasoningTokens ?? 0} />
			</SimpleGrid>

			{warnings.map((warning) => (
				<Alert
					key={warning.sequence}
					icon={<IconAlertTriangle size={16} />}
					color="yellow"
					title={warning.warningCategory ?? t("pages.development.live.warning", "Warning")}
				>
					{warning.warningMessage}
				</Alert>
			))}

			<Tabs defaultValue="live">
				<Tabs.List>
					<Tabs.Tab value="live" leftSection={<IconCode size={14} />}>
						{t("pages.development.live.tabs.live", "Live")}
					</Tabs.Tab>
					<Tabs.Tab value="tools" leftSection={<IconTool size={14} />}>
						{t("pages.development.live.tabs.tools", "Tools")}
					</Tabs.Tab>
					<Tabs.Tab value="commands" leftSection={<IconTerminal2 size={14} />}>
						{t("pages.development.live.tabs.commands", "Commands")}
					</Tabs.Tab>
					<Tabs.Tab value="files" leftSection={<IconFile size={14} />}>
						{t("pages.development.live.tabs.files", "Files")}
					</Tabs.Tab>
					<Tabs.Tab value="validation" leftSection={<IconListCheck size={14} />}>
						{t("pages.development.live.tabs.validation", "Validation")}
					</Tabs.Tab>
					<Tabs.Tab value="details" leftSection={<IconInfoCircle size={14} />}>
						{t("pages.development.live.tabs.details", "Details")}
					</Tabs.Tab>
				</Tabs.List>

				<Tabs.Panel value="live" pt="md">
					<ScrollArea h={260}>
						<Stack gap="xs">
							{output.length === 0 ? <Text c="dimmed">{t("pages.development.live.noOutput", "No live output yet.")}</Text> : null}
							{output.map((update) => (
								<Paper key={update.sequence} withBorder={true} p="xs">
									<Text size="xs" c="dimmed">
										#{update.sequence} · {update.kind}
									</Text>
									<Text>{update.outputDelta ?? update.currentActivity ?? t("pages.development.live.activityUpdated", "Activity updated")}</Text>
								</Paper>
							))}
						</Stack>
					</ScrollArea>
				</Tabs.Panel>
				<Tabs.Panel value="tools" pt="md">
					<Stack gap="xs">
						{tools.length === 0 ? <Text c="dimmed">{t("pages.development.live.noTools", "No tool activity yet.")}</Text> : null}
						{tools.map((update) => (
							<Code key={update.sequence}>
								{update.currentToolId ?? "tool"} · {update.currentActivity ?? update.status}
							</Code>
						))}
					</Stack>
				</Tabs.Panel>
				<Tabs.Panel value="commands" pt="md">
					<Stack gap="xs">
						{commands.length === 0 ? (
							<Text c="dimmed">{t("pages.development.live.noCommands", "No command activity yet.")}</Text>
						) : null}
						{commands.map((update) => (
							<Code key={update.sequence}>
								{update.currentCommandId ?? "command"} · {update.currentActivity ?? update.status}
							</Code>
						))}
					</Stack>
				</Tabs.Panel>
				<Tabs.Panel value="files" pt="md">
					<Stack gap="xs">
						<Text>
							{t("pages.development.live.filesSummary", "{{files}} file(s) changed; patch size {{bytes}} bytes.", {
								files: latest?.changedFileCount ?? 0,
								bytes: latest?.patchByteCount ?? 0,
							})}
						</Text>
						{artifacts
							.filter((artifact) => artifact.kind === "ChangedFilesManifest" || artifact.kind === "Patch")
							.map((artifact) => (
								<Code key={artifact.id}>
									{artifact.kind} · {artifact.contentHash?.slice(0, 12)} · {artifact.byteCount ?? 0} bytes
								</Code>
							))}
					</Stack>
				</Tabs.Panel>
				<Tabs.Panel value="validation" pt="md">
					<Stack gap="md" data-testid="development-validation-panel">
						<ValidationReportView artifact={latestValidationReport} />
						<Divider />
						<Stack gap="xs">
							<Text size="sm" fw={600}>
								{t("pages.development.validation.evidence", "Stored evidence")}
							</Text>
							{validationArtifacts.length === 0 ? (
								<Text c="dimmed">
									{t("pages.development.validation.noEvidence", "No validation or review evidence yet.")}
								</Text>
							) : null}
							{validationArtifacts.map((artifact) => (
								<Group key={artifact.id} justify="space-between">
									<Text>{artifact.kind}</Text>
									<Badge color={artifact.isValid ? "green" : "red"}>
										{artifact.isValid
											? t("pages.development.validation.current", "Current")
											: t("pages.development.validation.invalidated", "Invalidated")}
									</Badge>
								</Group>
							))}
						</Stack>
					</Stack>
				</Tabs.Panel>
				<Tabs.Panel value="details" pt="md">
					<Stack gap="xs">
						<Text size="sm">
							{t("pages.development.live.details.attempt", "Attempt")}: {attempt?.id ?? "—"}
						</Text>
						<Text size="sm">
							{t("pages.development.live.details.predecessor", "Predecessor")}: {attempt?.predecessorAttemptId ?? "—"}
						</Text>
						<Text size="sm">
							{t("pages.development.live.details.activity", "Current activity")}: {latest?.currentActivity ?? "—"}
						</Text>
						<Text size="sm">
							{t("pages.development.live.details.subject", "Subject")}: {latest?.subjectHash?.slice(0, 16) ?? "—"}
						</Text>
						<Text size="sm">
							{t("pages.development.live.details.events", "Durable events")}: {events.length}
						</Text>
					</Stack>
				</Tabs.Panel>
			</Tabs>
		</Stack>
	);
}
