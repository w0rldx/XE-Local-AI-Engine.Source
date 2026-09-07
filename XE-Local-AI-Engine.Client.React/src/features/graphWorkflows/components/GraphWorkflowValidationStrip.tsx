// Everything wrong with the graph, in one strip under the canvas.
//
// The error shape is hybrid on purpose (S0 ⚑-3(c)): a failure the server or the validator can pin to a node or an edge
// carries that key, while a whole-graph rule carries none. This renders the two differently, because a save refused
// over one card must not leave the operator scanning eight of them — a KEYED issue is a button that selects its
// subject, an UNKEYED one is a line in a single Alert above them. Client issues and server issues arrive through the
// same `GraphWorkflowGraphIssue`, so there is one render path, not two.
//
// Warnings (F5-2b) arrive through that same shape with `severity: "warning"` and render in their own Alert below the
// errors: a different colour, a title that asks rather than refuses, and no claim that the graph cannot be saved —
// because it can. Only the server raises them; every client rule mirrors a rule that REFUSES a save.

import { Alert, Button, Group, List, Stack } from "@mantine/core";
import { IconAlertTriangle, IconInfoCircle } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { type GraphWorkflowGraphIssue, isGraphWorkflowWarning } from "@/features/graphWorkflows/models/GraphWorkflowValidation";

export interface GraphWorkflowValidationStripProps {
	readonly issues: readonly GraphWorkflowGraphIssue[];
	/** A keyed issue was clicked: the page selects that node or edge. One key namespace, so the subject is unambiguous. */
	readonly onSelectSubject: (subject: string) => void;
}

/**
 * De-duplicated: `noStart` raised by the client and again by the server is ONE problem, and two identical lines would
 * read as two. A keyed issue is identified by WHAT it is about, not by how it reads — two server errors on different
 * nodes can carry the same sentence, and collapsing those would hide one of the two bad cards.
 */
function split(issues: readonly GraphWorkflowGraphIssue[], issueText: (issue: GraphWorkflowGraphIssue) => string) {
	const keyed = new Map<string, GraphWorkflowGraphIssue>();
	const unkeyed = new Map<string, string>();
	for (const issue of issues) {
		const text = issueText(issue);
		if (issue.subject !== undefined && issue.subject.length > 0) {
			keyed.set(`${issue.subject}|${issue.rule}|${issue.message ?? ""}`, issue);
		} else {
			unkeyed.set(text, text);
		}
	}
	return { keyed, unkeyed };
}

export function GraphWorkflowValidationStrip({ issues, onSelectSubject }: GraphWorkflowValidationStripProps) {
	const { t } = useTranslation();

	// `serverRejected` and `serverWarned` carry the server's own sentence; every client rule's message IS its i18n key.
	const issueText = (issue: GraphWorkflowGraphIssue): string =>
		issue.message !== undefined && issue.message.length > 0
			? issue.message
			: t(`pages.graphWorkflows.definition.issues.${issue.rule}`, issue.rule, { subject: issue.subject ?? "" });

	if (issues.length === 0) {
		return null;
	}

	const errors = split(
		issues.filter((issue) => !isGraphWorkflowWarning(issue)),
		issueText,
	);
	const warnings = split(issues.filter(isGraphWorkflowWarning), issueText);

	const chip = (issue: GraphWorkflowGraphIssue, identity: string, warning: boolean) => (
		<Button
			key={identity}
			size="compact-xs"
			variant="light"
			color={warning ? "yellow" : "red"}
			onClick={() => onSelectSubject(issue.subject ?? "")}
			data-testid={`graph-workflow-validation-${warning ? "warning" : "issue"}-${issue.subject}`}
		>
			{issueText(issue)}
		</Button>
	);

	return (
		<Stack gap="xs" data-testid="graph-workflow-validation-strip">
			{errors.unkeyed.size > 0 ? (
				<Alert
					color="red"
					variant="light"
					icon={<IconAlertTriangle size={16} />}
					title={t("pages.graphWorkflows.editor.validation.title", "This graph cannot be saved yet")}
					data-testid="graph-workflow-validation-unkeyed"
				>
					<List size="sm">
						{[...errors.unkeyed.values()].map((line) => (
							<List.Item key={line}>{line}</List.Item>
						))}
					</List>
				</Alert>
			) : null}
			{errors.keyed.size > 0 ? (
				// A long issue list SCROLLS rather than growing: fifteen chips would otherwise push the strip up over the canvas
				// and cover the controls. `flex: "0 0 auto"` keeps it out of the canvas's share of the column.
				<Group
					gap="xs"
					wrap="wrap"
					style={{ maxHeight: 120, overflowY: "auto", flex: "0 0 auto" }}
					data-testid="graph-workflow-validation-issues"
				>
					{[...errors.keyed.entries()].map(([identity, issue]) => chip(issue, identity, false))}
				</Group>
			) : null}
			{warnings.keyed.size > 0 || warnings.unkeyed.size > 0 ? (
				// One Alert for the whole warning half, keyed chips inside it: a warning is not a reason to save less, so it
				// gets a colour and a title that say "look" rather than "stop", and never the error strip's refusal.
				<Alert
					color="yellow"
					variant="light"
					icon={<IconInfoCircle size={16} />}
					title={t("pages.graphWorkflows.editor.validation.warningsTitle", "Worth checking before you run")}
					data-testid="graph-workflow-validation-warnings"
				>
					<Stack gap="xs">
						{warnings.unkeyed.size > 0 ? (
							<List size="sm">
								{[...warnings.unkeyed.values()].map((line) => (
									<List.Item key={line}>{line}</List.Item>
								))}
							</List>
						) : null}
						{warnings.keyed.size > 0 ? (
							<Group gap="xs" wrap="wrap" style={{ maxHeight: 120, overflowY: "auto", flex: "0 0 auto" }}>
								{[...warnings.keyed.entries()].map(([identity, issue]) => chip(issue, identity, true))}
							</Group>
						) : null}
					</Stack>
				</Alert>
			) : null}
		</Stack>
	);
}
