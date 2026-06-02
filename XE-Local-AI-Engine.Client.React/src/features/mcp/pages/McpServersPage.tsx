import { Alert, Button, Card, Container, Group, Loader, Stack, Text, Title } from "@mantine/core";
import { IconAlertTriangle, IconPlug, IconPlus } from "@tabler/icons-react";
import { useCallback, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { McpServerForm } from "@/features/mcp/components/McpServerForm";
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

function errorMessage(error: unknown, fallback: string): string {
	return error instanceof Error ? error.message : fallback;
}

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

	const serversQuery = useMcpServers();
	const createMutation = useCreateMcpServer();
	const updateMutation = useUpdateMcpServer();
	const deleteMutation = useDeleteMcpServer();
	const enableMutation = useSetMcpServerEnabled();

	const servers = serversQuery.data ?? [];

	const editingServer = useMemo(() => {
		if (editorTarget?.mode !== "edit") {
			return undefined;
		}
		return servers.find((server) => server.id === editorTarget.id);
	}, [servers, editorTarget]);

	const isMutating =
		createMutation.isPending ||
		updateMutation.isPending ||
		deleteMutation.isPending ||
		enableMutation.isPending;

	const submitError =
		createMutation.error || updateMutation.error
			? errorMessage(
					createMutation.error ?? updateMutation.error,
					t("pages.mcp.errors.save", "Could not save the MCP server."),
				)
			: undefined;

	const handleSubmit = useCallback(
		(values: McpServerFormValues) => {
			const body = toSaveMcpServerRequest(values);

			if (editorTarget?.mode === "edit") {
				updateMutation.mutate(
					{ path: { mcpServerId: editorTarget.id }, body },
					{ onSuccess: () => closeEditor() },
				);
				return;
			}

			createMutation.mutate({ body }, { onSuccess: () => closeEditor() });
		},
		[closeEditor, createMutation, editorTarget, updateMutation],
	);

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
				deleteMutation.mutate({ path: { mcpServerId: server.id } });
			}
		},
		[confirm, deleteMutation, expandedToolsId, t],
	);

	const handleToggleEnabled = useCallback(
		(server: McpServerRegistration, enabled: boolean) => {
			enableMutation.mutate({ id: server.id, enabled });
		},
		[enableMutation],
	);

	const isEditorOpen = editorTarget !== null;
	const formInitialValues = editingServer ? toFormValues(editingServer) : emptyFormValues;

	return (
		<Container fluid={true} py="lg">
			<Stack gap="lg">
				<Group justify="space-between" align="flex-start">
					<Stack gap={4}>
						<Text size="sm" tt="uppercase" fw={700} c="dimmed">
							{t("pages.mcp.eyebrow", "Worker Node")}
						</Text>
						<Group gap="xs" align="center">
							<IconPlug size={24} />
							<Title order={2}>{t("pages.mcp.title", "MCP servers")}</Title>
						</Group>
						<Text c="dimmed">
							{t(
								"pages.mcp.subtitle",
								"Register local MCP servers to extend the node tool catalog. Servers are disabled until you enable them, and every discovered tool requires approval by default.",
							)}
						</Text>
					</Stack>
					{!isEditorOpen ? (
						<Button leftSection={<IconPlus size={16} />} onClick={openCreate} data-testid="mcp-create-button">
							{t("pages.mcp.createButton", "Register server")}
						</Button>
					) : null}
				</Group>

				{deleteMutation.error ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="mcp-delete-error">
						{errorMessage(deleteMutation.error, t("pages.mcp.errors.delete", "Could not delete the MCP server."))}
					</Alert>
				) : null}

				{enableMutation.error ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="mcp-enable-error">
						{errorMessage(enableMutation.error, t("pages.mcp.errors.enable", "Could not change the server state."))}
					</Alert>
				) : null}

				{isEditorOpen ? (
					<Card withBorder={true} radius="md" p="lg" data-testid="mcp-editor-card">
						<Stack gap="md">
							<Title order={3}>
								{editorTarget?.mode === "edit"
									? t("pages.mcp.editor.editTitle", "Edit MCP server")
									: t("pages.mcp.editor.createTitle", "Register MCP server")}
							</Title>
							<McpServerForm
								key={editorTarget?.mode === "edit" ? editorTarget.id : "create"}
								initialValues={formInitialValues}
								isSubmitting={createMutation.isPending || updateMutation.isPending}
								submitError={submitError}
								onSubmit={handleSubmit}
								onCancel={closeEditor}
							/>
						</Stack>
					</Card>
				) : (
					<Card withBorder={true} radius="md" p="lg">
						<Stack gap="md">
							{serversQuery.isLoading ? (
								<Group gap="sm">
									<Loader size="sm" />
									<Text c="dimmed">{t("pages.mcp.list.loading", "Loading MCP servers…")}</Text>
								</Group>
							) : null}
							{serversQuery.error ? (
								<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="mcp-list-error">
									{errorMessage(serversQuery.error, t("pages.mcp.errors.load", "Could not load MCP servers."))}
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
										<McpServerToolsSelector
											servers={servers}
											expandedToolsId={expandedToolsId}
											onSelect={setExpandedToolsId}
										/>
									) : null}
								</>
							) : null}
						</Stack>
					</Card>
				)}
			</Stack>
		</Container>
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
