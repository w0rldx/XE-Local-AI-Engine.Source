import { Alert, Button, Group, Stack, Text } from "@mantine/core";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { CodeEditor } from "@/core/ui/components/CodeEditor/CodeEditor";
import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { graphWorkflowConflictTypes, readGraphWorkflowConflict } from "@/features/graphWorkflows/api/GraphWorkflowConflict";
import { GRAPH_WORKFLOW_MAX_RUN_INPUT_BYTES } from "@/features/graphWorkflows/models/GraphWorkflowModels";
import { useStartGraphWorkflowRun } from "@/features/graphWorkflows/queries/useGraphWorkflows";

export interface GraphWorkflowStartRunDialogProps {
	readonly opened: boolean;
	readonly onClose: () => void;
	readonly definition: { readonly id: string; readonly name: string; readonly version: number };
	/** The Start node's `config.defaultInput`, which seeds the editor. */
	readonly defaultInput: unknown;
	/** The editor has unsaved edits, so a run would execute the SAVED graph rather than what is on the canvas. */
	readonly isDirty: boolean;
	readonly onStarted: (runId: string) => void;
}

/**
 * Starting a run. Two things here are load-bearing rather than decoration.
 *
 * The `requestId` is minted ONCE per dialog open and reused across every retry, which is the whole of D7's
 * idempotency: a resend after a dropped response returns the run that already started instead of starting a second
 * one. The body is only mounted while the dialog is open, so a new open is a new mount and therefore a new id.
 *
 * And the input is counted in UTF-8 BYTES against the server's cap before it is sent. A 400 after an operator has
 * pasted 64 KiB of JSON is a worse answer than a counter that says so while they paste.
 */
export function GraphWorkflowStartRunDialog({
	opened,
	onClose,
	definition,
	defaultInput,
	isDirty,
	onStarted,
}: GraphWorkflowStartRunDialogProps) {
	const { t } = useTranslation();
	return (
		<DialogShell
			opened={opened}
			onClose={onClose}
			title={t("pages.graphWorkflows.startRun.title", "Start a run")}
			data-testid="graph-workflow-start-run-dialog"
		>
			{opened ? (
				<StartRunForm
					definition={definition}
					defaultInput={defaultInput}
					isDirty={isDirty}
					onStarted={onStarted}
					onClose={onClose}
				/>
			) : null}
		</DialogShell>
	);
}

function seedInput(defaultInput: unknown): string {
	return defaultInput === undefined || defaultInput === null ? "" : JSON.stringify(defaultInput, null, 2);
}

function StartRunForm({
	definition,
	defaultInput,
	isDirty,
	onStarted,
	onClose,
}: {
	readonly definition: { readonly id: string; readonly name: string; readonly version: number };
	readonly defaultInput: unknown;
	readonly isDirty: boolean;
	readonly onStarted: (runId: string) => void;
	readonly onClose: () => void;
}) {
	const { t } = useTranslation();
	const start = useStartGraphWorkflowRun();
	const [input, setInput] = useState(() => seedInput(defaultInput));
	// Minted once for the life of this mount, which IS one dialog open — the mount is what a close tears down.
	const [requestId] = useState(() => crypto.randomUUID());

	const trimmed = input.trim();
	const byteCount = new TextEncoder().encode(trimmed).length;
	const tooLarge = byteCount > GRAPH_WORKFLOW_MAX_RUN_INPUT_BYTES;
	let parsed: unknown;
	let invalidJson = false;
	if (trimmed.length > 0) {
		try {
			parsed = JSON.parse(trimmed);
		} catch {
			invalidJson = true;
		}
	}

	const conflict = readGraphWorkflowConflict(start.error);
	const blocked = isDirty || tooLarge || invalidJson;

	const submit = (): void => {
		start.mutate(
			{
				path: { definitionId: definition.id },
				body: { requestId, input: parsed, definitionVersion: definition.version },
			},
			{
				onSuccess: (data) => {
					if (data.runId) {
						onStarted(data.runId);
					}
				},
			},
		);
	};

	return (
		<Stack gap="sm">
			{/* The run executes the version named here, so the operator can see it is the one they just saved. */}
			<Text size="sm" data-testid="graph-workflow-start-run-version">
				{t("pages.graphWorkflows.startRun.runsVersion", "Runs version {{version}} of {{name}}", {
					version: definition.version,
					name: definition.name,
				})}
			</Text>

			<Text size="xs" fw={500}>
				{t("pages.graphWorkflows.startRun.inputLabel", "Input (JSON)")}
			</Text>
			<CodeEditor
				value={input}
				language="json"
				height={260}
				onChange={setInput}
				aria-label={t("pages.graphWorkflows.startRun.inputLabel", "Input (JSON)")}
				data-testid="graph-workflow-start-run-input"
			/>
			<Text size="xs" c={tooLarge ? "red" : "dimmed"} data-testid="graph-workflow-start-run-bytes">
				{t("pages.graphWorkflows.startRun.byteCount", "{{used}} of {{max}} bytes", {
					used: byteCount,
					max: GRAPH_WORKFLOW_MAX_RUN_INPUT_BYTES,
				})}
			</Text>
			{tooLarge ? (
				<Text size="xs" c="red" data-testid="graph-workflow-start-run-too-large">
					{t("pages.graphWorkflows.startRun.tooLarge", "This input is larger than the node accepts. Remove {{over}} bytes.", {
						over: byteCount - GRAPH_WORKFLOW_MAX_RUN_INPUT_BYTES,
					})}
				</Text>
			) : null}
			{invalidJson ? (
				<Text size="xs" c="red" data-testid="graph-workflow-start-run-invalid-json">
					{t("pages.graphWorkflows.startRun.invalidJson", "Enter valid JSON, or leave it empty.")}
				</Text>
			) : null}
			{isDirty ? (
				<Text size="xs" c="dimmed" data-testid="graph-workflow-start-run-dirty">
					{t("pages.graphWorkflows.startRun.saveFirst", "Save the graph first — a run executes the saved definition, not the canvas.")}
				</Text>
			) : null}

			{conflict?.conflictType === graphWorkflowConflictTypes.runConflict ||
			conflict?.conflictType === graphWorkflowConflictTypes.definitionConflict ? (
				<Alert color="yellow" variant="light" data-testid="graph-workflow-start-run-stale">
					{t(
						"pages.graphWorkflows.startRun.staleVersion",
						"This workflow was saved again since you opened this dialog. Reload it, then start the run.",
					)}
				</Alert>
			) : start.error ? (
				<Alert color="red" variant="light" data-testid="graph-workflow-start-run-error">
					{apiErrorMessage(start.error, t("pages.graphWorkflows.startRun.failed", "The run could not be started."))}
				</Alert>
			) : null}

			<Group justify="flex-end" gap="xs">
				<Button size="xs" variant="subtle" onClick={onClose} data-testid="graph-workflow-start-run-cancel">
					{t("common.cancel", "Cancel")}
				</Button>
				<Button
					size="xs"
					loading={start.isPending}
					disabled={blocked || start.isPending}
					onClick={submit}
					data-testid="graph-workflow-start-run-submit"
				>
					{t("pages.graphWorkflows.startRun.start", "Start run")}
				</Button>
			</Group>
		</Stack>
	);
}
