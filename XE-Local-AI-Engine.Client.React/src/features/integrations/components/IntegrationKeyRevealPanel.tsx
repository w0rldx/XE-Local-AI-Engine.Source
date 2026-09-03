import { Alert, Button, Code, CopyButton, Group, Stack, Text } from "@mantine/core";
import { IconAlertTriangle, IconCheck, IconCopy } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

interface IntegrationKeyRevealPanelProps {
	apiKey: string;
	onDismiss: () => void;
}

// Show-once reveal for a freshly generated integration key, mirroring the local-model-proxy key panel. The node
// persists only a SHA-256 digest, so this is the only time the plaintext exists anywhere: it is held in the page's
// component state (never a store, never the query cache), which means a remount or a navigation drops it while the
// list refetch that a successful generate fires leaves it on screen — the operator is still reading it when that
// lands, and clearing it there would destroy their only copy.
export function IntegrationKeyRevealPanel({ apiKey, onDismiss }: IntegrationKeyRevealPanelProps) {
	const { t } = useTranslation();

	return (
		<Alert
			color="yellow"
			variant="light"
			icon={<IconAlertTriangle size={18} />}
			title={t("pages.integrations.keys.reveal.title", "Copy this key now")}
			data-testid="integration-key-reveal"
		>
			<Stack gap="xs">
				<Text size="sm">
					{t(
						"pages.integrations.keys.reveal.warning",
						"This is the only time this key will be shown. It is not stored and cannot be recovered — if you lose it you must generate a new one and update the client that uses it.",
					)}
				</Text>
				<Group gap="xs" wrap="nowrap">
					<Code data-testid="integration-key-reveal-value" style={{ flex: 1, overflowX: "auto" }}>
						{apiKey}
					</Code>
					<CopyButton value={apiKey}>
						{({ copied, copy }) => (
							<Button
								variant="default"
								size="xs"
								leftSection={copied ? <IconCheck size={14} /> : <IconCopy size={14} />}
								onClick={copy}
								data-testid="integration-key-reveal-copy"
							>
								{copied ? t("common.copied", "Copied") : t("common.copy", "Copy")}
							</Button>
						)}
					</CopyButton>
				</Group>
				<Group justify="flex-end">
					<Button variant="subtle" size="xs" onClick={onDismiss} data-testid="integration-key-reveal-dismiss">
						{t("pages.integrations.keys.reveal.dismiss", "I have copied it")}
					</Button>
				</Group>
			</Stack>
		</Alert>
	);
}
