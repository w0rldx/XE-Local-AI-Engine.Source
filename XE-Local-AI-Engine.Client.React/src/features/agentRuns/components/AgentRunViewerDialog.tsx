import { Alert, Button, Group, Loader, Stack, Tabs, Text } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { CodeEditor } from "@/core/ui/components/CodeEditor/CodeEditor";
import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { useAgentRunLog, useAgentRunPatch } from "@/features/agentRuns/queries/useAgentRuns";

interface AgentRunViewerDialogProps {
	readonly runId: string;
	readonly onClose: () => void;
}

type ViewerTab = "log" | "patch";

/**
 * What one run actually wrote: its event log and its exported patch, side by side and read-only.
 *
 * Both files are model-authored, so neither is rewritten on the way here — an operator reading a doctored patch needs
 * to see the bytes the run produced, not a cleaned-up rendering of them. What makes that safe to show is the surface:
 * `CodeEditor` is Monaco with its unicode highlighting left at the default, which marks invisible, ambiguous and
 * bidirectional characters in place, so a right-to-left override cannot silently reorder a line an operator is about
 * to approve elsewhere.
 *
 * Each tab fetches only while this dialog is open, and the inactive tab's file is fetched too — both are small reads
 * an operator opening the viewer is about to want, and splitting them per tab would trade one request for a spinner
 * on every tab click.
 */
export function AgentRunViewerDialog({ runId, onClose }: AgentRunViewerDialogProps) {
	const { t } = useTranslation();
	const [tab, setTab] = useState<ViewerTab>("log");
	const log = useAgentRunLog(runId, true);
	const patch = useAgentRunPatch(runId, true);

	return (
		<DialogShell
			opened={true}
			onClose={onClose}
			title={t("pages.agentRuns.viewer.title", "Run contents")}
			size="xl"
			data-testid="agent-run-viewer-dialog"
			footer={
				<Button variant="default" onClick={onClose} data-testid="agent-run-viewer-close">
					{t("common.close", "Close")}
				</Button>
			}
		>
			<Tabs value={tab} onChange={(value) => setTab(value === "patch" ? "patch" : "log")}>
				<Tabs.List>
					<Tabs.Tab value="log" data-testid="agent-run-viewer-tab-log">
						{t("pages.agentRuns.viewer.logTab", "Log")}
					</Tabs.Tab>
					<Tabs.Tab value="patch" data-testid="agent-run-viewer-tab-patch">
						{t("pages.agentRuns.viewer.patchTab", "Patch")}
					</Tabs.Tab>
				</Tabs.List>
				<Tabs.Panel value="log" pt="sm">
					<ViewerPanel
						query={log}
						language="plaintext"
						emptyMessage={t("pages.agentRuns.viewer.logEmpty", "This run recorded no log.")}
						errorMessage={t("pages.agentRuns.viewer.logError", "The run's log could not be read.")}
						testId="agent-run-viewer-log"
					/>
				</Tabs.Panel>
				<Tabs.Panel value="patch" pt="sm">
					<ViewerPanel
						query={patch}
						language="diff"
						emptyMessage={t("pages.agentRuns.viewer.patchEmpty", "This run exported no patch.")}
						errorMessage={t("pages.agentRuns.viewer.patchError", "The run's patch could not be read.")}
						testId="agent-run-viewer-patch"
					/>
				</Tabs.Panel>
			</Tabs>
		</DialogShell>
	);
}

/** One tab's body: the four states a capped read of a run file can be in. */
function ViewerPanel({
	query,
	language,
	emptyMessage,
	errorMessage,
	testId,
}: {
	readonly query: {
		readonly data?: { readonly text: string; readonly truncated: boolean };
		readonly isPending: boolean;
		readonly isError: boolean;
		readonly error: unknown;
	};
	readonly language: string;
	readonly emptyMessage: string;
	readonly errorMessage: string;
	readonly testId: string;
}) {
	const { t } = useTranslation();

	if (query.isPending) {
		return (
			<Group gap="sm" data-testid={`${testId}-loading`}>
				<Loader size="sm" />
				<Text size="sm" c="dimmed">
					{t("pages.agentRuns.viewer.loading", "Reading…")}
				</Text>
			</Group>
		);
	}

	if (query.isError) {
		return <InlineErrorAlert message={apiErrorMessage(query.error, errorMessage)} data-testid={`${testId}-error`} />;
	}

	const text = query.data?.text ?? "";
	if (text.length === 0) {
		return <EmptyState message={emptyMessage} data-testid={`${testId}-empty`} />;
	}

	return (
		<Stack gap="xs">
			{query.data?.truncated === true ? (
				<Alert color="yellow" variant="light" icon={<IconAlertTriangle size={16} />} data-testid={`${testId}-truncated`}>
					{t("pages.agentRuns.viewer.truncated", "This file is longer than what is shown here; part of it was left out.")}
				</Alert>
			) : null}
			<CodeEditor value={text} language={language} readOnly={true} height={420} aria-label={testId} data-testid={testId} />
		</Stack>
	);
}
