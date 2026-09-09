import { Badge, Box, Group, Stack, Text, UnstyledButton } from "@mantine/core";
import { IconChevronRight, IconSparkles } from "@tabler/icons-react";

import { ModelNameTooltip } from "@/features/chat/components/ModelSelectorCard/ModelNameTooltip";
import { cx } from "@/features/chat/components/ModelSelectorCard.helpers";
import type { ModelOption } from "@/features/chat/models/ChatModels";
import { deriveModelDisplay } from "@/features/chat/models/ModelDisplay";

import classes from "../ModelSelectorCard.module.css";

interface ModelSelectorOptionProps {
	option: ModelOption;
	selected: boolean;
	reasoningLabel: string;
	nativeReasoningLabel: string;
	statusFallback: (option: ModelOption) => string;
	onSelect: (value: string) => void;
}

export function ModelSelectorOption({
	option,
	selected,
	reasoningLabel,
	nativeReasoningLabel,
	statusFallback,
	onSelect,
}: ModelSelectorOptionProps) {
	const display = deriveModelDisplay(option, option.value);

	return (
		<UnstyledButton
			data-testid={`chat-model-selector-option-${option.value}`}
			disabled={!option.isAvailable}
			onClick={() => onSelect(option.value)}
			className={cx(
				classes["option-button"],
				selected && classes["option-button-selected"],
				!option.isAvailable && classes["option-button-disabled"],
			)}
		>
			<Group gap="sm" wrap="nowrap" align="flex-start">
				<Box
					className={cx(
						classes["status-accent"],
						option.isAvailable ? classes["status-accent-available"] : classes["status-accent-unavailable"],
					)}
				/>
				<Stack gap={2} style={{ flex: 1, minWidth: 0 }}>
					<Group gap={6} wrap="nowrap">
						<ModelNameTooltip display={display}>
							<Text size="sm" fw={600} lineClamp={1}>
								{display.primary}
							</Text>
						</ModelNameTooltip>
						{/* Two distinct reasoning capabilities, never both set (the detector makes graded win). Violet =
						    a graded think:<level> control; teal = reasons natively on a template-baked channel, keeping
						    the binary On/Off vocabulary. Rendering the second badge is what stops the picker implying a
						    harmony model (gpt-oss) cannot reason at all. */}
						{option.isReasoningModel ? (
							<Badge
								size="xs"
								variant="light"
								color="violet"
								leftSection={<IconSparkles size={10} />}
								className={classes["reasoning-badge"]}
							>
								{reasoningLabel}
							</Badge>
						) : null}
						{!option.isReasoningModel && option.isNativeReasoningModel ? (
							<Badge
								size="xs"
								variant="light"
								color="teal"
								leftSection={<IconSparkles size={10} />}
								className={classes["reasoning-badge"]}
								data-testid={`chat-model-native-reasoning-badge-${option.value}`}
							>
								{nativeReasoningLabel}
							</Badge>
						) : null}
					</Group>
					{/* Line two carries what line one dropped (size · quant), or the availability word when the catalog
					    reported no status at all. The raw id used to sit here too; it now travels in the tooltip, which
					    is the only place it can be shown untruncated. */}
					<Text size="xs" c="dimmed" lineClamp={1}>
						{display.secondary ?? statusFallback(option)}
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
