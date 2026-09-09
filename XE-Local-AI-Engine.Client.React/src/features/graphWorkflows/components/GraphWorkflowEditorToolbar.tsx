import { Button, Text } from "@mantine/core";
import { useTranslation } from "react-i18next";

export interface GraphWorkflowEditorToolbarProps {
	readonly definitionName: string;
	/** No definition open: everything but the list is inert. */
	readonly hasDefinition: boolean;
	readonly isDirty: boolean;
	/** A stored graph that had no layout was laid out on open, so it opens dirty (ruling C4) — different words, same state. */
	readonly layoutIsUnsaved: boolean;
	readonly isValidating: boolean;
	readonly isSaving: boolean;
	readonly canSave: boolean;
	readonly onRename: () => void;
	readonly onValidate: () => void;
	readonly onSave: () => void;
	readonly onSaveAs: () => void;
	readonly onStartRun: () => void;
}

/** The canvas's own toolbar: what an operator can do to the open definition, and why a Save is being asked for. */
export function GraphWorkflowEditorToolbar({
	definitionName,
	hasDefinition,
	isDirty,
	layoutIsUnsaved,
	isValidating,
	isSaving,
	canSave,
	onRename,
	onValidate,
	onSave,
	onSaveAs,
	onStartRun,
}: GraphWorkflowEditorToolbarProps) {
	const { t } = useTranslation();

	return (
		<>
			<Text size="sm" fw={600} data-testid="gw-page-definition-name">
				{definitionName}
			</Text>
			<Button size="xs" variant="default" disabled={!hasDefinition || isDirty} onClick={onRename} data-testid="gw-page-rename">
				{t("pages.graphWorkflows.page.rename", "Rename")}
			</Button>
			<Button
				size="xs"
				variant="default"
				loading={isValidating}
				disabled={!hasDefinition}
				onClick={onValidate}
				data-testid="gw-page-validate"
			>
				{t("pages.graphWorkflows.page.validate", "Check")}
			</Button>
			<Button size="xs" loading={isSaving} disabled={!canSave || isSaving} onClick={onSave} data-testid="gw-page-save">
				{t("pages.graphWorkflows.page.save", "Save")}
			</Button>
			<Button size="xs" variant="default" disabled={!hasDefinition} onClick={onSaveAs} data-testid="gw-page-save-as">
				{t("pages.graphWorkflows.page.saveAs", "Save as…")}
			</Button>
			<Button size="xs" variant="light" disabled={!hasDefinition || isDirty} onClick={onStartRun} data-testid="gw-page-start-run">
				{t("pages.graphWorkflows.page.startRun", "Start run")}
			</Button>
			{isDirty ? (
				layoutIsUnsaved ? (
					<Text size="xs" c="dimmed" data-testid="gw-page-unsaved-layout">
						{t("pages.graphWorkflows.page.unsavedLayout", "This graph had no saved layout — Save to keep the one on screen.")}
					</Text>
				) : (
					<Text size="xs" c="dimmed" data-testid="gw-page-save-first">
						{t("pages.graphWorkflows.page.saveFirst", "Save first — a run executes the saved graph, not the canvas.")}
					</Text>
				)
			) : null}
		</>
	);
}
