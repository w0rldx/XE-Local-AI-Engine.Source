import { Alert, Button, Group, Loader, Text } from "@mantine/core";
import { IconAlertTriangle, IconDeviceFloppy, IconPlug, IconPlus, IconX } from "@tabler/icons-react";
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
import {
	IntegrationTriggerForm,
	type IntegrationTriggerFormHandle,
} from "@/features/integrations/components/IntegrationTriggerForm";
import { IntegrationTriggerList } from "@/features/integrations/components/IntegrationTriggerList";
import {
	toCreateIntegrationTriggerRequest,
	toIntegrationTriggerFormValues,
	toUpdateIntegrationTriggerRequest,
} from "@/features/integrations/models/IntegrationMappers";
import {
	emptyIntegrationTriggerFormValues,
	type IntegrationTrigger,
	type IntegrationTriggerFormValues,
} from "@/features/integrations/models/IntegrationModels";
import { useIntegrationAgentOptions } from "@/features/integrations/queries/useIntegrationAgentOptions";
import {
	useCreateIntegrationTrigger,
	useDeleteIntegrationTrigger,
	useIntegrationTriggers,
	useUpdateIntegrationTrigger,
} from "@/features/integrations/queries/useIntegrationTriggers";
import { useIntegrationsUiStore } from "@/features/integrations/stores/IntegrationsUiStore";

// Operator surface for the named external entry points of the loopback integration API. Each trigger binds a slug
// an integrator calls to a saved agent that runs unattended.
export function IntegrationTriggersPage() {
	const { t } = useTranslation();
	const { confirm } = useConfirm();

	const editorTarget = useIntegrationsUiStore((state) => state.editorTarget);
	const openCreate = useIntegrationsUiStore((state) => state.actions.openCreate);
	const openEdit = useIntegrationsUiStore((state) => state.actions.openEdit);
	const closeEditor = useIntegrationsUiStore((state) => state.actions.closeEditor);

	// Reset the editor on unmount so navigating away and back does not reopen it from stale Zustand state.
	useEffect(() => {
		return () => {
			closeEditor();
		};
	}, [closeEditor]);

	const [isFormDirty, setIsFormDirty] = useState(false);
	useUnsavedChangesGuard({ isDirty: isFormDirty });

	const formRef = useRef<IntegrationTriggerFormHandle>(null);

	const triggersQuery = useIntegrationTriggers();
	const {
		options: agents,
		toolsByName,
		isLoading: isCatalogLoading,
		isError: isCatalogError,
	} = useIntegrationAgentOptions();

	const createMutation = useCreateIntegrationTrigger();
	const updateMutation = useUpdateIntegrationTrigger();
	const deleteMutation = useDeleteIntegrationTrigger();

	const triggers = useMemo(() => triggersQuery.data ?? [], [triggersQuery.data]);

	const editingTrigger = useMemo(() => {
		if (editorTarget?.mode !== "edit") {
			return undefined;
		}
		return triggers.find((trigger) => trigger.id === editorTarget.id);
	}, [triggers, editorTarget]);

	const isMutating = createMutation.isPending || updateMutation.isPending || deleteMutation.isPending;
	const isSubmitting = createMutation.isPending || updateMutation.isPending;

	// A trigger the list no longer carries has no version to send, so an edit save cannot become a create with the
	// same click — the verb would change silently. Block the save and say why.
	const missingTriggerError =
		editorTarget?.mode === "edit" && editingTrigger === undefined
			? t(
					"pages.integrations.triggers.errors.missing",
					"This trigger no longer exists. Close the editor and reload the list.",
				)
			: undefined;

	// An empty tool catalog reads as "every tool needs approval" because the resolution is fail-closed. That is the
	// right direction, but the operator has to be told the catalog is why.
	const toolCatalogNotice = isCatalogLoading
		? t("pages.integrations.triggers.form.toolCatalog.loading", "Loading the tool catalog. Tool facts are unknown until it arrives.")
		: isCatalogError
			? t(
					"pages.integrations.triggers.form.toolCatalog.failed",
					"The tool catalog could not be loaded. Every tool counts as approval-requiring and side-effecting until it can be read.",
				)
			: undefined;

	// The local CallerManaged check is a preflight, not the authorization: a save the server rejects anyway (a stale
	// catalog, a tool added to the agent between the read and the submit) surfaces the server's message here.
	const submitError =
		createMutation.error || updateMutation.error
			? apiErrorMessage(
					createMutation.error ?? updateMutation.error,
					t("pages.integrations.triggers.errors.save", "Could not save the trigger."),
				)
			: undefined;

	const closeEditorClean = useCallback(() => {
		setIsFormDirty(false);
		closeEditor();
	}, [closeEditor]);

	const handleSubmit = useCallback(
		(values: IntegrationTriggerFormValues) => {
			if (editorTarget?.mode === "edit") {
				if (editingTrigger === undefined) {
					return;
				}
				updateMutation.mutate(
					{
						path: { triggerId: editingTrigger.id },
						body: toUpdateIntegrationTriggerRequest(values, editingTrigger.version),
					},
					{ onSuccess: () => closeEditorClean() },
				);
				return;
			}

			createMutation.mutate({ body: toCreateIntegrationTriggerRequest(values) }, { onSuccess: () => closeEditorClean() });
		},
		[closeEditorClean, createMutation, editingTrigger, editorTarget, updateMutation],
	);

	const handleDelete = useCallback(
		async (trigger: IntegrationTrigger) => {
			const confirmed = await confirm({
				title: t("pages.integrations.triggers.delete.title", "Delete trigger"),
				description: t(
					"pages.integrations.triggers.delete.description",
					"Delete '{{name}}'? Callers using it start receiving 404 responses. This cannot be undone.",
					{ name: trigger.displayName },
				),
				confirmationText: t("common.delete", "Delete"),
				cancellationText: t("common.cancel", "Cancel"),
			});

			if (confirmed) {
				deleteMutation.mutate(
					{ path: { triggerId: trigger.id } },
					{
						onError: (error) =>
							toast.error(apiErrorMessage(error, t("pages.integrations.triggers.errors.delete", "Could not delete the trigger."))),
					},
				);
			}
		},
		[confirm, deleteMutation, t],
	);

	// The enable switch is an update like any other, so it carries the row's own expectedVersion.
	const handleToggleEnabled = useCallback(
		(trigger: IntegrationTrigger, enabled: boolean) => {
			updateMutation.mutate(
				{
					path: { triggerId: trigger.id },
					body: toUpdateIntegrationTriggerRequest({ ...toIntegrationTriggerFormValues(trigger), enabled }, trigger.version),
				},
				{
					onError: (error) =>
						toast.error(apiErrorMessage(error, t("pages.integrations.triggers.errors.save", "Could not save the trigger."))),
				},
			);
		},
		[t, updateMutation],
	);

	const requestCloseEditor = useCallback(async () => {
		if (!isFormDirty) {
			closeEditorClean();
			return;
		}
		const shouldDiscard = await confirm({
			title: t("components.dialogShell.unsavedTitle", "Unsaved changes"),
			description: t("components.dialogShell.unsavedDescription", "You have unsaved changes. Discard them and leave?"),
			confirmationText: t("common.discard", "Discard"),
			cancellationText: t("common.keepEditing", "Keep editing"),
		});
		if (shouldDiscard) {
			closeEditorClean();
		}
	}, [isFormDirty, closeEditorClean, confirm, t]);

	const editorFooter = (
		<>
			<Button
				variant="subtle"
				leftSection={<IconX size={16} />}
				onClick={requestCloseEditor}
				disabled={isSubmitting}
				data-testid="integration-trigger-form-cancel"
			>
				{t("common.cancel", "Cancel")}
			</Button>
			<Button
				leftSection={<IconDeviceFloppy size={16} />}
				onClick={() => formRef.current?.submit()}
				loading={isSubmitting}
				data-testid="integration-trigger-form-submit"
			>
				{t("common.save", "Save")}
			</Button>
		</>
	);

	const loadError = triggersQuery.error
		? apiErrorMessage(triggersQuery.error, t("pages.integrations.triggers.errors.load", "Could not load integration triggers."))
		: undefined;

	return (
		<PageShell>
			<PageHeader
				title={t("pages.integrations.triggers.title", "Integration triggers")}
				icon={<IconPlug size={24} />}
				subtitle={t(
					"pages.integrations.triggers.subtitle",
					"A trigger is a named external entry point that runs a saved agent unattended over the loopback integration API.",
				)}
				actions={
					<Button leftSection={<IconPlus size={16} />} onClick={openCreate} data-testid="integration-trigger-create-button">
						{t("pages.integrations.triggers.createButton", "Create trigger")}
					</Button>
				}
			/>

			<DialogShell
				title={
					editorTarget?.mode === "edit"
						? t("pages.integrations.triggers.editor.editTitle", "Edit trigger")
						: t("pages.integrations.triggers.editor.createTitle", "Create trigger")
				}
				opened={editorTarget !== null}
				// The page owns the single confirm-on-dirty path (requestCloseEditor, wired to onClose AND footer Cancel),
				// so DialogShell's own confirmCloseWhen stays off — with both on, a dirty close prompts twice.
				onClose={requestCloseEditor}
				closeOnClickOutside={!isFormDirty}
				closeOnEscape={!isFormDirty}
				footer={editorFooter}
				zIndex={300}
				data-testid="integration-trigger-editor"
			>
				<IntegrationTriggerForm
					ref={formRef}
					key={editorTarget?.mode === "edit" ? editorTarget.id : "create"}
					initialValues={editingTrigger ? toIntegrationTriggerFormValues(editingTrigger) : emptyIntegrationTriggerFormValues}
					agents={agents}
					toolsByName={toolsByName}
					isEditing={editorTarget?.mode === "edit"}
					submitError={missingTriggerError ?? submitError}
					toolCatalogNotice={toolCatalogNotice}
					onSubmit={handleSubmit}
					onDirtyChange={setIsFormDirty}
				/>
			</DialogShell>

			<SectionCard data-testid="integration-triggers-card">
				{triggersQuery.isLoading ? (
					<Group gap="sm">
						<Loader size="sm" />
						<Text c="dimmed">{t("pages.integrations.triggers.list.loading", "Loading integration triggers…")}</Text>
					</Group>
				) : null}
				{loadError ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="integration-triggers-error">
						{loadError}
					</Alert>
				) : null}
				{!(triggersQuery.isLoading || loadError) ? (
					<IntegrationTriggerList
						triggers={triggers}
						agents={agents}
						isMutating={isMutating}
						onEdit={openEdit}
						onDelete={handleDelete}
						onToggleEnabled={handleToggleEnabled}
					/>
				) : null}
			</SectionCard>
		</PageShell>
	);
}
