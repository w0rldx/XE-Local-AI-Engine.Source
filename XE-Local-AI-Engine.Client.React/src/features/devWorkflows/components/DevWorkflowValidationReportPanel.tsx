import { Alert, Badge, Code, Group, Stack, Text } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { ValidationCommandCard } from "@/core/ui/components/ValidationCommandCard/ValidationCommandCard";
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
				<InlineErrorAlert variant="light" message={report.failureDetail} data-testid="dev-workflow-validation-failure">
					<Code data-testid="dev-workflow-validation-failure-code">{report.failureCode}</Code>
				</InlineErrorAlert>
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
						<DevWorkflowValidationCommandCard key={command.commandId} command={command} />
					))}
				</Stack>
			)}
		</Stack>
	);
}

function DevWorkflowValidationCommandCard({ command }: { readonly command: DevWorkflowValidationCommand }) {
	const { t } = useTranslation();

	return (
		<ValidationCommandCard
			command={command}
			labels={{
				incomplete: t("pages.devWorkflows.validation.command.incomplete", "Did not complete"),
				truncated: t("pages.devWorkflows.validation.command.truncated", "Output truncated"),
				exitCode: t("pages.devWorkflows.validation.command.exitCode", "exit {{code}}", { code: command.exitCode }),
				testDiscovered: t("pages.devWorkflows.validation.tests.discovered", "Discovered"),
				testExecuted: t("pages.devWorkflows.validation.tests.executed", "Executed"),
				testPassed: t("pages.devWorkflows.validation.tests.passed", "Passed"),
				testFailed: t("pages.devWorkflows.validation.tests.failed", "Failed"),
			}}
			testIds={{
				card: `dev-workflow-validation-command-${command.commandId}`,
				output: `dev-workflow-validation-output-${command.commandId}`,
				testCounts: "dev-workflow-validation-tests",
				testCountValuePrefix: "dev-workflow-validation-tests",
			}}
			unparsedOutcome={command.testOutcome ? <ValidationTestParseFailure outcome={command.testOutcome} /> : null}
		/>
	);
}

/** A parse failure is a validation failure, never missing data — so it renders instead of the counts, never beside them. */
function ValidationTestParseFailure({ outcome }: { readonly outcome: DevWorkflowTestOutcome }) {
	const { t } = useTranslation();

	return (
		<InlineErrorAlert
			mt="sm"
			variant="light"
			message={t(
				"pages.devWorkflows.validation.tests.unparsed",
				"The test results could not be read, so no executed, passed or failed count is available for this run.",
			)}
			data-testid="dev-workflow-validation-tests-unparsed"
		>
			<Code>{outcome.parseFailureCode ?? "unknown"}</Code>
			{/* Server prose, verbatim (§2.11). */}
			{outcome.parseFailureDetail ? (
				<Text size="xs" c="dimmed">
					{outcome.parseFailureDetail}
				</Text>
			) : null}
		</InlineErrorAlert>
	);
}
