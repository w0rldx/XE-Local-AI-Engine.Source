import { Button, Menu, Stack, Text } from "@mantine/core";
import { IconChevronDown, IconPlus } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import {
	isExternalProviderTemplate,
	type ProviderTemplateId,
	type ProviderTemplateOption,
} from "@/features/cloud-providers/models/ProviderTemplates";

interface AddProviderMenuProps {
	readonly options: readonly ProviderTemplateOption[];
	readonly onPick: (id: ProviderTemplateId) => void;
}

export function AddProviderMenu({ options, onPick }: AddProviderMenuProps) {
	const { t } = useTranslation();
	const labels: Record<ProviderTemplateId, { label: string; description: string }> = {
		generic: {
			label: t("pages.cloudProviders.templates.generic.label", "OpenAI-compatible endpoint"),
			description: t("pages.cloudProviders.templates.generic.description"),
		},
		gateway: {
			label: t("pages.cloudProviders.templates.gateway.label", "Enterprise AI gateway"),
			description: t("pages.cloudProviders.templates.gateway.description"),
		},
		azureOpenAi: {
			label: t("pages.cloudProviders.templates.azureOpenAi.label", "Azure OpenAI v1 (API key)"),
			description: t("pages.cloudProviders.templates.azureOpenAi.description"),
		},
		azureFoundry: {
			label: t("pages.cloudProviders.templates.azureFoundry.label", "Azure AI Foundry"),
			description: t("pages.cloudProviders.templates.azureFoundry.description"),
		},
		codex: {
			label: t("pages.cloudProviders.templates.codex.label", "ChatGPT (Codex)"),
			description: t("pages.cloudProviders.templates.codex.description"),
		},
	};
	const blockedReasons = {
		azureFoundryConfigured: t("pages.cloudProviders.templates.azureFoundry.configured"),
		codexSignedIn: t("pages.cloudProviders.templates.codex.signedIn"),
	};
	const external = options.filter((option) => isExternalProviderTemplate(option.id));
	const single = options.filter((option) => !isExternalProviderTemplate(option.id));

	const item = (option: ProviderTemplateOption) => (
		<Menu.Item
			key={option.id}
			disabled={option.blockedBy !== null}
			data-testid={`cloud-provider-template-${option.id}`}
			onClick={() => onPick(option.id)}
		>
			<Stack gap={0}>
				<Text size="sm" fw={500}>
					{labels[option.id].label}
				</Text>
				<Text size="xs" c="dimmed">
					{option.blockedBy === null ? labels[option.id].description : blockedReasons[option.blockedBy]}
				</Text>
			</Stack>
		</Menu.Item>
	);

	return (
		<Menu position="bottom-end" width={320} withinPortal={true}>
			<Menu.Target>
				<Button
					size="xs"
					leftSection={<IconPlus size={14} />}
					rightSection={<IconChevronDown size={14} />}
					disabled={options.length === 0}
					data-testid="cloud-provider-add"
				>
					{t("pages.cloudProviders.add", "Add provider")}
				</Button>
			</Menu.Target>
			<Menu.Dropdown>
				{external.length > 0 ? (
					<>
						<Menu.Label>{t("pages.cloudProviders.templates.openAiCompatibleGroup", "OpenAI-compatible / gateway")}</Menu.Label>
						{external.map(item)}
					</>
				) : null}
				{external.length > 0 && single.length > 0 ? <Menu.Divider /> : null}
				{single.map(item)}
			</Menu.Dropdown>
		</Menu>
	);
}
