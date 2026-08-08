import { Alert, Badge, Button, Card, Group, Loader, Stack, Table, Text, TextInput, Title } from "@mantine/core";
import { IconAlertTriangle, IconFolder, IconRefresh, IconTrash } from "@tabler/icons-react";
import { type FormEvent, useCallback, useState } from "react";
import { flushSync } from "react-dom";
import { useTranslation } from "react-i18next";

import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { toast } from "@/core/ui/notifications/Toast";
import type { McpWorkspace } from "@/features/mcp/models/McpWorkspaceModels";
import { useCreateMcpWorkspace, useDeleteMcpWorkspace, useMcpWorkspaces } from "@/features/mcp/queries/useMcpWorkspaces";

export function McpWorkspaceAllowlistPanel() {
	const { t } = useTranslation();
	const { confirm } = useConfirm();
	const workspacesQuery = useMcpWorkspaces();
	const createWorkspace = useCreateMcpWorkspace();
	const deleteWorkspace = useDeleteMcpWorkspace();
	const [alias, setAlias] = useState("");
	const [hostPath, setHostPath] = useState("");

	const isPending = createWorkspace.isPending || deleteWorkspace.isPending;
	const canSubmit = alias.trim().length > 0 && hostPath.trim().length > 0 && !isPending;

	const handleSubmit = useCallback(
		(event: FormEvent<HTMLFormElement>): void => {
			event.preventDefault();
			const body = { alias: alias.trim(), hostPath: hostPath.trim() };

			// The trusted host path is secret-adjacent operator input. Erase it from rendered component state before the
			// request starts, including the failure path. flushSync makes that ordering observable even to a synchronous
			// mutation adapter and prevents the plaintext from lingering while a slow request is in flight.
			flushSync(() => setHostPath(""));
			createWorkspace.mutate(
				{ body },
				{
					onSuccess: () => {
						setAlias("");
						toast.success(t("pages.nodeSettings.mcpWorkspaces.added", "Workspace access added."));
					},
					onError: () =>
						toast.error(
							t("pages.nodeSettings.mcpWorkspaces.addError", "Could not add workspace access. Check the values and try again."),
						),
				},
			);
		},
		[alias, createWorkspace, hostPath, t],
	);

	const handleRevoke = useCallback(
		async (workspace: McpWorkspace): Promise<void> => {
			const confirmed = await confirm({
				title: t("pages.nodeSettings.mcpWorkspaces.revokeTitle", "Revoke workspace access"),
				description: t(
					"pages.nodeSettings.mcpWorkspaces.revokeDescription",
					"Revoke read-only access to '{{alias}}'? New delegated work will no longer be able to use it.",
					{ alias: workspace.alias },
				),
				confirmationText: t("pages.nodeSettings.mcpWorkspaces.revoke", "Revoke"),
				cancellationText: t("common.cancel", "Cancel"),
			});

			if (!confirmed) {
				return;
			}

			deleteWorkspace.mutate(
				{ path: { workspaceId: workspace.id } },
				{
					onSuccess: () => toast.success(t("pages.nodeSettings.mcpWorkspaces.revoked", "Workspace access revoked.")),
					onError: () => toast.error(t("pages.nodeSettings.mcpWorkspaces.revokeError", "Could not revoke workspace access.")),
				},
			);
		},
		[confirm, deleteWorkspace, t],
	);

	return (
		<Card withBorder={true} radius="md" p="lg" data-testid="mcp-workspaces-card">
			<Stack gap="md">
				<Group justify="space-between" align="center">
					<Group gap="xs" align="center">
						<IconFolder size={20} />
						<Title order={4}>{t("pages.nodeSettings.mcpWorkspaces.title", "MCP workspace access")}</Title>
					</Group>
					<Badge variant="light" color="blue">
						{t("pages.nodeSettings.mcpWorkspaces.readOnly", "Read only")}
					</Badge>
				</Group>

				<Text size="sm" c="dimmed">
					{t(
						"pages.nodeSettings.mcpWorkspaces.description",
						"Allow external MCP clients to delegate code-reading tasks in selected folders. Paths remain on this node and are never returned to clients.",
					)}
				</Text>

				<form onSubmit={handleSubmit} aria-label={t("pages.nodeSettings.mcpWorkspaces.formLabel", "Add workspace access")}>
					<Stack gap="sm">
						<TextInput
							label={t("pages.nodeSettings.mcpWorkspaces.aliasLabel", "Alias")}
							description={t(
								"pages.nodeSettings.mcpWorkspaces.aliasDescription",
								"A non-sensitive name shown to MCP clients and in this list.",
							)}
							placeholder={t("pages.nodeSettings.mcpWorkspaces.aliasPlaceholder", "Source repository")}
							value={alias}
							onChange={(event) => setAlias(event.currentTarget.value)}
							required={true}
							disabled={isPending}
							data-testid="mcp-workspace-alias"
						/>
						<TextInput
							label={t("pages.nodeSettings.mcpWorkspaces.pathLabel", "Trusted host path")}
							description={t(
								"pages.nodeSettings.mcpWorkspaces.pathDescription",
								"Stored securely on this node. It is cleared from this form as soon as you submit it.",
							)}
							placeholder={t("pages.nodeSettings.mcpWorkspaces.pathPlaceholder", "Enter an absolute folder path")}
							type="password"
							autoComplete="off"
							spellCheck={false}
							value={hostPath}
							onChange={(event) => setHostPath(event.currentTarget.value)}
							required={true}
							disabled={isPending}
							data-testid="mcp-workspace-path"
						/>
						<Group justify="flex-end">
							<Button type="submit" loading={createWorkspace.isPending} disabled={!canSubmit}>
								{t("pages.nodeSettings.mcpWorkspaces.add", "Add read-only workspace")}
							</Button>
						</Group>
					</Stack>
				</form>

				{workspacesQuery.isLoading ? (
					<Group gap="sm" role="status" aria-live="polite">
						<Loader size="sm" />
						<Text c="dimmed">{t("pages.nodeSettings.mcpWorkspaces.loading", "Loading workspace access…")}</Text>
					</Group>
				) : null}

				{workspacesQuery.error ? (
					<Alert
						color="red"
						icon={<IconAlertTriangle size={16} />}
						title={t("pages.nodeSettings.mcpWorkspaces.loadError", "Could not load workspace access.")}
						role="alert"
					>
						<Button
							variant="subtle"
							size="xs"
							leftSection={<IconRefresh size={14} />}
							onClick={() => workspacesQuery.refetch()}
							loading={workspacesQuery.isFetching}
						>
							{t("common.retry", "Retry")}
						</Button>
					</Alert>
				) : null}

				{!workspacesQuery.isLoading && !workspacesQuery.error && (workspacesQuery.data?.length ?? 0) === 0 ? (
					<Text c="dimmed" role="status">
						{t("pages.nodeSettings.mcpWorkspaces.empty", "No folders are available to delegated MCP agents.")}
					</Text>
				) : null}

				{!workspacesQuery.error && (workspacesQuery.data?.length ?? 0) > 0 ? (
					<Table.ScrollContainer minWidth={560}>
						<Table striped={true} highlightOnHover={true} verticalSpacing="sm" data-testid="mcp-workspaces-table">
							<Table.Thead>
								<Table.Tr>
									<Table.Th>{t("pages.nodeSettings.mcpWorkspaces.aliasColumn", "Alias")}</Table.Th>
									<Table.Th>{t("pages.nodeSettings.mcpWorkspaces.idColumn", "Workspace ID")}</Table.Th>
									<Table.Th>{t("pages.nodeSettings.mcpWorkspaces.accessColumn", "Access")}</Table.Th>
									<Table.Th>{t("pages.nodeSettings.mcpWorkspaces.actionsColumn", "Actions")}</Table.Th>
								</Table.Tr>
							</Table.Thead>
							<Table.Tbody>
								{workspacesQuery.data?.map((workspace) => (
									<Table.Tr key={workspace.id}>
										<Table.Td>{workspace.alias}</Table.Td>
										<Table.Td>{workspace.id}</Table.Td>
										<Table.Td>
											<Badge variant="light" color="blue">
												{t("pages.nodeSettings.mcpWorkspaces.readOnly", "Read only")}
											</Badge>
										</Table.Td>
										<Table.Td>
											<Button
												variant="subtle"
												color="red"
												size="xs"
												leftSection={<IconTrash size={14} />}
												onClick={() => handleRevoke(workspace)}
												disabled={isPending}
												aria-label={t("pages.nodeSettings.mcpWorkspaces.revokeAria", "Revoke access to {{alias}}", {
													alias: workspace.alias,
												})}
											>
												{t("pages.nodeSettings.mcpWorkspaces.revoke", "Revoke")}
											</Button>
										</Table.Td>
									</Table.Tr>
								))}
							</Table.Tbody>
						</Table>
					</Table.ScrollContainer>
				) : null}

				{isPending ? (
					<Text size="sm" c="dimmed" role="status" aria-live="polite">
						{t("pages.nodeSettings.mcpWorkspaces.pending", "Updating workspace access…")}
					</Text>
				) : null}
			</Stack>
		</Card>
	);
}
