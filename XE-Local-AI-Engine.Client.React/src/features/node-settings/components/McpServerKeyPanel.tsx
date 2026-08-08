import { Alert, Badge, Button, Card, Code, CopyButton, Group, Stack, Text, Title } from "@mantine/core";
import {
	IconAlertTriangle,
	IconCheck,
	IconCopy,
	IconPlugConnected,
	IconRefresh,
	IconTrash,
} from "@tabler/icons-react";
import {
	useGenerateMcpServerApiKey,
	useMcpServerApiKey,
	useRevokeMcpServerApiKey,
} from "@/features/mcp/queries/useMcpServerApiKey";
import { useState } from "react";
import { useTranslation } from "react-i18next";

// Manages the INBOUND MCP credential: the bearer key an external MCP client (Claude Code, Claude Desktop, an IDE)
// presents to this node's own MCP server endpoint, so it can delegate a task to the local model.
//
// The key is shown EXACTLY ONCE, in the response to a generate action, and is unrecoverable afterwards: the node
// persists only a SHA-256 digest, so there is nothing for the GET to return and no way to recover a key the operator
// failed to copy. `revealedKey` therefore lives in component state rather than in the query cache — a refetch,
// remount or navigation drops it, which is the honest lifetime for a value the server can no longer supply. Losing it
// means regenerating and reconfiguring every client, so the reveal carries an explicit copy-it-now warning.
export function McpServerKeyPanel() {
	const { t } = useTranslation();
	const { data, isLoading } = useMcpServerApiKey();
	const revoke = useRevokeMcpServerApiKey();

	// Held here and nowhere else. Cleared on revoke so a revoked key never lingers on screen as if it still worked.
	const [revealedKey, setRevealedKey] = useState<string | null>(null);

	const generate = useGenerateMcpServerApiKey();

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
						"Let an external MCP client such as Claude Code connect to this node and hand tasks to your local model. Only a one-way hash of the key is stored, so it is shown once when you generate it and cannot be retrieved afterwards. Only clients on this machine can connect.",
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
							{t("pages.nodeSettings.mcpServerKey.prefixLabel", "Key")}
						</Text>
						<Code data-testid="mcp-server-key-prefix" style={{ overflowX: "auto" }}>
							{`${data.prefix ?? ""}…`}
						</Code>
						<Text size="xs" c="dimmed" data-testid="mcp-server-key-not-retrievable">
							{t(
								"pages.nodeSettings.mcpServerKey.notRetrievable",
								"Only the first characters are shown. The full key is not stored and cannot be shown again — regenerate if you no longer have it.",
							)}
						</Text>

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

				{revealedKey !== null ? (
					<Alert
						color="yellow"
						variant="light"
						icon={<IconAlertTriangle size={18} />}
						title={t("pages.nodeSettings.mcpServerKey.revealTitle", "Copy this key now")}
						data-testid="mcp-server-key-reveal"
					>
						<Stack gap="xs">
							<Text size="sm">
								{t(
									"pages.nodeSettings.mcpServerKey.revealWarning",
									"This is the only time this key will be shown. It is not stored and cannot be recovered — if you lose it you must generate a new one and update every client that uses it.",
								)}
							</Text>
							<Group gap="xs" wrap="nowrap">
								<Code data-testid="mcp-server-key-value" style={{ flex: 1, overflowX: "auto" }}>
									{revealedKey}
								</Code>
								<CopyButton value={revealedKey}>
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
							<Group justify="flex-end">
								<Button
									variant="subtle"
									size="xs"
									onClick={() => setRevealedKey(null)}
									data-testid="mcp-server-key-dismiss-reveal"
								>
									{t("pages.nodeSettings.mcpServerKey.dismissReveal", "I have copied it")}
								</Button>
							</Group>
						</Stack>
					</Alert>
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
						onClick={() =>
							generate.mutate(
								{},
								// The ONLY capture point for the plaintext. If this response is not held here it is
								// gone: the query this invalidates cannot return the key.
								{ onSuccess: (response) => setRevealedKey(response.key ?? null) },
							)
						}
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
						onClick={() => revoke.mutate({}, { onSuccess: () => setRevealedKey(null) })}
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
