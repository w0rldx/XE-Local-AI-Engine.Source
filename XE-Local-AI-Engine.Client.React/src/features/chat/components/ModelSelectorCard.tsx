import { Badge, Group, Select, Stack, Text } from "@mantine/core";
import { IconBrain, IconSparkles } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import type { ModelOption } from "@/features/chat/models/ChatModels";

interface ModelSelectorCardProps {
	modelOptions: ModelOption[];
	selectedModel: string;
	disabled?: boolean;
	onModelChange: (model: string) => void;
}

function display(option: ModelOption | undefined, fallback: string): string {
	return option?.displayName?.trim() || option?.label.trim() || option?.value.trim() || fallback;
}

export function ModelSelectorCard({ modelOptions, selectedModel, disabled = false, onModelChange }: ModelSelectorCardProps) {
	const { t } = useTranslation();
	const selected = modelOptions.find((option) => option.value === selectedModel);
	const data = modelOptions.map((option) => ({
		value: option.value,
		label: display(option, option.value),
		disabled: !option.isAvailable,
	}));

	return (
		<Stack gap={4} style={{ minWidth: 180 }}>
			<Select
				data-testid="chat-model-selector-trigger"
				leftSection={<IconBrain size={15} />}
				data={data}
				value={selectedModel}
				disabled={disabled || modelOptions.length === 0}
				allowDeselect={false}
				onChange={(value) => {
					if (value) {
						onModelChange(value);
					}
				}}
				size="xs"
				aria-label={t("pages.chat.modelLabel", "Model")}
			/>
			<Group gap={6} wrap="nowrap">
				<Text size="xs" c="dimmed" lineClamp={1}>
					{selected?.statusLabel ?? t("pages.chat.modelAvailable", "Available")}
				</Text>
				{selected?.isReasoningModel ? (
					<Badge size="xs" variant="light" color="violet" leftSection={<IconSparkles size={10} />}>
						{t("pages.chat.reasoningLabel", "Reasoning")}
					</Badge>
				) : null}
			</Group>
		</Stack>
	);
}
