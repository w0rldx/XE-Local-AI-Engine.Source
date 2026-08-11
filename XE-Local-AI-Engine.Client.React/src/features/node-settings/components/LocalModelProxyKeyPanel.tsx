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
	useGenerateLocalModelProxyApiKey,
	useLocalModelProxyApiKey,
	useRevokeLocalModelProxyApiKey,
} from "@/features/node-settings/queries/useLocalModelProxyApiKey";
import { useState } from "react";
import { useTranslation } from "react-i18next";

// Manages the INBOUND local-model-proxy credential: the bearer key an external OpenAI-compatible tool (LiteLLM,
// Continue, a local agent) presents to this node's OpenAI-compatible proxy endpoint, so it can use the local model
// through this node as a plain OpenAI provider. The proxy serves the raw model only — no agent tools, no memory.
//
// The key is shown EXACTLY ONCE, in the response to a generate action, and is unrecoverable afterwards: the node
// persists only a SHA-256 digest, so there is nothing for the GET to return and no way to recover a key the operator
// failed to copy. `revealedKey` therefore lives in component state rather than in the query cache — a refetch,
// remount or navigation drops it, which is the honest lifetime for a value the server can no longer supply. Losing it
// means regenerating and reconfiguring every client, so the reveal carries an explicit copy-it-now warning.
export function LocalModelProxyKeyPanel() {
	const { t } = useTranslation();
	const { data, isLoading } = useLocalModelProxyApiKey();
	const revoke = useRevokeLocalModelProxyApiKey();

	// Held here and nowhere else. Cleared on revoke so a revoked key never lingers on screen as if it still worked.
	const [revealedKey, setRevealedKey] = useState<string | null>(null);

	const generate = useGenerateLocalModelProxyApiKey();

	const isConfigured = data?.configured === true;
	const isBusy = generate.isPending || revoke.isPending;

	return (
		<Card withBorder={true} radius="md" p="lg" data-testid="local-model-proxy-key-card">
			<Stack gap="md">
				<Group justify="space-between" align="center">
					<Group gap="xs" align="center">
						<IconPlugConnected size={20} />
						<Title order={4}>{t("pages.nodeSettings.localModelProxyKey.title", "Local model proxy")}</Title>
					</Group>
					{!isLoading ? (
						<Badge
							color={isConfigured ? "green" : "gray"}
							variant="light"
							data-testid="local-model-proxy-key-status"
						>
							{isConfigured
								? t("pages.nodeSettings.localModelProxyKey.configured", "Key configured")
								: t("pages.nodeSettings.localModelProxyKey.none", "No key")}
						</Badge>
					) : null}
				</Group>

				<Text size="sm" c="dimmed">
					{t(
						"pages.nodeSettings.localModelProxyKey.description",
						"Let an external OpenAI-compatible tool (LiteLLM, Continue, a local agent) use your local model through this node as a plain OpenAI provider — set its base_url to the endpoint below and its API key to the generated key. This serves the raw model only, with no agent tools or memory. Only a one-way hash of the key is stored, so it is shown once when you generate it and cannot be retrieved afterwards. Only clients on this machine can connect.",
					)}
				</Text>

				{isConfigured && data ? (
					<Stack gap="xs">
						<Text size="sm" fw={500}>
							{t("pages.nodeSettings.localModelProxyKey.endpointLabel", "base_url")}
						</Text>
						<Group gap="xs" wrap="nowrap">
							<Code data-testid="local-model-proxy-key-endpoint" style={{ flex: 1, overflowX: "auto" }}>
								{data.endpointUrl}
							</Code>
							<CopyButton value={data.endpointUrl}>
								{({ copied, copy }) => (
									<Button
										variant="default"
										size="xs"
										leftSection={copied ? <IconCheck size={14} /> : <IconCopy size={14} />}
										onClick={copy}
										data-testid="local-model-proxy-key-copy-endpoint"
									>
										{copied ? t("common.copied", "Copied") : t("common.copy", "Copy")}
									</Button>
								)}
							</CopyButton>
						</Group>

						<Text size="sm" fw={500} mt="xs">
							{t("pages.nodeSettings.localModelProxyKey.prefixLabel", "Key")}
						</Text>
						<Code data-testid="local-model-proxy-key-prefix" style={{ overflowX: "auto" }}>
							{`${data.prefix ?? ""}…`}
						</Code>
						<Text size="xs" c="dimmed" data-testid="local-model-proxy-key-not-retrievable">
							{t(
								"pages.nodeSettings.localModelProxyKey.notRetrievable",
								"Only the first characters are shown. The full key is not stored and cannot be shown again — regenerate if you no longer have it.",
							)}
						</Text>

						<Text size="xs" c="dimmed" data-testid="local-model-proxy-key-last-used">
							{data.lastUsedAt
								? t("pages.nodeSettings.localModelProxyKey.lastUsed", "Last used {{when}}", {
										when: new Date(data.lastUsedAt).toLocaleString(),
									})
								: t(
										"pages.nodeSettings.localModelProxyKey.neverUsed",
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
						title={t("pages.nodeSettings.localModelProxyKey.revealTitle", "Copy this key now")}
						data-testid="local-model-proxy-key-reveal"
					>
						<Stack gap="xs">
							<Text size="sm">
								{t(
									"pages.nodeSettings.localModelProxyKey.revealWarning",
									"This is the only time this key will be shown. It is not stored and cannot be recovered — if you lose it you must generate a new one and update every client that uses it.",
								)}
							</Text>
							<Group gap="xs" wrap="nowrap">
								<Code data-testid="local-model-proxy-key-value" style={{ flex: 1, overflowX: "auto" }}>
									{revealedKey}
								</Code>
								<CopyButton value={revealedKey}>
									{({ copied, copy }) => (
										<Button
											variant="default"
											size="xs"
											leftSection={copied ? <IconCheck size={14} /> : <IconCopy size={14} />}
											onClick={copy}
											data-testid="local-model-proxy-key-copy-key"
										>
											{copied ? t("common.copied", "Copied") : t("common.copy", "Copy")}
										</Button>
									)}
								</CopyButton>
							</Group>
							<Group justify="flex-end">
								<Button
									variant="subtle"
									size="xs"
									onClick={() => setRevealedKey(null)}
									data-testid="local-model-proxy-key-dismiss-reveal"
								>
									{t("pages.nodeSettings.localModelProxyKey.dismissReveal", "I have copied it")}
								</Button>
							</Group>
						</Stack>
					</Alert>
				) : null}

				{isConfigured ? (
					<Alert color="blue" variant="light" data-testid="local-model-proxy-key-hint">
						{t(
							"pages.nodeSettings.localModelProxyKey.clientHint",
							"Point your OpenAI-compatible client at base_url = the endpoint above with api_key = the generated key. It serves only the local model — no agent tools, no memory, no knowledge base.",
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
						data-testid="local-model-proxy-key-generate"
					>
						{isConfigured
							? t("pages.nodeSettings.localModelProxyKey.regenerate", "Regenerate key")
							: t("pages.nodeSettings.localModelProxyKey.generate", "Generate key")}
					</Button>
					<Button
						variant="default"
						color="red"
						leftSection={<IconTrash size={16} />}
						loading={revoke.isPending}
						disabled={!isConfigured || isBusy}
						onClick={() => revoke.mutate({}, { onSuccess: () => setRevealedKey(null) })}
						data-testid="local-model-proxy-key-revoke"
					>
						{t("pages.nodeSettings.localModelProxyKey.revoke", "Revoke key")}
					</Button>
				</Group>

				{isConfigured ? (
					<Text size="xs" c="dimmed">
						{t(
							"pages.nodeSettings.localModelProxyKey.regenerateWarning",
							"Regenerating replaces the key immediately — any client still configured with the old one stops working.",
						)}
					</Text>
				) : null}
			</Stack>
		</Card>
	);
}
