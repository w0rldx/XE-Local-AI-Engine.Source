import { Alert, Button, Group, Loader, Stack, Text } from "@mantine/core";
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
import { McpServerForm, type McpServerFormHandle } from "@/features/mcp/components/McpServerForm";
import { McpServerList } from "@/features/mcp/components/McpServerList";
import { McpServerToolsPanel } from "@/features/mcp/components/McpServerToolsPanel";
import { toSaveMcpServerRequest } from "@/features/mcp/models/McpServerMappers";
import type { McpServerFormValues, McpServerRegistration } from "@/features/mcp/models/McpServerModels";
import {
	useCreateMcpServer,
	useDeleteMcpServer,
	useMcpServers,
	useSetMcpServerEnabled,
	useUpdateMcpServer,
} from "@/features/mcp/queries/useMcpServers";
import { useMcpManagementStore } from "@/features/mcp/stores/McpManagementStore";

const emptyFormValues: McpServerFormValues = {
	name: "",
	description: "",
	transportKind: "Stdio",
	command: "",
	arguments: [],
	workingDirectory: "",
	env: [],
	url: "",
};

function toFormValues(server: McpServerRegistration): McpServerFormValues {
	return {
		name: server.name,
		description: server.description,
		transportKind: server.transportKind,
		command: server.command ?? "",
		arguments: [...server.arguments],
		workingDirectory: server.workingDirectory ?? "",
		env: server.env.map((entry) => ({ ...entry })),
		url: server.url ?? "",
	};
}

export function McpServersPage() {
	const { t } = useTranslation();
	const { confirm } = useConfirm();

	const editorTarget = useMcpManagementStore((state) => state.editorTarget);
	const openCreate = useMcpManagementStore((state) => state.actions.openCreate);
	const openEdit = useMcpManagementStore((state) => state.actions.openEdit);
	const closeEditor = useMcpManagementStore((state) => state.actions.closeEditor);

	// The server whose discovered-tools panel is expanded (independent of the editor). Null = collapsed.
	const [expandedToolsId, setExpandedToolsId] = useState<string | null>(null);

	// Whether the editor form has unsaved edits. Reported up by the form; drives the dialog close-guard and the
	// route/tab-close guard. Reset whenever the editor closes so a stale dirty flag never lingers.
	const [isDirty, setIsDirty] = useState(false);
	const formRef = useRef<McpServerFormHandle>(null);

	// Block in-app navigation + tab close while the open editor has unsaved edits.
	useUnsavedChangesGuard({ isDirty });

	// Reset the transient editor target when the page unmounts so navigating away and back does not reopen the
	// editor (the "stuck editor" bug — the Zustand store is a module singleton that outlives the route).
	useEffect(() => closeEditor, [closeEditor]);

	const serversQuery = useMcpServers();
	const createMutation = useCreateMcpServer();
	const updateMutation = useUpdateMcpServer();
	const deleteMutation = useDeleteMcpServer();
	const enableMutation = useSetMcpServerEnabled();

	const servers = useMemo(() => serversQuery.data ?? [], [serversQuery.data]);

	const editingServer = useMemo(() => {
		if (editorTarget?.mode !== "edit") {
			return undefined;
		}
		return servers.find((server) => server.id === editorTarget.id);
	}, [servers, editorTarget]);

	const isMutating = createMutation.isPending || updateMutation.isPending || deleteMutation.isPending || enableMutation.isPending;

	// Editor save in flight (create or update); drives the footer Save loading state and disables Cancel.
	const isSaving = createMutation.isPending || updateMutation.isPending;

	const submitError =
		createMutation.error || updateMutation.error
			? apiErrorMessage(createMutation.error ?? updateMutation.error, t("pages.mcp.errors.save", "Could not save the MCP server."))
			: undefined;

	// Closes the editor and clears the dirty flag. A successful save closes through here so the next open starts
	// clean; the form is re-keyed per target so its internal state is rebuilt from initialValues.
	const closeAndResetEditor = useCallback(() => {
		setIsDirty(false);
		closeEditor();
	}, [closeEditor]);

	const handleSubmit = useCallback(
		(values: McpServerFormValues) => {
			const body = toSaveMcpServerRequest(values);

			if (editorTarget?.mode === "edit") {
				updateMutation.mutate({ path: { mcpServerId: editorTarget.id }, body }, { onSuccess: () => closeAndResetEditor() });
				return;
			}

			createMutation.mutate({ body }, { onSuccess: () => closeAndResetEditor() });
		},
		[closeAndResetEditor, createMutation, editorTarget, updateMutation],
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
		async (server: McpServerRegistration) => {
			const confirmed = await confirm({
				title: t("pages.mcp.delete.title", "Delete MCP server"),
				description: t("pages.mcp.delete.description", "Delete '{{name}}'? This cannot be undone.", {
					name: server.name,
				}),
				confirmationText: t("common.delete", "Delete"),
				cancellationText: t("common.cancel", "Cancel"),
			});

			if (confirmed) {
				if (expandedToolsId === server.id) {
					setExpandedToolsId(null);
				}
				deleteMutation.mutate(
					{ path: { mcpServerId: server.id } },
					{ onError: (error) => toast.error(apiErrorMessage(error, t("pages.mcp.errors.delete", "Could not delete the MCP server."))) },
				);
			}
		},
		[confirm, deleteMutation, expandedToolsId, t],
	);

	const handleToggleEnabled = useCallback(
		(server: McpServerRegistration, enabled: boolean) => {
			enableMutation.mutate(
				{ id: server.id, enabled },
				{ onError: (error) => toast.error(apiErrorMessage(error, t("pages.mcp.errors.enable", "Could not change the server state."))) },
			);
		},
		[enableMutation, t],
	);

	const isEditorOpen = editorTarget !== null;
	const formInitialValues = editingServer ? toFormValues(editingServer) : emptyFormValues;

	return (
		<PageShell>
			<PageHeader
				icon={<IconPlug size={24} />}
				title={t("pages.mcp.title", "MCP servers")}
				subtitle={t(
					"pages.mcp.subtitle",
					"Register local MCP servers to extend the node tool catalog. Servers are disabled until you enable them, and every discovered tool requires approval by default.",
				)}
				actions={
					<Button leftSection={<IconPlus size={16} />} onClick={openCreate} data-testid="mcp-create-button">
						{t("pages.mcp.createButton", "Register server")}
					</Button>
				}
			/>

			<SectionCard title={t("pages.mcp.list.title", "Registered servers")}>
				{serversQuery.isLoading ? (
					<Group gap="sm">
						<Loader size="sm" />
						<Text c="dimmed">{t("pages.mcp.list.loading", "Loading MCP servers…")}</Text>
					</Group>
				) : null}
				{serversQuery.error ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="mcp-list-error">
						{apiErrorMessage(serversQuery.error, t("pages.mcp.errors.load", "Could not load MCP servers."))}
					</Alert>
				) : null}
				{!serversQuery.isLoading && !serversQuery.error ? (
					<>
						<McpServerList
							servers={servers}
							isMutating={isMutating}
							onEdit={openEdit}
							onDelete={handleDelete}
							onToggleEnabled={handleToggleEnabled}
						/>
						{servers.length > 0 ? (
							<McpServerToolsSelector servers={servers} expandedToolsId={expandedToolsId} onSelect={setExpandedToolsId} />
						) : null}
					</>
				) : null}
			</SectionCard>

			<DialogShell
				opened={isEditorOpen}
				onClose={requestCloseEditor}
				title={
					editorTarget?.mode === "edit"
						? t("pages.mcp.editor.editTitle", "Edit MCP server")
						: t("pages.mcp.editor.createTitle", "Register MCP server")
				}
				// Explicit stacking contract: the editor sits below ConfirmProvider's zIndex 400 so the
				// unsaved-changes discard prompt always renders on top of it.
				zIndex={300}
				// Dirty edits must not be lost to an accidental overlay/escape dismiss; the only way out is the
				// guarded close (title-bar X / footer Cancel), which confirms first.
				closeOnClickOutside={!isDirty}
				closeOnEscape={!isDirty}
				footer={
					<>
						<Button
							variant="subtle"
							leftSection={<IconX size={16} />}
							onClick={requestCloseEditor}
							disabled={isSaving}
							data-testid="mcp-form-cancel"
						>
							{t("common.cancel", "Cancel")}
						</Button>
						<Button
							leftSection={<IconDeviceFloppy size={16} />}
							onClick={() => formRef.current?.submit()}
							loading={isSaving}
							data-testid="mcp-form-submit"
						>
							{t("common.save", "Save")}
						</Button>
					</>
				}
			>
				<Stack gap="md" px="md" pb="md" data-testid="mcp-editor-card">
					<McpServerForm
						key={editorTarget?.mode === "edit" ? editorTarget.id : "create"}
						ref={formRef}
						initialValues={formInitialValues}
						isSubmitting={isSaving}
						submitError={submitError}
						onSubmit={handleSubmit}
						onCancel={requestCloseEditor}
						onDirtyChange={setIsDirty}
						hideActions={true}
					/>
				</Stack>
			</DialogShell>
		</PageShell>
	);
}

interface McpServerToolsSelectorProps {
	servers: readonly McpServerRegistration[];
	expandedToolsId: string | null;
	onSelect: (id: string | null) => void;
}

// Lets the user pick a registered server to inspect its live discovered tools + connection status. Kept inline
// on the page (not the table) so the on-demand GetServerTools fetch only fires for the explicitly chosen server.
function McpServerToolsSelector({ servers, expandedToolsId, onSelect }: McpServerToolsSelectorProps) {
	const { t } = useTranslation();

	return (
		<Stack gap="xs" data-testid="mcp-tools-inspector">
			<Text size="sm" fw={600}>
				{t("pages.mcp.tools.inspectLabel", "Inspect discovered tools")}
			</Text>
			<Group gap="xs">
				{servers.map((server) => (
					<Button
						key={server.id}
						size="xs"
						variant={expandedToolsId === server.id ? "filled" : "light"}
						onClick={() => onSelect(expandedToolsId === server.id ? null : server.id)}
						data-testid={`mcp-tools-select-${server.id}`}
					>
						{server.name}
					</Button>
				))}
			</Group>
			<McpServerToolsPanel serverId={expandedToolsId} />
		</Stack>
	);
}
