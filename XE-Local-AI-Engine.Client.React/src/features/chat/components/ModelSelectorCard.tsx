import { Badge, Box, Divider, Group, Paper, Popover, ScrollArea, Stack, Text, TextInput, UnstyledButton } from "@mantine/core";
import { IconChevronDown, IconChevronRight, IconCpu, IconSearch, IconSparkles } from "@tabler/icons-react";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { cx, display } from "@/features/chat/components/ModelSelectorCard.helpers";
import type { ModelOption } from "@/features/chat/models/ChatModels";
import { localDefaultModelValue } from "@/features/chat/models/NodeChatModelSelection";
import classes from "./ModelSelectorCard.module.css";

interface ModelSelectorCardProps {
	modelOptions: ModelOption[];
	selectedModel: string;
	disabled?: boolean;
	onModelChange: (model: string) => void;
}

interface ModelSelectorOptionProps {
	option: ModelOption;
	selected: boolean;
	reasoningLabel: string;
	statusFallback: (option: ModelOption) => string;
	onSelect: (value: string) => void;
}

function ModelSelectorOption({ option, selected, reasoningLabel, statusFallback, onSelect }: ModelSelectorOptionProps) {
	const label = display(option, option.value);
	const showValue = option.value !== label;

	return (
		<UnstyledButton
			data-testid={`chat-model-selector-option-${option.value}`}
			disabled={!option.isAvailable}
			onClick={() => onSelect(option.value)}
			className={cx(classes["option-button"], selected && classes["option-button-selected"], !option.isAvailable && classes["option-button-disabled"])}
		>
			<Group gap="sm" wrap="nowrap" align="flex-start">
				<Box className={cx(classes["status-accent"], option.isAvailable ? classes["status-accent-available"] : classes["status-accent-unavailable"])} />
				<Stack gap={2} style={{ flex: 1, minWidth: 0 }}>
					<Group gap={6} wrap="nowrap">
						<Text size="sm" fw={600} lineClamp={1}>
							{label}
						</Text>
						{option.isReasoningModel ? (
							<Badge size="xs" variant="light" color="violet" leftSection={<IconSparkles size={10} />} className={classes["reasoning-badge"]}>
								{reasoningLabel}
							</Badge>
						) : null}
					</Group>
					<Group gap={6} wrap="nowrap">
						<Text size="xs" c="dimmed" lineClamp={1}>
							{option.statusLabel?.trim() || statusFallback(option)}
						</Text>
						{showValue ? (
							<Text size="xs" c="dimmed" opacity={0.6} lineClamp={1}>
								{option.value}
							</Text>
						) : null}
					</Group>
				</Stack>
				<IconChevronRight size={12} color="var(--mantine-color-dimmed)" className={cx(classes["option-chevron"], selected && classes["option-chevron-visible"])} />
			</Group>
		</UnstyledButton>
	);
}

interface ModelSelectorSectionProps {
	items: ModelOption[];
	title: string;
	reasoningLabel: string;
	selectedModel: string;
	statusFallback: (option: ModelOption) => string;
	onSelect: (value: string) => void;
}

function ModelSelectorSection({ items, title, reasoningLabel, selectedModel, statusFallback, onSelect }: ModelSelectorSectionProps) {
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
					statusFallback={statusFallback}
					onSelect={onSelect}
				/>
			))}
		</Box>
	);
}

export function ModelSelectorCard({ modelOptions, selectedModel, disabled = false, onModelChange }: ModelSelectorCardProps) {
	const { t } = useTranslation();
	const [pickerOpened, setPickerOpened] = useState(false);
	const [searchQuery, setSearchQuery] = useState("");

	const selected = modelOptions.find((option) => option.value === selectedModel);
	const placeholder = t("pages.chat.modelPlaceholder", "Select model");
	const selectedLabel = display(selected, placeholder);
	const hasOptions = modelOptions.length > 0;
	const isDisabled = disabled || !hasOptions;
	const showSearch = modelOptions.length > 5;
	// The chat picker is strictly filtered to chat-capable models (locked decision D3), so a node whose only installed
	// models are embedding/unknown shows just the local-default option. Detect that to explain the otherwise-bare list.
	const hasNoChatModels = modelOptions.every((option) => option.value === localDefaultModelValue);
	const reasoningLabel = t("pages.chat.reasoningLabel", "Reasoning");
	const statusFallback = (option: ModelOption): string =>
		option.isAvailable ? t("pages.chat.modelAvailable", "Available") : t("pages.chat.modelUnavailable", "Unavailable");

	const filtered = useMemo(() => {
		const query = searchQuery.trim().toLowerCase();
		if (!query) {
			return modelOptions;
		}

		return modelOptions.filter(
			(option) =>
				option.value.toLowerCase().includes(query) ||
				option.label.toLowerCase().includes(query) ||
				option.displayName?.toLowerCase().includes(query),
		);
	}, [modelOptions, searchQuery]);

	const availableOptions = filtered.filter((option) => option.isAvailable);
	const unavailableOptions = filtered.filter((option) => !option.isAvailable);

	const closePicker = (): void => {
		setPickerOpened(false);
		setSearchQuery("");
	};

	const select = (value: string): void => {
		const option = modelOptions.find((modelOption) => modelOption.value === value);
		if (!option || !option.isAvailable) {
			return;
		}

		onModelChange(value);
		closePicker();
	};

	return (
		<Popover
			position="top-start"
			offset={4}
			withinPortal={true}
			shadow="md"
			opened={pickerOpened}
			onChange={(opened) => {
				setPickerOpened(opened);
				if (!opened) {
					setSearchQuery("");
				}
			}}
			width={260}
		>
			<Popover.Target>
				<Paper
					radius="md"
					data-testid="chat-model-selector-selected"
					className={cx(classes["trigger-paper"], selected && classes["trigger-paper-selected"], classes["compact-trigger"], classes["compact-paper"])}
				>
					<UnstyledButton
						type="button"
						data-testid="chat-model-selector-trigger"
						disabled={isDisabled}
						onClick={() => {
							if (!isDisabled) {
								setPickerOpened((previous) => !previous);
							}
						}}
						className={classes["compact-trigger-button"]}
						aria-disabled={isDisabled}
						aria-expanded={pickerOpened}
						aria-label={t("pages.chat.modelLabel", "Model")}
					>
						<Group gap="xs" wrap="nowrap" align="center">
							<IconCpu size={16} color="var(--mantine-color-dimmed)" style={{ flexShrink: 0 }} />
							<Text size="xs" fw={600} lineClamp={1} className={classes["trigger-label"]} style={{ flex: 1, minWidth: 0 }}>
								{selectedLabel}
							</Text>
							<IconChevronDown size={12} color="var(--mantine-color-dimmed)" className={cx(classes["chevron"], pickerOpened && classes["chevron-open"])} />
						</Group>
					</UnstyledButton>
				</Paper>
			</Popover.Target>
			<Popover.Dropdown p={6}>
				{hasOptions ? (
					<Stack gap={2}>
						{showSearch ? (
							<Box px="xs" pt={4} pb={2}>
								<TextInput
									placeholder={t("pages.chat.modelSelector.search", "Search models...")}
									size="xs"
									leftSection={<IconSearch size={14} />}
									value={searchQuery}
									onChange={(event) => setSearchQuery(event.currentTarget.value)}
									className={classes["search-input"]}
									data-testid="chat-model-selector-search"
								/>
							</Box>
						) : null}
						<ScrollArea.Autosize mah={320} type="hover" offsetScrollbars={true}>
							<Stack gap={2}>
								<ModelSelectorSection
									items={availableOptions}
									title={t("pages.chat.modelSelector.availableModels", "Available")}
									reasoningLabel={reasoningLabel}
									selectedModel={selectedModel}
									statusFallback={statusFallback}
									onSelect={select}
								/>
								<ModelSelectorSection
									items={unavailableOptions}
									title={t("pages.chat.modelSelector.unavailableModels", "Unavailable")}
									reasoningLabel={reasoningLabel}
									selectedModel={selectedModel}
									statusFallback={statusFallback}
									onSelect={select}
								/>
								{filtered.length === 0 ? (
									<Text size="sm" c="dimmed" px="sm" py="xs" ta="center">
										{t("pages.chat.modelSelector.noResults", "No models found")}
									</Text>
								) : null}
								{hasNoChatModels && searchQuery.trim().length === 0 ? (
									<Text size="sm" c="dimmed" px="sm" py="xs" ta="center" data-testid="chat-model-selector-no-chat-models">
										{t("pages.chat.modelSelector.noChatModels", "No chat-capable models")}
									</Text>
								) : null}
							</Stack>
						</ScrollArea.Autosize>
						<Divider my={4} />
						<Text size="xs" c="dimmed" px="sm" py={4} lh={1.4}>
							{t("pages.chat.modelSelector.hint", "Choose a model installed on the local node.")}
						</Text>
					</Stack>
				) : (
					<Text size="sm" c="dimmed" px="sm" py="xs" data-testid="chat-model-selector-empty">
						{placeholder}
					</Text>
				)}
			</Popover.Dropdown>
		</Popover>
	);
}
