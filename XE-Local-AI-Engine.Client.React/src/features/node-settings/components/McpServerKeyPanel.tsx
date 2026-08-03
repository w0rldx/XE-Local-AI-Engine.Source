import { Alert, Badge, Button, Card, Code, CopyButton, Group, Stack, Text, Title } from "@mantine/core";
import { IconCheck, IconCopy, IconPlugConnected, IconRefresh, IconTrash } from "@tabler/icons-react";
import {
	useGenerateMcpServerApiKey,
	useMcpServerApiKey,
	useRevokeMcpServerApiKey,
} from "@/features/mcp/queries/useMcpServerApiKey";
import { useTranslation } from "react-i18next";

// Manages the INBOUND MCP credential: the bearer key an external MCP client (Claude Code, Claude Desktop, an IDE)
// presents to this node's own MCP server endpoint, so it can delegate a task to the local model.
//
// The key IS shown in full, deliberately. It is stored encrypted at rest and this page is loopback-only and
// Operator-gated, and an operator must be able to re-copy it into a client config without invalidating clients that
// already hold it. That is why this is a plain Code block and not a PasswordInput — unlike the HF token panel beside
// it, which is write-only because that token is never needed again once saved.
export function McpServerKeyPanel() {
	const { t } = useTranslation();
	const { data, isLoading } = useMcpServerApiKey();
	const generate = useGenerateMcpServerApiKey();
	const revoke = useRevokeMcpServerApiKey();

	const isConfigured = data?.configured === true;
	const isBusy = generate.isPending || revoke.isPending;

	return (
		<Card withBorder={true} radius="md" p="lg" data-testid="mcp-server-key-card">
			<Stack gap="md">
				<Group justify="space-between" align="center">
					<Group gap="xs" align="center">
						<IconPlugConnected size={20} />
						<Title order={4}>{t("pages.nodeSettings.mcpServerKey.title", "MCP server")}</Title>
					</Group>
					{!isLoading ? (
						<Badge
							color={isConfigured ? "green" : "gray"}
							variant="light"
							data-testid="mcp-server-key-status"
						>
							{isConfigured
								? t("pages.nodeSettings.mcpServerKey.configured", "Key configured")
								: t("pages.nodeSettings.mcpServerKey.none", "No key")}
						</Badge>
					) : null}
				</Group>

				<Text size="sm" c="dimmed">
					{t(
						"pages.nodeSettings.mcpServerKey.description",
						"Let an external MCP client such as Claude Code connect to this node and hand tasks to your local model. The key is stored encrypted and can be copied again at any time. Only clients on this machine can connect.",
					)}
				</Text>

				{isConfigured && data ? (
					<Stack gap="xs">
						<Text size="sm" fw={500}>
							{t("pages.nodeSettings.mcpServerKey.endpointLabel", "Endpoint URL")}
						</Text>
						<Group gap="xs" wrap="nowrap">
							<Code data-testid="mcp-server-key-endpoint" style={{ flex: 1, overflowX: "auto" }}>
								{data.endpointUrl}
							</Code>
							<CopyButton value={data.endpointUrl}>
								{({ copied, copy }) => (
									<Button
										variant="default"
										size="xs"
										leftSection={copied ? <IconCheck size={14} /> : <IconCopy size={14} />}
										onClick={copy}
										data-testid="mcp-server-key-copy-endpoint"
									>
										{copied
											? t("common.copied", "Copied")
											: t("common.copy", "Copy")}
									</Button>
								)}
							</CopyButton>
						</Group>

						<Text size="sm" fw={500} mt="xs">
							{t("pages.nodeSettings.mcpServerKey.keyLabel", "API key")}
						</Text>
						<Group gap="xs" wrap="nowrap">
							<Code data-testid="mcp-server-key-value" style={{ flex: 1, overflowX: "auto" }}>
								{data.key}
							</Code>
							<CopyButton value={data.key ?? ""}>
								{({ copied, copy }) => (
									<Button
										variant="default"
										size="xs"
										leftSection={copied ? <IconCheck size={14} /> : <IconCopy size={14} />}
										onClick={copy}
										data-testid="mcp-server-key-copy-key"
									>
										{copied
											? t("common.copied", "Copied")
											: t("common.copy", "Copy")}
									</Button>
								)}
							</CopyButton>
						</Group>

						<Text size="xs" c="dimmed" data-testid="mcp-server-key-last-used">
							{data.lastUsedAt
								? t("pages.nodeSettings.mcpServerKey.lastUsed", "Last used {{when}}", {
										when: new Date(data.lastUsedAt).toLocaleString(),
									})
								: t(
										"pages.nodeSettings.mcpServerKey.neverUsed",
										"Never used yet — no client has connected with this key.",
									)}
						</Text>
					</Stack>
				) : null}

				{isConfigured ? (
					<Alert color="blue" variant="light" data-testid="mcp-server-key-hint">
						{t(
							"pages.nodeSettings.mcpServerKey.clientHint",
							'Add it to Claude Code with: claude mcp add --transport http xe-engine "<endpoint>" --header "Authorization: Bearer <key>". Set a per-server timeout of about 1800000 ms, or a long local-model run will be cut off by the client.',
						)}
					</Alert>
				) : null}

				<Group gap="sm">
					<Button
						loading={generate.isPending}
						disabled={isBusy}
						leftSection={isConfigured ? <IconRefresh size={16} /> : undefined}
						onClick={() => generate.mutate({})}
						data-testid="mcp-server-key-generate"
					>
						{isConfigured
							? t("pages.nodeSettings.mcpServerKey.regenerate", "Regenerate key")
							: t("pages.nodeSettings.mcpServerKey.generate", "Generate key")}
					</Button>
					<Button
						variant="default"
						color="red"
						leftSection={<IconTrash size={16} />}
						loading={revoke.isPending}
						disabled={!isConfigured || isBusy}
						onClick={() => revoke.mutate({})}
						data-testid="mcp-server-key-revoke"
					>
						{t("pages.nodeSettings.mcpServerKey.revoke", "Revoke key")}
					</Button>
				</Group>

				{isConfigured ? (
					<Text size="xs" c="dimmed">
						{t(
							"pages.nodeSettings.mcpServerKey.regenerateWarning",
							"Regenerating replaces the key immediately — any client still configured with the old one stops working.",
						)}
					</Text>
				) : null}
			</Stack>
		</Card>
	);
}
