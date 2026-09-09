import { Alert, Badge, Code, Group, Paper, ScrollArea, SimpleGrid, Stack, Text } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { StatTile } from "@/core/ui/components/StatTile/StatTile";
import type { DevWorkflowNodeRunDetailResponse } from "@/features/devWorkflows/models/DevWorkflowModels";
import {
	type DevWorkflowTestOutcome,
	type DevWorkflowValidationCommand,
	devWorkflowMissingEvidenceCode,
	type parseDevWorkflowValidationReport,
} from "@/features/devWorkflows/models/DevWorkflowValidationReport";

/**
 * The validation half of a Tool node's report, rendered from `<nodeKey>-validation.json`. Sibling of
 * `DevWorkflowApplyReportPanel`, which renders the other half.
 *
 * Its rule is that panel's rule: never let an absence read as a pass. A refusal shows the report's own sentence
 * rather than "0 commands, 0 tests", and evidence that stops short is labelled partial rather than red.
 */
export function DevWorkflowValidationReportPanel({
	report,
	nodeRun,
}: {
	readonly report: NonNullable<ReturnType<typeof parseDevWorkflowValidationReport>>;
	readonly nodeRun: DevWorkflowNodeRunDetailResponse;
}) {
	const { t } = useTranslation();
	const commands = report.commands ?? [];
	// A node run whose clock ran out reports the commands it never reached as missing evidence. That is a PARTIAL
	// record, and saying so is the difference between "slow" and "broken" for whoever reads it next.
	const partial = report.failureCode === devWorkflowMissingEvidenceCode && nodeRun.failureClass === "Timeout";

	return (
		<Stack gap="xs" data-testid="dev-workflow-validation-report">
			<Group gap="xs" wrap="wrap">
				<Badge color={report.passed ? "green" : "red"} data-testid="dev-workflow-validation-result">
					{report.passed
						? t("pages.devWorkflows.validation.passed", "Validation passed")
						: t("pages.devWorkflows.validation.failed", "Validation failed")}
				</Badge>
				<Text size="xs" c="dimmed">
					{t("pages.devWorkflows.validation.base", "base {{commit}} · profile {{profile}}", {
						commit: (report.baseCommit ?? "").slice(0, 12) || "—",
						profile: report.commandProfileId ?? "—",
					})}
				</Text>
			</Group>

			{/* Directly under the base commit, because it QUALIFIES it: these commands did not judge that commit as it
			    stands, they judged the child's staged work on top of it. `basedOn` exists only when a patch really was
			    overlaid — every other state refuses the node rather than reporting — so its absence is not evidence of a
			    base validation (an older report has no such field either) and nothing is claimed on that path. */}
			{report.basedOn ? (
				<Stack gap={2} data-testid="dev-workflow-validation-based-on">
					<Text size="xs">
						{t(
							"pages.devWorkflows.validation.basedOn",
							"Judged the implementation task's approved patch {{hash}} · task {{task}}",
							{
								hash: (report.basedOn.patchHash ?? "").slice(0, 12) || "—",
								task: report.basedOn.developmentTaskId ?? "—",
							},
						)}
					</Text>
					{/* Server prose, verbatim (§2.11): it is the sentence that says what was applied to what. */}
					{report.basedOn.detail ? (
						<Text size="xs" c="dimmed" data-testid="dev-workflow-validation-based-on-detail">
							{report.basedOn.detail}
						</Text>
					) : null}
				</Stack>
			) : null}

			{partial ? (
				<Alert
					color="orange"
					variant="light"
					icon={<IconAlertTriangle size={16} />}
					data-testid="dev-workflow-validation-partial"
				>
					<Stack gap={4}>
						<Text size="sm">
							{t(
								"pages.devWorkflows.validation.partial",
								"This report is partial: the node ran out of time before every declared command had run.",
							)}
						</Text>
						{/* The row's sentence, not the report's, because it is the one that names the budget in seconds. */}
						{nodeRun.terminalReason ? (
							<Text size="xs" c="dimmed" data-testid="dev-workflow-validation-partial-reason">
								{nodeRun.terminalReason}
							</Text>
						) : null}
					</Stack>
				</Alert>
			) : null}

			{/* Server prose, displayed verbatim (§2.11): the verdict's detail already names the command and the reason,
			    and the raw code is shown beside it so an unrecognised one is never silently dropped. */}
			{report.failureCode && !partial ? (
				<Alert color="red" variant="light" icon={<IconAlertTriangle size={16} />} data-testid="dev-workflow-validation-failure">
					<Stack gap={4}>
						{report.failureDetail ? <Text size="sm">{report.failureDetail}</Text> : null}
						<Code data-testid="dev-workflow-validation-failure-code">{report.failureCode}</Code>
					</Stack>
				</Alert>
			) : null}

			{commands.length === 0 ? (
				// Never a count. "0 commands · 0 tests" over a refusal is the exact false green this panel exists to
				// prevent: it reads as a clean run, and the report's own sentence says it was nothing of the kind.
				<Text size="sm" c="dimmed" data-testid="dev-workflow-validation-no-commands">
					{t(
						"pages.devWorkflows.validation.noCommands",
						"No validation command ran, so this report evidences nothing about the code.",
					)}
				</Text>
			) : (
				<Stack gap="xs">
					{commands.map((command) => (
						<ValidationCommandCard key={command.commandId} command={command} />
					))}
				</Stack>
			)}
		</Stack>
	);
}

function ValidationCommandCard({ command }: { readonly command: DevWorkflowValidationCommand }) {
	const { t } = useTranslation();
	const failed = !command.completed || command.exitCode !== 0;
	// Only a failing command's captured output is rendered: on a pass it is noise, on a failure it is the only record
	// of why. It is sanitized server-side, and when the whole report would not fit it is the server's own sentence
	// saying the text was left out — which is why it is printed verbatim rather than pattern-matched.
	const capturedOutput = failed ? [command.standardError, command.standardOutput].filter((output) => !!output?.trim()) : [];

	return (
		<Paper withBorder={true} p="xs" data-testid={`dev-workflow-validation-command-${command.commandId}`}>
			<Group justify="space-between" wrap="nowrap" align="flex-start">
				<Code>{command.commandId}</Code>
				<Group gap={4} wrap="wrap">
					{command.completed ? null : (
						<Badge size="xs" color="red">
							{t("pages.devWorkflows.validation.command.incomplete", "Did not complete")}
						</Badge>
					)}
					{command.outputTruncated ? (
						<Badge size="xs" color="yellow" variant="light">
							{t("pages.devWorkflows.validation.command.truncated", "Output truncated")}
						</Badge>
					) : null}
					<Badge size="xs" color={failed ? "red" : "green"} variant="light">
						{t("pages.devWorkflows.validation.command.exitCode", "exit {{code}}", { code: command.exitCode })}
					</Badge>
					<Text size="xs" c="dimmed">
						{((command.durationMilliseconds ?? 0) / 1000).toFixed(1)}s
					</Text>
				</Group>
			</Group>
			{command.testOutcome ? <TestOutcomeView outcome={command.testOutcome} /> : null}
			{capturedOutput.length > 0 ? (
				<ScrollArea.Autosize mah={200} mt="xs">
					<Code block={true} data-testid={`dev-workflow-validation-output-${command.commandId}`}>
						{capturedOutput.join("\n")}
					</Code>
				</ScrollArea.Autosize>
			) : null}
		</Paper>
	);
}

/** A parse failure is a validation failure, never missing data — so it renders instead of the counts, never beside them. */
function TestOutcomeView({ outcome }: { readonly outcome: DevWorkflowTestOutcome }) {
	const { t } = useTranslation();

	if (!outcome.parsed) {
		return (
			<Alert
				mt="xs"
				color="red"
				variant="light"
				icon={<IconAlertTriangle size={16} />}
				data-testid="dev-workflow-validation-tests-unparsed"
			>
				<Stack gap={4}>
					<Text size="sm">
						{t(
							"pages.devWorkflows.validation.tests.unparsed",
							"The test results could not be read, so no executed, passed or failed count is available for this run.",
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
		<SimpleGrid cols={{ base: 2, sm: 4 }} mt="xs" data-testid="dev-workflow-validation-tests">
			{/* The test ids sit on the VALUES: a test that could only find the tile would pass against four zeroes. */}
			<StatTile
				variant="paper"
				label={t("pages.devWorkflows.validation.tests.discovered", "Discovered")}
				value={outcome.discovered}
				valueTestId="dev-workflow-validation-tests-discovered"
			/>
			<StatTile
				variant="paper"
				label={t("pages.devWorkflows.validation.tests.executed", "Executed")}
				value={outcome.executed}
				valueTestId="dev-workflow-validation-tests-executed"
			/>
			<StatTile
				variant="paper"
				label={t("pages.devWorkflows.validation.tests.passed", "Passed")}
				value={outcome.passed}
				valueTestId="dev-workflow-validation-tests-passed"
			/>
			<StatTile
				variant="paper"
				label={t("pages.devWorkflows.validation.tests.failed", "Failed")}
				value={outcome.failed}
				valueTestId="dev-workflow-validation-tests-failed"
			/>
		</SimpleGrid>
	);
}
