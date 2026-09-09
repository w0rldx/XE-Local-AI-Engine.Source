import { Alert, Button, Loader, Stack, Text } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import { useMemo } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { DevWorkflowApplyReportPanel } from "@/features/devWorkflows/components/DevWorkflowApplyReportPanel";
import { DevWorkflowValidationReportPanel } from "@/features/devWorkflows/components/DevWorkflowValidationReportPanel";
import { parseDevWorkflowApplyReport } from "@/features/devWorkflows/models/DevWorkflowApplyReport";
import {
	type DevWorkflowNodeRunDetailResponse,
	decodeDevWorkflowArtifactContent,
} from "@/features/devWorkflows/models/DevWorkflowModels";
import { parseDevWorkflowValidationReport } from "@/features/devWorkflows/models/DevWorkflowValidationReport";
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
 * A Tool node is one of two things: a validation node, whose `<nodeKey>-validation.json` says what ran against
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
	const text = useMemo(() => (raw ? decodeDevWorkflowArtifactContent(raw.content ?? "", raw.isBase64 === true).text : ""), [raw]);
	// A Tool node is either a validation node or an apply node, and BOTH write their report under the ordinary
	// `Report` artifact kind — so the document itself is what says which one this is. Handing an apply report to the
	// validation reader produced "could not be read", which is a false alarm about evidence that is perfectly intact.
	const applyReport = useMemo(() => parseDevWorkflowApplyReport(text), [text]);
	const report = useMemo(() => (applyReport ? null : parseDevWorkflowValidationReport(text)), [applyReport, text]);
	const isApply = applyReport !== null;
	// With no artifact at all — or a body neither reader understands — NOTHING says which kind of Tool node this is: the
	// discriminator lives in the graph node's config and the node-run row does not carry it. So every string on those paths is
	// neutral. Calling a refused APPLY node's silence "no validation report was written" names a document that node was
	// never going to write and sends whoever reads it looking for the wrong evidence; the fix is to stop guessing, not
	// to guess better.
	// `primaryArtifactId` is the node's newest artifact, NOT this attempt's: a retry or a fix-loop reset (which
	// puts Succeeded rows back to Pending) leaves attempt N's report standing until attempt N+1's commands land. Both
	// documents carry the attempt they were written for, so an older one is never painted as the current result — a
	// stale "Validation passed" over a node that is re-validating is the one lie this panel must not tell.
	// A decomposition that found no work writes one already-succeeded row per validation node in its template, so an
	// apply downstream can read a validation that really did run for this run. That row wrote no report because it ran
	// nothing, and "no report was written, so there is nothing here that evidences what this node did" would be an
	// alarm about a row behaving exactly as designed.
	const notApplicable = useMemo(() => validationNotApplicable(nodeRun.outputJson), [nodeRun.outputJson]);
	const reportAttempt = applyReport?.attempt ?? report?.attempt;
	const priorAttempt = typeof reportAttempt === "number" && reportAttempt < (nodeRun.attempt ?? 1) ? reportAttempt : undefined;

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
				<InlineErrorAlert
					variant="light"
					message={apiErrorMessage(
						contentQuery.error,
						t("pages.devWorkflows.validation.loadFailed", "Could not load this node's report."),
					)}
					data-testid="dev-workflow-validation-error"
				/>
			) : null}

			{/* An unreadable body is not an empty panel: the document is still in the artifacts tab, verbatim. */}
			{artifactId && contentQuery.isSuccess && report === null && applyReport === null ? (
				<InlineErrorAlert
					variant="light"
					message={t(
						"pages.devWorkflows.validation.unreadable",
						"This node's report could not be read, so its result cannot be trusted. Open it in the artifacts tab to see what was stored.",
					)}
					data-testid="dev-workflow-validation-unreadable"
				/>
			) : null}

			{priorAttempt === undefined ? null : (
				<Alert
					color="yellow"
					variant="light"
					icon={<IconAlertTriangle size={16} />}
					data-testid="dev-workflow-validation-stale-attempt"
				>
					{t(
						"pages.devWorkflows.validation.priorAttempt",
						"The stored report is attempt {{reportAttempt}}'s, and this node is on attempt {{attempt}}. It is not the current result — open it from the artifacts tab if you want the earlier evidence.",
						{ reportAttempt: priorAttempt, attempt: nodeRun.attempt ?? 1 },
					)}
				</Alert>
			)}

			{report && priorAttempt === undefined ? <DevWorkflowValidationReportPanel report={report} nodeRun={nodeRun} /> : null}
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
