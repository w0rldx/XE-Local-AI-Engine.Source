import { Alert, Button, Group, Loader, Stack, Text } from "@mantine/core";
import { IconAlertTriangle, IconTerminal2 } from "@tabler/icons-react";
import { useCallback, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { useUnsavedChangesGuard } from "@/core/ui/hooks/useUnsavedChangesGuard";
import { toast } from "@/core/ui/notifications/Toast";
import { CommandForm, type CommandFormHandle } from "@/features/commands/components/CommandForm";
import { CommandList } from "@/features/commands/components/CommandList";
import { toSaveCommandRequest } from "@/features/commands/models/CommandMappers";
import { CUSTOM_COMMAND_CAPACITY, type CommandFormValues, type SlashCommand } from "@/features/commands/models/CommandModels";
import { useCommands, useCreateCommand, useDeleteCommand, useUpdateCommand } from "@/features/commands/queries/useCommands";

type CommandEditorTarget = { mode: "create" } | { mode: "edit"; id: string } | null;

const emptyFormValues: CommandFormValues = { name: "", description: "", actionType: "SendPrompt", prompt: "" };

function toFormValues(command: SlashCommand): CommandFormValues {
	return { name: command.name, description: command.description ?? "", actionType: "SendPrompt", prompt: command.action.prompt };
}

export function CommandsPage() {
	const { t } = useTranslation();
	const { confirm } = useConfirm();
	const [editorTarget, setEditorTarget] = useState<CommandEditorTarget>(null);
	const [isDirty, setIsDirty] = useState(false);
	const formRef = useRef<CommandFormHandle>(null);
	useUnsavedChangesGuard({ isDirty });

	const commandsQuery = useCommands();
	const createMutation = useCreateCommand();
	const updateMutation = useUpdateCommand();
	const deleteMutation = useDeleteCommand();
	const commands = useMemo(() => commandsQuery.data ?? [], [commandsQuery.data]);
	const customCount = commands.filter((command) => command.source === "custom").length;
	const isAtCapacity = customCount >= CUSTOM_COMMAND_CAPACITY;
	const editingCommand = editorTarget?.mode === "edit" ? commands.find((command) => command.id === editorTarget.id) : undefined;
	const isSaving = createMutation.isPending || updateMutation.isPending;
	const isMutating = isSaving || deleteMutation.isPending;
	const submitError =
		createMutation.error || updateMutation.error
			? apiErrorMessage(createMutation.error ?? updateMutation.error, t("pages.commands.errors.save"))
			: undefined;

	const closeAndResetEditor = useCallback(() => {
		setIsDirty(false);
		setEditorTarget(null);
	}, []);

	const requestCloseEditor = useCallback(async () => {
		if (isDirty) {
			const confirmed = await confirm({
				title: t("components.dialogShell.unsavedTitle"),
				description: t("components.dialogShell.unsavedDescription"),
				confirmationText: t("common.discard"),
				cancellationText: t("common.keepEditing"),
			});
			if (!confirmed) {
				return;
			}
		}
		closeAndResetEditor();
	}, [closeAndResetEditor, confirm, isDirty, t]);

	const handleSubmit = useCallback(
		(values: CommandFormValues) => {
			const body = toSaveCommandRequest(values);
			if (editorTarget?.mode === "edit") {
				updateMutation.mutate({ path: { commandId: editorTarget.id }, body }, { onSuccess: closeAndResetEditor });
				return;
			}
			createMutation.mutate({ body }, { onSuccess: closeAndResetEditor });
		},
		[closeAndResetEditor, createMutation, editorTarget, updateMutation],
	);

	const handleDelete = useCallback(
		async (command: SlashCommand) => {
			if (command.source !== "custom" || !command.id) {
				return;
			}
			const confirmed = await confirm({
				title: t("pages.commands.delete.title"),
				description: t("pages.commands.delete.description", { name: command.name }),
				confirmationText: t("common.delete"),
				cancellationText: t("common.cancel"),
			});
			if (confirmed) {
				deleteMutation.mutate(
					{ path: { commandId: command.id } },
					{ onError: (error) => toast.error(apiErrorMessage(error, t("pages.commands.errors.delete"))) },
				);
			}
		},
		[confirm, deleteMutation, t],
	);

	return (
		<PageShell>
			<PageHeader
				title={t("pages.commands.title")}
				icon={<IconTerminal2 size={24} />}
				subtitle={t("pages.commands.subtitle")}
				actions={
					<Button
						onClick={() => setEditorTarget({ mode: "create" })}
						disabled={isAtCapacity || commandsQuery.isLoading}
						data-testid="command-create-button"
					>
						{t("pages.commands.createButton")}
					</Button>
				}
			/>
			{isAtCapacity ? <Alert color="yellow">{t("pages.commands.capacity")}</Alert> : null}
			<SectionCard>
				{commandsQuery.isLoading ? (
					<Group gap="sm">
						<Loader size="sm" />
						<Text c="dimmed">{t("pages.commands.list.loading")}</Text>
					</Group>
				) : null}
				{commandsQuery.error ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />}>
						<Stack gap="sm" align="flex-start">
							<Text size="sm">{apiErrorMessage(commandsQuery.error, t("pages.commands.errors.load"))}</Text>
							<Button size="xs" variant="light" onClick={() => commandsQuery.refetch()}>
								{t("common.retry", "Retry")}
							</Button>
						</Stack>
					</Alert>
				) : null}
				{!commandsQuery.isLoading && !commandsQuery.error ? (
					<CommandList
						commands={commands}
						isMutating={isMutating}
						onEdit={(id) => setEditorTarget({ mode: "edit", id })}
						onDelete={handleDelete}
					/>
				) : null}
			</SectionCard>

			<DialogShell
				opened={editorTarget !== null}
				onClose={requestCloseEditor}
				title={editorTarget?.mode === "edit" ? t("pages.commands.editor.editTitle") : t("pages.commands.editor.createTitle")}
				zIndex={300}
				closeOnClickOutside={!isDirty}
				closeOnEscape={!isDirty}
				footer={
					<>
						<Button variant="subtle" onClick={requestCloseEditor} disabled={isSaving}>
							{t("common.cancel")}
						</Button>
						<Button onClick={() => formRef.current?.submit()} loading={isSaving}>
							{t("common.save")}
						</Button>
					</>
				}
			>
				<Stack px="md" pb="md">
					<CommandForm
						key={editingCommand?.id ?? "create"}
						ref={formRef}
						initialValues={editingCommand ? toFormValues(editingCommand) : emptyFormValues}
						isSubmitting={isSaving}
						submitError={submitError}
						onSubmit={handleSubmit}
						onDirtyChange={setIsDirty}
					/>
				</Stack>
			</DialogShell>
		</PageShell>
	);
}
