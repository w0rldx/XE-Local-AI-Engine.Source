import { Alert, Badge, Button, Code, Group, Loader, Paper, ScrollArea, SimpleGrid, Stack, Text } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import { useMemo } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { StatTile } from "@/core/ui/components/StatTile/StatTile";
import { DevWorkflowApplyReportPanel } from "@/features/devWorkflows/components/DevWorkflowApplyReportPanel";
import { parseDevWorkflowApplyReport } from "@/features/devWorkflows/models/DevWorkflowApplyReport";
import {
	type DevWorkflowNodeRunDetailResponse,
	decodeDevWorkflowArtifactContent,
} from "@/features/devWorkflows/models/DevWorkflowModels";
import {
	type DevWorkflowValidationCommand,
	type DevWorkflowTestOutcome,
	devWorkflowMissingEvidenceCode,
	parseDevWorkflowValidationReport,
} from "@/features/devWorkflows/models/DevWorkflowValidationReport";
import { useDevWorkflowArtifactContent } from "@/features/devWorkflows/queries/useDevWorkflows";

export interface DevWorkflowToolNodePanelProps {
	readonly nodeRun: DevWorkflowNodeRunDetailResponse;
	/** Brings the operator to the artifact list, where the report's raw document and its versions live. */
	readonly onShowArtifacts: () => void;
}

/**
 * A Tool node's report, rendered from the artifact the executor wrote before it moved the row (so the evidence exists
 * whatever the node run then became).
 *
 * A Tool node is one of two things (R-C3): a validation node, whose `<nodeKey>-validation.json` says what ran against
 * a clean checkout, or an apply node, whose `<nodeKey>-apply.json` says which patches the hash-locked gate landed.
 * Both are artifact kind `Report` and the discriminator is not on the wire, so the BODY decides — the two documents'
 * evidence arrays (`commands` and `tasks`) are what tell them apart.
 *
 * Everything below is the validation half. Its rule is the apply panel's rule too:
 *
 * Everything here follows one rule: never let an absence read as a pass. The two ways this panel could lie are a
 * refusal shown as "0 commands, 0 tests" and a partial report shown as a complete one, so each has its own state:
 *
 * - **No report at all.** A pass refused before a single command ran — a dependency manifest the sandbox has no
 *   network to fetch, an unacknowledged repository, a backend that cannot hold a trusted workspace — writes no
 *   artifact and puts its sanitized sentence on the row. That sentence is the render.
 * - **A report with no command evidence.** Same story from the other side, and the report's own `failureDetail` says
 *   which policy refused it.
 * - **A report whose evidence stops short** (`missing_command_evidence` on a node run whose clock ran out). It is the
 *   commands that DID run, labelled as partial, with the row naming the timeout — not a red gate with no account.
 *
 * The raw document stays one click away in the artifacts tab, and an unreadable body falls back to it rather than
 * rendering an empty panel.
 */
export function DevWorkflowToolNodePanel({ nodeRun, onShowArtifacts }: DevWorkflowToolNodePanelProps) {
	const { t } = useTranslation();
	// The Tool node's only artifact is its report, so the node's headline output is it. `primaryArtifactId` is the
	// newest version, which is the attempt an operator is looking at.
	const artifactId = nodeRun.primaryArtifactId ?? undefined;
	const contentQuery = useDevWorkflowArtifactContent(nodeRun.runId ?? undefined, artifactId);
	const raw = contentQuery.data;
	const text = useMemo(
		() => (raw ? decodeDevWorkflowArtifactContent(raw.content ?? "", raw.isBase64 === true).text : ""),
		[raw],
	);
	// A Tool node is either a validation node or an apply node (R-C3), and BOTH write their report under the ordinary
	// `Report` artifact kind — so the document itself is what says which one this is. Handing an apply report to the
	// validation reader produced "could not be read", which is a false alarm about evidence that is perfectly intact.
	const applyReport = useMemo(() => parseDevWorkflowApplyReport(text), [text]);
	const report = useMemo(() => (applyReport ? null : parseDevWorkflowValidationReport(text)), [applyReport, text]);
	const isApply = applyReport !== null;
	// With no artifact at all — or a body neither reader understands — NOTHING says which kind of Tool node this is: the
	// discriminator lives in the graph node's config and P3 does not project it. So every string on those paths is
	// neutral. Calling a refused APPLY node's silence "no validation report was written" names a document that node was
	// never going to write and sends whoever reads it looking for the wrong evidence; the fix is to stop guessing, not
	// to guess better.
	// `primaryArtifactId` is the node's newest artifact, NOT this attempt's: a retry or an X9 fix-loop reset (which
	// puts Succeeded rows back to Pending) leaves attempt N's report standing until attempt N+1's commands land. Both
	// documents carry the attempt they were written for, so an older one is never painted as the current result — a
	// stale "Validation passed" over a node that is re-validating is the one lie this panel must not tell.
	// A decomposition that found no work writes one already-succeeded row per validation node in its template, so an
	// apply downstream can read a validation that really did run for this run. That row wrote no report because it ran
	// nothing, and "no report was written, so there is nothing here that evidences what this node did" would be an
	// alarm about a row behaving exactly as designed.
	const notApplicable = useMemo(() => validationNotApplicable(nodeRun.outputJson), [nodeRun.outputJson]);
	const reportAttempt = applyReport?.attempt ?? report?.attempt;
	const priorAttempt =
		typeof reportAttempt === "number" && reportAttempt < (nodeRun.attempt ?? 1) ? reportAttempt : undefined;

	return (
		<SectionCard
			title={
				isApply
					? t("pages.devWorkflows.node.apply", "Patch apply")
					: report !== null
						? t("pages.devWorkflows.node.tool", "Validation")
						: t("pages.devWorkflows.node.report", "Report")
			}
			gap="xs"
			data-testid="dev-workflow-node-tool"
		>
			{artifactId ? null : notApplicable ? (
				<Text size="sm" c="dimmed" data-testid="dev-workflow-validation-not-applicable">
					{t(
						"pages.devWorkflows.validation.notApplicable",
						"Nothing was decomposed for this run, so this check had nothing to validate.",
					)}
				</Text>
			) : (
				<RefusedWithoutReport nodeRun={nodeRun} />
			)}

			{artifactId && contentQuery.isPending ? <Loader size="sm" data-testid="dev-workflow-validation-loading" /> : null}

			{artifactId && contentQuery.isError ? (
				<Alert color="red" variant="light" icon={<IconAlertTriangle size={16} />} data-testid="dev-workflow-validation-error">
					{apiErrorMessage(
						contentQuery.error,
						t("pages.devWorkflows.validation.loadFailed", "Could not load this node's report."),
					)}
				</Alert>
			) : null}

			{/* An unreadable body is not an empty panel: the document is still in the artifacts tab, verbatim. */}
			{artifactId && contentQuery.isSuccess && report === null && applyReport === null ? (
				<Alert color="red" variant="light" icon={<IconAlertTriangle size={16} />} data-testid="dev-workflow-validation-unreadable">
					{t(
						"pages.devWorkflows.validation.unreadable",
						"This node's report could not be read, so its result cannot be trusted. Open it in the artifacts tab to see what was stored.",
					)}
				</Alert>
			) : null}

			{priorAttempt === undefined ? null : (
				<Alert color="yellow" variant="light" icon={<IconAlertTriangle size={16} />} data-testid="dev-workflow-validation-stale-attempt">
					{t(
						"pages.devWorkflows.validation.priorAttempt",
						"The stored report is attempt {{reportAttempt}}'s, and this node is on attempt {{attempt}}. It is not the current result — open it from the artifacts tab if you want the earlier evidence.",
						{ reportAttempt: priorAttempt, attempt: nodeRun.attempt ?? 1 },
					)}
				</Alert>
			)}

			{report && priorAttempt === undefined ? <ValidationReport report={report} nodeRun={nodeRun} /> : null}
			{applyReport && priorAttempt === undefined ? <DevWorkflowApplyReportPanel report={applyReport} /> : null}

			{artifactId ? (
				<Button size="xs" variant="subtle" onClick={onShowArtifacts} data-testid="dev-workflow-node-tool-report">
					{isApply
						? t("pages.devWorkflows.node.openApplyReport", "Open the apply report")
						: report !== null
							? t("pages.devWorkflows.node.openReport", "Open the validation report")
							: t("pages.devWorkflows.node.openStoredReport", "Open the stored report")}
				</Button>
			) : null}
		</SectionCard>
	);
}

/**
 * Whether the row says it validated nothing because there was nothing to validate — the verdict a zero-task
 * decomposition writes onto its template's validation nodes. Read off the row's own output document, which is the only
 * place that fact lives on the DETAIL response; an unreadable body is simply not that verdict.
 *
 * The token is `DevWorkflowNodeOutputVerdicts.ValidationNotApplicable`, and the server answers the same question with
 * `DevWorkflowGraphContract.ValidationWasNotApplicable` — which is what the node-run SUMMARY carries as
 * `validationNotApplicable`, for the run table and the progress counts. Spelled out here because there is no generated
 * enum for a verdict inside an output document.
 */
function validationNotApplicable(outputJson: string | null | undefined): boolean {
	if (!outputJson) {
		return false;
	}

	try {
		const parsed: unknown = JSON.parse(outputJson);
		return (
			typeof parsed === "object" &&
			parsed !== null &&
			(parsed as { readonly verdict?: unknown }).verdict === "validation-not-applicable"
		);
	} catch {
		return false;
	}
}

/**
 * The node ended without a report of its own. The row's sanitized `terminalReason` is the only account of why — a
 * dependency manifest the sandbox has no network for, an unacknowledged repository, an operator's cancel, or the
 * dispatcher's backstop discarding a flight whose deadline passed — and it is a sentence someone can act on, so it is
 * shown as the result rather than hidden behind "no validation report yet", which reads as "nothing wrong".
 *
 * The copy claims only that no report was written. On the backstop-timeout path commands HAVE run — workspace prep
 * happens before the in-lane clock starts, so a cold clone is the ORDINARY way to get here — and their evidence is
 * simply discarded with the flight.
 */
function RefusedWithoutReport({ nodeRun }: { readonly nodeRun: DevWorkflowNodeRunDetailResponse }) {
	const { t } = useTranslation();
	if (!nodeRun.terminalReason && !nodeRun.failureClass) {
		return (
			<Text size="sm" c="dimmed" data-testid="dev-workflow-validation-none">
				{t("pages.devWorkflows.node.noReport", "No report yet.")}
			</Text>
		);
	}

	// A cancel is an answer someone gave, not a fault: it says the same thing about the evidence without the alarm.
	const cancelled = nodeRun.failureClass === "Cancelled";
	return (
		<Alert
			color={cancelled ? "gray" : "red"}
			variant="light"
			icon={cancelled ? undefined : <IconAlertTriangle size={16} />}
			data-testid="dev-workflow-validation-refused"
		>
			<Stack gap={4}>
				<Text size="sm">
					{t(
						"pages.devWorkflows.validation.refused",
						"No report was written, so there is nothing here that evidences what this node did.",
					)}
				</Text>
				{nodeRun.terminalReason ? (
					<Text size="xs" c="dimmed" style={{ whiteSpace: "pre-wrap" }} data-testid="dev-workflow-validation-refused-reason">
						{nodeRun.terminalReason}
					</Text>
				) : null}
			</Stack>
		</Alert>
	);
}

function ValidationReport({
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
				<Alert color="orange" variant="light" icon={<IconAlertTriangle size={16} />} data-testid="dev-workflow-validation-partial">
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
			<Alert mt="xs" color="red" variant="light" icon={<IconAlertTriangle size={16} />} data-testid="dev-workflow-validation-tests-unparsed">
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
