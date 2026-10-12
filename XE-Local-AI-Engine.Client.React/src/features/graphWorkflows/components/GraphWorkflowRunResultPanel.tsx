import { Button, CopyButton, Group, Stack } from "@mantine/core";
import { IconCheck, IconCopy } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { MarkdownView } from "@/core/ui/components/MarkdownView/MarkdownView";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { Document } from "@/features/graphWorkflows/components/GraphWorkflowNodePanel";

/**
 * The run's final answer. The run's `output` IS the succeeded End node's resolved `result` value (the outcome stays on
 * that node run), so a run without one carries null. A string is what a report-writing node produced and reads as
 * Markdown; anything else is a document and gets the node panel's read-only JSON view.
 */
export function GraphWorkflowRunResultPanel({ output }: { readonly output: unknown }) {
	const { t } = useTranslation();
	if (output === null || output === undefined) {
		return null;
	}
	const raw = typeof output === "string" ? output : JSON.stringify(output, null, 2);

	return (
		<SectionCard gap="sm" data-testid="graph-workflow-run-result">
			<Group justify="flex-end">
				<CopyButton value={raw} timeout={2000}>
					{({ copied, copy }) => (
						<Button
							size="xs"
							variant="subtle"
							color={copied ? "teal" : "gray"}
							leftSection={copied ? <IconCheck size={14} /> : <IconCopy size={14} />}
							onClick={copy}
							data-testid="graph-workflow-run-result-copy"
						>
							{copied
								? t("pages.graphWorkflows.runResult.copied", "Copied")
								: t("pages.graphWorkflows.runResult.copy", "Copy result")}
						</Button>
					)}
				</CopyButton>
			</Group>
			{typeof output === "string" ? (
				<Stack gap={0} data-testid="graph-workflow-run-result-markdown">
					<MarkdownView content={output} images="link" />
				</Stack>
			) : (
				<Document value={output} testId="graph-workflow-run-result-json" />
			)}
		</SectionCard>
	);
}
