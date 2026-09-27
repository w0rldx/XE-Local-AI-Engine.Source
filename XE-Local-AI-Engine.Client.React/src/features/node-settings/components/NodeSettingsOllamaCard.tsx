import { Card, Group, Stack, Text, TextInput, Title } from "@mantine/core";
import { IconServer2 } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import {
	nodeSettingsFieldError,
	nodeSettingsFieldLabel,
} from "@/features/node-settings/components/NodeSettingsFieldPresentation";
import type { NodeSettingsFieldsForm } from "@/features/node-settings/models/NodeSettingsFieldsModel";

interface Props {
	readonly form: NodeSettingsFieldsForm;
	readonly errors: Readonly<Record<string, string>>;
	readonly onChange: <K extends keyof NodeSettingsFieldsForm>(field: K, value: NodeSettingsFieldsForm[K]) => void;
	// Nothing reads the Ollama endpoint when the runtime is gated off (XE_OLLAMA_RUNTIME_ENABLED=false), so offering
	// the field invites an operator to configure a runtime this node will never start. The page owns the probe and
	// FAILS OPEN: only a definite `false` arrives as true here, so a still-loading or failed probe leaves the field
	// exactly as it is. The stored value stays in the form model either way, so hiding the input never changes what a
	// save round-trips.
	readonly ollamaRuntimeDisabled: boolean;
}

export function NodeSettingsOllamaCard({ form, errors, onChange, ollamaRuntimeDisabled }: Props) {
	const { t } = useTranslation();

	return (
		<Card withBorder={true} radius="md" p="lg" data-testid="node-settings-ollama-card">
			<Stack gap="md">
				<Group justify="space-between" align="center">
					<Title order={2} size="h4">
						{t("pages.nodeSettings.fields.ollama.title", "Ollama")}
					</Title>
					<IconServer2 size={20} />
				</Group>
				{ollamaRuntimeDisabled ? (
					<Text size="xs" c="dimmed" data-testid="node-settings-ollama-disabled">
						{t("pages.nodeSettings.fields.ollamaEndpoint.disabled", "Ollama runtime is disabled on this node.")}
					</Text>
				) : (
					<TextInput
						label={nodeSettingsFieldLabel(
							t,
							"ollamaEndpoint",
							t("pages.nodeSettings.fields.ollamaEndpoint.label", "Ollama endpoint"),
						)}
						description={t("pages.nodeSettings.fields.ollamaEndpoint.description", "The Ollama API base URL.")}
						placeholder="http://127.0.0.1:11434"
						value={form.ollamaEndpoint}
						onChange={(event) => onChange("ollamaEndpoint", event.currentTarget.value)}
						error={nodeSettingsFieldError(t, errors, "ollamaEndpoint")}
						data-testid="node-settings-ollama-endpoint"
					/>
				)}
			</Stack>
		</Card>
	);
}
