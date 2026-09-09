import { Box, Group, Stack, Text, UnstyledButton } from "@mantine/core";
import { IconChevronRight } from "@tabler/icons-react";

import { ModelNameTooltip } from "@/features/chat/components/ModelSelectorCard/ModelNameTooltip";
import { cx } from "@/features/chat/components/ModelSelectorCard.helpers";
import type { ModelOption } from "@/features/chat/models/ChatModels";
import { deriveModelDisplay } from "@/features/chat/models/ModelDisplay";

import classes from "../ModelSelectorCard.module.css";

interface CloudModelOptionProps {
	option: ModelOption;
	selected: boolean;
	egressCue: string;
	egressCueColor: string;
	onSelect: (value: string) => void;
}

// A cloud or external row. Line two is the EGRESS CUE rather than the derived secondary: for these models "where does
// this turn go" outranks "what does it weigh", and it is the one fact a local model never has to answer.
export function CloudModelOption({ option, selected, egressCue, egressCueColor, onSelect }: CloudModelOptionProps) {
	const display = deriveModelDisplay(option, option.value);

	return (
		<UnstyledButton
			data-testid={`chat-model-selector-option-${option.value}`}
			onClick={() => onSelect(option.value)}
			className={cx(classes["option-button"], selected && classes["option-button-selected"])}
		>
			<Group gap="sm" wrap="nowrap" align="flex-start">
				<Box className={cx(classes["status-accent"], classes["status-accent-available"])} />
				<Stack gap={2} style={{ flex: 1, minWidth: 0 }}>
					<ModelNameTooltip display={display}>
						<Text size="sm" fw={600} lineClamp={1}>
							{display.primary}
						</Text>
					</ModelNameTooltip>
					<Text size="xs" c={egressCueColor} lineClamp={1} data-testid={`chat-model-selector-cloud-egress-${option.value}`}>
						{egressCue}
					</Text>
				</Stack>
				<IconChevronRight
					size={12}
					color="var(--mantine-color-dimmed)"
					className={cx(classes["option-chevron"], selected && classes["option-chevron-visible"])}
				/>
			</Group>
		</UnstyledButton>
	);
}
