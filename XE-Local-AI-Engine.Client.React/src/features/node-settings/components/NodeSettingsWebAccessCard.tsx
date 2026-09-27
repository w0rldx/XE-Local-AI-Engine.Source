import { Card, Group, Stack, Switch, TextInput, Title } from "@mantine/core";
import { IconWorldSearch } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { nodeSettingsFieldError } from "@/features/node-settings/components/NodeSettingsFieldPresentation";
import type { NodeSettingsFieldsForm } from "@/features/node-settings/models/NodeSettingsFieldsModel";

interface Props {
	readonly form: NodeSettingsFieldsForm;
	readonly errors: Readonly<Record<string, string>>;
	readonly onChange: <K extends keyof NodeSettingsFieldsForm>(field: K, value: NodeSettingsFieldsForm[K]) => void;
}

// The web_search / web_fetch kill-switch and the optional SearXNG backend. Both are read live, so no restart hint. The
// URL stays editable while the switch is off, so the backend can be set up before web access is turned on.
export function NodeSettingsWebAccessCard({ form, errors, onChange }: Props) {
	const { t } = useTranslation();

	return (
		<Card withBorder={true} radius="md" p="lg" data-testid="node-settings-web-access-card">
			<Stack gap="md">
				<Group justify="space-between" align="center">
					<Title order={2} size="h4">
						{t("pages.nodeSettings.fields.webAccess.title", "Web access")}
					</Title>
					<IconWorldSearch size={20} />
				</Group>
				<Switch
					label={t("pages.nodeSettings.fields.webAccessEnabled.label", "Allow web search and page fetching")}
					description={t(
						"pages.nodeSettings.fields.webAccessEnabled.description",
						"When enabled, the model can send search queries to DuckDuckGo or SearXNG and download public web pages. Retrieved content is shown to you for review before the model sees it. Off by default.",
					)}
					checked={form.webAccessEnabled}
					onChange={(event) => onChange("webAccessEnabled", event.currentTarget.checked)}
					data-testid="node-settings-web-access-enabled"
				/>
				<TextInput
					label={t("pages.nodeSettings.fields.webSearchSearxngUrl.label", "SearXNG URL")}
					description={t(
						"pages.nodeSettings.fields.webSearchSearxngUrl.description",
						"Leave empty to use DuckDuckGo (best effort). Point to your own SearXNG instance with the JSON format enabled for reliable search.",
					)}
					placeholder="http://localhost:8888"
					value={form.webSearchSearxngUrl}
					onChange={(event) => onChange("webSearchSearxngUrl", event.currentTarget.value)}
					error={nodeSettingsFieldError(t, errors, "webSearchSearxngUrl")}
					data-testid="node-settings-web-search-searxng-url"
				/>
			</Stack>
		</Card>
	);
}
