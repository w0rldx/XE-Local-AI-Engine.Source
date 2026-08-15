import { Alert, Button, Group, Loader, Stack, Text } from "@mantine/core";
import { IconAlertTriangle, IconDeviceFloppy, IconDownload, IconPlus, IconSchool, IconX } from "@tabler/icons-react";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { useUnsavedChangesGuard } from "@/core/ui/hooks/useUnsavedChangesGuard";
import { toast } from "@/core/ui/notifications/Toast";
import { SkillForm, type SkillFormHandle } from "@/features/skills/components/SkillForm";
import { SkillImportDialog } from "@/features/skills/components/SkillImportDialog";
import { SkillList } from "@/features/skills/components/SkillList";
import { toCreateSkillRequest, toUpdateSkillRequest } from "@/features/skills/models/SkillMappers";
import type { Skill, SkillFormValues, SkillSummary } from "@/features/skills/models/SkillModels";
import { useCreateSkill, useDeleteSkill, useSkill, useSkills, useUpdateSkill } from "@/features/skills/queries/useSkills";
import { useSkillManagementStore } from "@/features/skills/stores/SkillManagementStore";

// A new skill always persists enabled by the store default; the create form has no enabled toggle, so the default
// here is true to match what the backend stores.
const emptyFormValues: SkillFormValues = {
	allowedTools: "",
	body: "",
	compatibility: "",
	description: "",
	enabled: true,
	generated: false,
	generationMetadata: null,
	license: "",
	metadata: null,
	name: "",
};

// Frontmatter is carried into the form (as "" when absent) so a save round-trips it: the update endpoint is a full
// replace, so anything the form drops is stored as null.
function toFormValues(skill: Skill): SkillFormValues {
	return {
		allowedTools: skill.allowedTools ?? "",
		body: skill.body,
		compatibility: skill.compatibility ?? "",
		description: skill.description,
		enabled: skill.enabled,
		// An edit starts with no applied draft: `generated` false leaves the stored posture alone, and a null
		// metadata block tells the server to preserve whatever provenance the row already carries.
		generated: false,
		generationMetadata: null,
		license: skill.license ?? "",
		metadata: skill.metadata,
		name: skill.name,
	};
}

export function SkillsPage() {
	const { t } = useTranslation();
	const { confirm } = useConfirm();

	const editorTarget = useSkillManagementStore((state) => state.editorTarget);
	const openCreate = useSkillManagementStore((state) => state.actions.openCreate);
	const openEdit = useSkillManagementStore((state) => state.actions.openEdit);
	const closeEditor = useSkillManagementStore((state) => state.actions.closeEditor);

	// Whether the editor form has unsaved edits. Reported up by the form; drives the dialog close-guard and the
	// route/tab-close guard. Reset whenever the editor closes so a stale dirty flag never lingers.
	const [isDirty, setIsDirty] = useState(false);
	// Purely transient dialog visibility — no reason to put it in the Zustand store, which exists for the editor target
	// that has to survive a cross-route open.
	const [isImportOpen, setIsImportOpen] = useState(false);
	const formRef = useRef<SkillFormHandle>(null);

	// Block in-app navigation + tab close while the open editor has unsaved edits.
	useUnsavedChangesGuard({ isDirty });

	// Reset the transient editor target when the page unmounts so navigating away and back does not reopen the
	// editor (the "stuck editor" bug — the Zustand store is a module singleton that outlives the route).
	useEffect(() => closeEditor, [closeEditor]);

	const skillsQuery = useSkills();
	const createMutation = useCreateSkill();
	const updateMutation = useUpdateSkill();
	const deleteMutation = useDeleteSkill();

	const skills = useMemo(() => skillsQuery.data ?? [], [skillsQuery.data]);

	// The list omits the body; on edit, fetch the full skill so the editor can load its body. Disabled on create.
	const editingId = editorTarget?.mode === "edit" ? editorTarget.id : null;
	const skillQuery = useSkill(editingId);

	const isMutating = createMutation.isPending || updateMutation.isPending || deleteMutation.isPending;
	// Editor save in flight (create or update); drives the footer Save loading state and disables Cancel.
	const isSaving = createMutation.isPending || updateMutation.isPending;

	const submitError =
		createMutation.error || updateMutation.error
			? apiErrorMessage(createMutation.error ?? updateMutation.error, t("pages.skills.errors.save", "Could not save the skill."))
			: undefined;

	// Closes the editor and clears the dirty flag. A successful save closes through here so the next open starts
	// clean; the form is re-keyed per target so its internal state is rebuilt from initialValues.
	const closeAndResetEditor = useCallback(() => {
		setIsDirty(false);
		closeEditor();
	}, [closeEditor]);

	// Saving AI-drafted content demotes the skill server-side (Imported, disabled) whether it was drafted from
	// scratch or improved in place, so the same explanation is owed either way — otherwise a skill the operator just
	// improved silently stops loading. The badge and the disabled toggle arrive with the refetched row; this is the
	// one part of the demotion the UI has to say out loud.
	const notifyIfDemoted = useCallback(
		(values: SkillFormValues) => {
			if (values.generated) {
				toast.warning(
					t(
						"assist.demotionToast",
						"Saved as an imported skill and left disabled. Review the generated instructions, then enable it.",
					),
				);
			}
		},
		[t],
	);

	const handleSubmit = useCallback(
		(values: SkillFormValues) => {
			if (editorTarget?.mode === "edit") {
				const body = toUpdateSkillRequest(values);
				updateMutation.mutate(
					{ path: { skillId: editorTarget.id }, body },
					{
						onSuccess: () => {
							notifyIfDemoted(values);
							closeAndResetEditor();
						},
					},
				);
				return;
			}

			const body = toCreateSkillRequest(values);
			createMutation.mutate(
				{ body },
				{
					onSuccess: () => {
						notifyIfDemoted(values);
						closeAndResetEditor();
					},
				},
			);
		},
		[closeAndResetEditor, createMutation, editorTarget, notifyIfDemoted, updateMutation],
	);

	// Single close path for every dismiss vector (title-bar X, footer Cancel). Confirms a discard first when the
	// form has unsaved edits; overlay/escape dismissal is disabled while dirty so this is the only way out.
	const requestCloseEditor = useCallback(async () => {
		if (isDirty) {
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
		closeAndResetEditor();
	}, [closeAndResetEditor, confirm, isDirty, t]);

	const handleDelete = useCallback(
		async (skill: SkillSummary) => {
			const confirmed = await confirm({
				title: t("pages.skills.delete.title", "Delete skill"),
				description: t("pages.skills.delete.description", "Delete '{{name}}'? This cannot be undone.", {
					name: skill.name,
				}),
				confirmationText: t("common.delete", "Delete"),
				cancellationText: t("common.cancel", "Cancel"),
			});

			if (confirmed) {
				deleteMutation.mutate(
					{ path: { skillId: skill.id } },
					{
						onError: (error) => toast.error(apiErrorMessage(error, t("pages.skills.errors.delete", "Could not delete the skill."))),
					},
				);
			}
		},
		[confirm, deleteMutation, t],
	);

	const isEditorOpen = editorTarget !== null;
	const isEditing = editorTarget?.mode === "edit";
	// On edit, wait for the body fetch before rendering the form; on create, the empty form is ready immediately.
	const isEditorBodyLoading = isEditing && skillQuery.isLoading;
	const editorBodyError = isEditing && skillQuery.error ? skillQuery.error : null;
	const formInitialValues = isEditing && skillQuery.data ? toFormValues(skillQuery.data) : emptyFormValues;

	return (
		<PageShell>
			<PageHeader
				title={t("pages.skills.title", "Skills")}
				icon={<IconSchool size={24} />}
				subtitle={t(
					"pages.skills.subtitle",
					"Author a node-wide library of reusable skills. Assign skills to an agent so its model can load the relevant expertise on demand while it works.",
				)}
				actions={
					<>
						<Button
							variant="default"
							leftSection={<IconDownload size={16} />}
							onClick={() => setIsImportOpen(true)}
							data-testid="skill-import-button"
						>
							{t("pages.skills.importButton", "Import skills")}
						</Button>
						<Button leftSection={<IconPlus size={16} />} onClick={openCreate} data-testid="skill-create-button">
							{t("pages.skills.createButton", "New skill")}
						</Button>
					</>
				}
			/>

			<SectionCard>
				{skillsQuery.isLoading ? (
					<Group gap="sm">
						<Loader size="sm" />
						<Text c="dimmed">{t("pages.skills.list.loading", "Loading skills…")}</Text>
					</Group>
				) : null}
				{skillsQuery.error ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="skills-list-error">
						{apiErrorMessage(skillsQuery.error, t("pages.skills.errors.load", "Could not load skills."))}
					</Alert>
				) : null}
				{!skillsQuery.isLoading && !skillsQuery.error ? (
					<SkillList skills={skills} isMutating={isMutating} onEdit={openEdit} onDelete={handleDelete} />
				) : null}
			</SectionCard>

			<DialogShell
				opened={isEditorOpen}
				onClose={requestCloseEditor}
				title={isEditing ? t("pages.skills.editor.editTitle", "Edit skill") : t("pages.skills.editor.createTitle", "New skill")}
				// Explicit stacking contract: the editor sits below ConfirmProvider's zIndex 400 so the unsaved-changes
				// discard prompt always renders on top of it.
				zIndex={300}
				// Dirty edits must not be lost to an accidental overlay/escape dismiss; the only way out is the guarded
				// close (title-bar X / footer Cancel), which confirms first.
				closeOnClickOutside={!isDirty}
				closeOnEscape={!isDirty}
				footer={
					<>
						<Button
							variant="subtle"
							leftSection={<IconX size={16} />}
							onClick={requestCloseEditor}
							disabled={isSaving}
							data-testid="skill-form-cancel"
						>
							{t("common.cancel", "Cancel")}
						</Button>
						<Button
							leftSection={<IconDeviceFloppy size={16} />}
							onClick={() => formRef.current?.submit()}
							loading={isSaving}
							disabled={isEditorBodyLoading || editorBodyError !== null}
							data-testid="skill-form-submit"
						>
							{t("common.save", "Save")}
						</Button>
					</>
				}
			>
				<Stack gap="md" px="md" pb="md" data-testid="skill-editor-card">
					{isEditorBodyLoading ? (
						<Group gap="sm" data-testid="skill-editor-loading">
							<Loader size="sm" />
							<Text c="dimmed">{t("pages.skills.editor.loading", "Loading skill…")}</Text>
						</Group>
					) : editorBodyError ? (
						<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="skill-editor-error">
							{apiErrorMessage(editorBodyError, t("pages.skills.errors.load", "Could not load skills."))}
						</Alert>
					) : (
						<SkillForm
							// Re-key per editor target AND on the loaded version so the form rebuilds its internal state from the
							// freshly fetched body (the create form keys on "create").
							key={isEditing && skillQuery.data ? `${skillQuery.data.id}-${skillQuery.data.version}` : "create"}
							ref={formRef}
							initialValues={formInitialValues}
							isSubmitting={isSaving}
							submitError={submitError}
							showEnabledToggle={isEditing}
							onSubmit={handleSubmit}
							onCancel={requestCloseEditor}
							onDirtyChange={setIsDirty}
							hideActions={true}
							skillId={isEditing && skillQuery.data ? skillQuery.data.id : undefined}
							provenance={isEditing && skillQuery.data ? skillQuery.data : undefined}
						/>
					)}
				</Stack>
			</DialogShell>

			<SkillImportDialog opened={isImportOpen} onClose={() => setIsImportOpen(false)} />
		</PageShell>
	);
}
