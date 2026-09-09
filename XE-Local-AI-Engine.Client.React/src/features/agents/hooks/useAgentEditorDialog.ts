import { useCallback, useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { useUnsavedChangesGuard } from "@/core/ui/hooks/useUnsavedChangesGuard";
import type { AgentDefinitionFormHandle } from "@/features/agents/components/AgentDefinitionForm";
import type { AgentDefinition } from "@/features/agents/models/AgentDefinitionModels";
import { useAgentManagementStore } from "@/features/agents/stores/AgentManagementStore";

/**
 * The agent editor dialog's own state: which definition it targets, whether the form has unsaved edits, and the one
 * close path every dismiss affordance routes through. The page keeps the mutations — this hook never writes.
 */
export function useAgentEditorDialog(definitions: readonly AgentDefinition[]) {
	const { t } = useTranslation();
	const { confirm } = useConfirm();

	const editorTarget = useAgentManagementStore((state) => state.editorTarget);
	const openCreate = useAgentManagementStore((state) => state.actions.openCreate);
	const openEdit = useAgentManagementStore((state) => state.actions.openEdit);
	const closeEditor = useAgentManagementStore((state) => state.actions.closeEditor);

	// Unsaved-edits state reported by the open editor form. Drives both the dialog close-guard and the route nav-guard.
	const [isEditorDirty, setIsEditorDirty] = useState(false);
	// Imperative handle to the editor form so the dialog footer's Save button can trigger validate-then-submit.
	const formRef = useRef<AgentDefinitionFormHandle>(null);

	// Fix the "stuck editor" bug: the management store is a module singleton whose editorTarget survives route unmount,
	// so navigating away and back would reopen the editor. Reset it when the page unmounts.
	useEffect(() => closeEditor, [closeEditor]);

	// Block in-app navigation / tab close while the editor has unsaved edits (prompts to discard via the shared confirm).
	useUnsavedChangesGuard({ isDirty: isEditorDirty });

	const editingDefinition =
		editorTarget?.mode === "edit" ? definitions.find((definition) => definition.id === editorTarget.id) : undefined;

	// Close the editor and drop the dirty flag together so a stale "dirty" never keeps blocking navigation after the
	// dialog is dismissed. Used for the no-confirm paths (successful save — nothing left to discard).
	const handleCloseEditor = useCallback(() => {
		setIsEditorDirty(false);
		closeEditor();
	}, [closeEditor]);

	// Single page-owned close path for every user-initiated dismiss (title-bar X, footer Cancel, overlay, escape). When
	// the form has unsaved edits it prompts to discard and only closes on confirm; otherwise it closes immediately. This
	// keeps all four dismiss affordances behaving identically (the inconsistency was: footer Cancel discarded silently
	// while the X confirmed).
	const requestCloseEditor = useCallback(async () => {
		if (isEditorDirty) {
			const confirmed = await confirm({
				title: t("components.dialogShell.unsavedTitle", "Discard unsaved changes?"),
				description: t(
					"components.dialogShell.unsavedDescription",
					"You have unsaved changes. If you leave now, they will be lost.",
				),
				confirmationText: t("common.discard", "Discard"),
				cancellationText: t("common.keepEditing", "Keep editing"),
			});
			if (!confirmed) {
				return;
			}
		}
		handleCloseEditor();
	}, [confirm, handleCloseEditor, isEditorDirty, t]);

	return {
		editorTarget,
		editingDefinition,
		isEditorOpen: editorTarget !== null,
		isEditorDirty,
		setIsEditorDirty,
		formRef,
		openCreate,
		openEdit,
		handleCloseEditor,
		requestCloseEditor,
	};
}
