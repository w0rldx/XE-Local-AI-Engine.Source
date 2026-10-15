import { Badge, Button, Text } from "@mantine/core";
import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";

import { DisabledReasonTooltip } from "@/core/ui/components/DisabledReasonTooltip/DisabledReasonTooltip";

export interface GraphWorkflowEditorToolbarProps {
	readonly definitionName: string;
	/** The definition may run action tools (ADR 0021); shown as a badge beside the name. */
	readonly allowsActions?: boolean;
	/** No definition open: everything but the list is inert. */
	readonly hasDefinition: boolean;
	readonly isDirty: boolean;
	/** A stored graph that had no layout was laid out on open, so it opens dirty — different words, same state. */
	readonly layoutIsUnsaved: boolean;
	/** Rename (a metadata write that bumps the version and reloads the canvas) is safe now. */
	readonly canEditMeta: boolean;
	readonly isValidating: boolean;
	readonly isSaving: boolean;
	readonly canSave: boolean;
	readonly onRename: () => void;
	readonly onValidate: () => void;
	readonly onSave: () => void;
	readonly onSaveAs: () => void;
	readonly onStartRun: () => void;
	/** Downloads the SAVED definition as a share file; offered only on a clean canvas. */
	readonly onExport: () => void;
	readonly isExporting: boolean;
	/** The graph-level settings control, rendered beside Check. */
	readonly settings?: ReactNode;
}

/** The canvas's own toolbar: what an operator can do to the open definition, and why a Save is being asked for. */
export function GraphWorkflowEditorToolbar({
	definitionName,
	allowsActions = false,
	hasDefinition,
	isDirty,
	layoutIsUnsaved,
	canEditMeta,
	isValidating,
	isSaving,
	canSave,
	onRename,
	onValidate,
	onSave,
	onSaveAs,
	onStartRun,
	onExport,
	isExporting,
	settings,
}: GraphWorkflowEditorToolbarProps) {
	const { t } = useTranslation();

	return (
		<>
			<Text size="sm" fw={600} data-testid="gw-page-definition-name">
				{definitionName}
			</Text>
			{allowsActions ? (
				<Badge size="xs" color="orange" variant="light" data-testid="gw-page-actions-badge">
					{t("pages.graphWorkflows.definitions.actionsBadge", "Actions")}
				</Badge>
			) : null}
			<Button
				size="xs"
				variant="default"
				disabled={!hasDefinition || !canEditMeta}
				onClick={onRename}
				data-testid="gw-page-rename"
			>
				{t("pages.graphWorkflows.page.rename", "Rename")}
			</Button>
			{settings}
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
			<DisabledReasonTooltip
				label={t("pages.graphWorkflows.page.exportSaveFirst", "Save first — an export is the saved workflow, not the canvas.")}
				disabled={!isDirty}
				controlDisabled={!hasDefinition || isDirty}
			>
				<Button
					size="xs"
					variant="default"
					loading={isExporting}
					disabled={!hasDefinition || isDirty}
					onClick={onExport}
					data-testid="gw-page-export"
				>
					{t("pages.graphWorkflows.page.export", "Export")}
				</Button>
			</DisabledReasonTooltip>
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
