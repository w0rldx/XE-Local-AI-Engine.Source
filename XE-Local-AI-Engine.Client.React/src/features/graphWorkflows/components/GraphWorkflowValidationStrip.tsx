// Everything wrong with the graph, in one strip under the canvas.
//
// The error shape is hybrid on purpose (S0 ⚑-3(c)): a failure the server or the validator can pin to a node or an edge
// carries that key, while a whole-graph rule carries none. This renders the two differently, because a save refused
// over one card must not leave the operator scanning eight of them — a KEYED issue is a button that selects its
// subject, an UNKEYED one is a line in a single Alert above them. Client issues and server issues arrive through the
// same `GraphWorkflowGraphIssue`, so there is one render path, not two.

import { Alert, Button, Group, List, Stack } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import type { GraphWorkflowGraphIssue } from "@/features/graphWorkflows/models/GraphWorkflowValidation";

export interface GraphWorkflowValidationStripProps {
	readonly issues: readonly GraphWorkflowGraphIssue[];
	/** A keyed issue was clicked: the page selects that node or edge. One key namespace, so the subject is unambiguous. */
	readonly onSelectSubject: (subject: string) => void;
}

export function GraphWorkflowValidationStrip({ issues, onSelectSubject }: GraphWorkflowValidationStripProps) {
	const { t } = useTranslation();

	// `serverRejected` carries the server's own sentence; every client rule's message IS its i18n key.
	const issueText = (issue: GraphWorkflowGraphIssue): string =>
		issue.message !== undefined && issue.message.length > 0
			? issue.message
			: t(`pages.graphWorkflows.definition.issues.${issue.rule}`, issue.rule, { subject: issue.subject ?? "" });

	if (issues.length === 0) {
		return null;
	}

	// De-duplicated by the rendered sentence: `noStart` raised by the client and again by the server is ONE problem, and
	// two identical lines would read as two. It also makes the sentence a stable React key.
	const keyed = new Map<string, GraphWorkflowGraphIssue>();
	const unkeyed = new Map<string, string>();
	for (const issue of issues) {
		const text = issueText(issue);
		if (issue.subject !== undefined && issue.subject.length > 0) {
			keyed.set(text, issue);
		} else {
			unkeyed.set(text, text);
		}
	}

	return (
		<Stack gap="xs" data-testid="graph-workflow-validation-strip">
			{unkeyed.size > 0 ? (
				<Alert
					color="red"
					variant="light"
					icon={<IconAlertTriangle size={16} />}
					title={t("pages.graphWorkflows.editor.validation.title", "This graph cannot be saved yet")}
					data-testid="graph-workflow-validation-unkeyed"
				>
					<List size="sm">
						{[...unkeyed.values()].map((line) => (
							<List.Item key={line}>{line}</List.Item>
						))}
					</List>
				</Alert>
			) : null}
			{keyed.size > 0 ? (
				<Group gap="xs" wrap="wrap">
					{[...keyed.entries()].map(([text, issue]) => (
						<Button
							key={text}
							size="compact-xs"
							variant="light"
							color="red"
							onClick={() => onSelectSubject(issue.subject ?? "")}
							data-testid={`graph-workflow-validation-issue-${issue.subject}`}
						>
							{text}
						</Button>
					))}
				</Group>
			) : null}
		</Stack>
	);
}
