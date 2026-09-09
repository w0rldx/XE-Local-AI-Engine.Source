import { Box, Text } from "@mantine/core";

import { ModelSelectorOption } from "@/features/chat/components/ModelSelectorCard/ModelSelectorOption";
import type { ModelOption } from "@/features/chat/models/ChatModels";

import classes from "../ModelSelectorCard.module.css";

interface ModelSelectorSectionProps {
	items: ModelOption[];
	title: string;
	reasoningLabel: string;
	nativeReasoningLabel: string;
	selectedModel: string;
	statusFallback: (option: ModelOption) => string;
	onSelect: (value: string) => void;
}

export function ModelSelectorSection({
	items,
	title,
	reasoningLabel,
	nativeReasoningLabel,
	selectedModel,
	statusFallback,
	onSelect,
}: ModelSelectorSectionProps) {
	if (items.length === 0) {
		return null;
	}

	return (
		<Box>
			<Text size="xs" fw={700} c="dimmed" tt="uppercase" px="sm" py={6} className={classes["dropdown-label"]}>
				{`${title} (${items.length})`}
			</Text>
			{items.map((option) => (
				<ModelSelectorOption
					key={option.value}
					option={option}
					selected={option.value === selectedModel}
					reasoningLabel={reasoningLabel}
					nativeReasoningLabel={nativeReasoningLabel}
					statusFallback={statusFallback}
					onSelect={onSelect}
				/>
			))}
		</Box>
	);
}
