import { Alert, Button, Card, Container, Group, Loader, Stack, Text, Title } from "@mantine/core";
import { IconAlertTriangle, IconDeviceFloppy, IconPlus, IconTools, IconX } from "@tabler/icons-react";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { useUnsavedChangesGuard } from "@/core/ui/hooks/useUnsavedChangesGuard";
import { toast } from "@/core/ui/notifications/Toast";
import { CustomToolForm, type CustomToolFormHandle } from "@/features/customTools/components/CustomToolForm";
import { CustomToolList } from "@/features/customTools/components/CustomToolList";
import { toDefinition, toFormValues } from "@/features/customTools/models/CustomToolMappers";
import type { CustomToolFormValues, CustomToolView } from "@/features/customTools/models/CustomToolModels";
import {
	useCreateCustomTool,
	useCustomTool,
	useCustomTools,
	useDeleteCustomTool,
	useUpdateCustomTool,
} from "@/features/customTools/queries/useCustomTools";
import { useCustomToolManagementStore } from "@/features/customTools/stores/CustomToolManagementStore";

// A new tool starts disabled and unacknowledged: enabling and acknowledging are deliberate acts in the editor.
const emptyFormValues: CustomToolFormValues = {
	name: "",
	description: "",
	kind: "HttpFetch",
	mode: "Fixed",
	enabled: false,
	acknowledged: false,
	parameters: [],
	http: { method: "GET", urlTemplate: "", headers: [], bodyTemplate: "", allowedHosts: [] },
	command: { executable: "", argsTemplate: [], workingDirectory: "", timeoutSeconds: 0, env: [] },
};

export function CustomToolsPage() {
	const { t } = useTranslation();
	const { confirm } = useConfirm();

	const editorTarget = useCustomToolManagementStore((state) => state.editorTarget);
	const openCreate = useCustomToolManagementStore((state) => state.actions.openCreate);
	const openEdit = useCustomToolManagementStore((state) => state.actions.openEdit);
	const closeEditor = useCustomToolManagementStore((state) => state.actions.closeEditor);

	const [isDirty, setIsDirty] = useState(false);
	// The danger acknowledgement drives the footer Save gate; the server enforces it too, so this is the visible half.
	const [isAcknowledged, setIsAcknowledged] = useState(false);
	const formRef = useRef<CustomToolFormHandle>(null);

	useUnsavedChangesGuard({ isDirty });

	// Reset the transient editor target on unmount so navigating away and back never reopens a stale editor.
	useEffect(() => closeEditor, [closeEditor]);

	const toolsQuery = useCustomTools();
	const createMutation = useCreateCustomTool();
	const updateMutation = useUpdateCustomTool();
	const deleteMutation = useDeleteCustomTool();

	const tools = useMemo(() => toolsQuery.data ?? [], [toolsQuery.data]);

	const editingId = editorTarget?.mode === "edit" ? editorTarget.id : null;
	const toolQuery = useCustomTool(editingId);

	const isMutating = createMutation.isPending || updateMutation.isPending || deleteMutation.isPending;
	const isSaving = createMutation.isPending || updateMutation.isPending;

	const submitError =
		createMutation.error || updateMutation.error
			? apiErrorMessage(createMutation.error ?? updateMutation.error, t("pages.customTools.errors.save", "Could not save the custom tool."))
			: undefined;

	const closeAndResetEditor = useCallback(() => {
		setIsDirty(false);
		setIsAcknowledged(false);
		closeEditor();
	}, [closeEditor]);

	const handleSubmit = useCallback(
		(values: CustomToolFormValues) => {
			const body = toDefinition(values);
			if (editorTarget?.mode === "edit") {
				updateMutation.mutate({ path: { customToolId: editorTarget.id }, body }, { onSuccess: () => closeAndResetEditor() });
				return;
			}
			createMutation.mutate({ body }, { onSuccess: () => closeAndResetEditor() });
		},
		[closeAndResetEditor, createMutation, editorTarget, updateMutation],
	);

	const requestCloseEditor = useCallback(async () => {
		if (isDirty) {
			const confirmed = await confirm({
				title: t("components.dialogShell.unsavedTitle", "Discard unsaved changes?"),
				description: t("components.dialogShell.unsavedDescription", "You have unsaved changes. If you leave now, they will be lost."),
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
		async (tool: CustomToolView) => {
			const confirmed = await confirm({
				title: t("pages.customTools.delete.title", "Delete custom tool"),
				description: t("pages.customTools.delete.description", "Delete '{{name}}'? This cannot be undone.", { name: tool.name }),
				confirmationText: t("common.delete", "Delete"),
				cancellationText: t("common.cancel", "Cancel"),
			});

			if (confirmed) {
				deleteMutation.mutate(
					{ path: { customToolId: tool.id } },
					{ onError: (error) => toast.error(apiErrorMessage(error, t("pages.customTools.errors.delete", "Could not delete the custom tool."))) },
				);
			}
		},
		[confirm, deleteMutation, t],
	);

	const isEditorOpen = editorTarget !== null;
	const isEditing = editorTarget?.mode === "edit";
	const isEditorBodyLoading = isEditing && toolQuery.isLoading;
	const editorBodyError = isEditing && toolQuery.error ? toolQuery.error : null;
	const formInitialValues = isEditing && toolQuery.data ? toFormValues(toolQuery.data) : emptyFormValues;

	return (
		<Container fluid={true} py="lg">
			<Stack gap="lg">
				<Group justify="space-between" align="flex-start">
					<Stack gap={4}>
						<Text size="sm" tt="uppercase" fw={700} c="dimmed">
							{t("pages.customTools.eyebrow", "Worker Node")}
						</Text>
						<Group gap="xs" align="center">
							<IconTools size={24} />
							<Title order={2}>{t("pages.customTools.title", "Custom tools")}</Title>
						</Group>
						<Text c="dimmed">
							{t(
								"pages.customTools.subtitle",
								"Author HTTP and host-command tools your agents can call. They run on this machine under per-call approval — assign one to an agent, off by default.",
							)}
						</Text>
					</Stack>
					<Button leftSection={<IconPlus size={16} />} onClick={openCreate} data-testid="custom-tool-create-button">
						{t("pages.customTools.createButton", "New custom tool")}
					</Button>
				</Group>

				<Alert color="red" variant="light" icon={<IconAlertTriangle size={18} />} data-testid="custom-tools-danger-banner">
					{t(
						"pages.customTools.dangerBanner",
						"Custom tools run commands, call networks, and launch programs on the host machine with your access. Only enable a tool whose exact behaviour you trust — every call still asks for your approval, but the code runs here.",
					)}
				</Alert>

				<Card withBorder={true} radius="md" p="lg">
					<Stack gap="md">
						{toolsQuery.isLoading ? (
							<Group gap="sm">
								<Loader size="sm" />
								<Text c="dimmed">{t("pages.customTools.list.loading", "Loading custom tools…")}</Text>
							</Group>
						) : null}
						{toolsQuery.error ? (
							<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="custom-tools-list-error">
								{apiErrorMessage(toolsQuery.error, t("pages.customTools.errors.load", "Could not load custom tools."))}
							</Alert>
						) : null}
						{!toolsQuery.isLoading && !toolsQuery.error ? (
							<CustomToolList tools={tools} isMutating={isMutating} onEdit={openEdit} onDelete={handleDelete} />
						) : null}
					</Stack>
				</Card>
			</Stack>

			<DialogShell
				opened={isEditorOpen}
				onClose={requestCloseEditor}
				title={
					isEditing
						? t("pages.customTools.editor.editTitle", "Edit custom tool")
						: t("pages.customTools.editor.createTitle", "New custom tool")
				}
				zIndex={300}
				closeOnClickOutside={!isDirty}
				closeOnEscape={!isDirty}
				footer={
					<>
						<Button
							variant="subtle"
							leftSection={<IconX size={16} />}
							onClick={requestCloseEditor}
							disabled={isSaving}
							data-testid="custom-tool-form-cancel"
						>
							{t("common.cancel", "Cancel")}
						</Button>
						<Button
							leftSection={<IconDeviceFloppy size={16} />}
							onClick={() => formRef.current?.submit()}
							loading={isSaving}
							// Gated on the danger acknowledgement (the server enforces it too) plus the edit-body load state.
							disabled={isEditorBodyLoading || editorBodyError !== null || !isAcknowledged}
							data-testid="custom-tool-form-submit"
						>
							{t("common.save", "Save")}
						</Button>
					</>
				}
			>
				<Stack gap="md" px="md" pb="md" data-testid="custom-tool-editor-card">
					{isEditorBodyLoading ? (
						<Group gap="sm" data-testid="custom-tool-editor-loading">
							<Loader size="sm" />
							<Text c="dimmed">{t("pages.customTools.editor.loading", "Loading custom tool…")}</Text>
						</Group>
					) : editorBodyError ? (
						<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="custom-tool-editor-error">
							{apiErrorMessage(editorBodyError, t("pages.customTools.errors.load", "Could not load custom tools."))}
						</Alert>
					) : (
						<CustomToolForm
							key={isEditing && toolQuery.data ? `${toolQuery.data.id}-${toolQuery.data.version}` : "create"}
							ref={formRef}
							initialValues={formInitialValues}
							isSubmitting={isSaving}
							submitError={submitError}
							showEnabledToggle={isEditing}
							onSubmit={handleSubmit}
							onCancel={requestCloseEditor}
							onDirtyChange={setIsDirty}
							onAcknowledgedChange={setIsAcknowledged}
						/>
					)}
				</Stack>
			</DialogShell>
		</Container>
	);
}
