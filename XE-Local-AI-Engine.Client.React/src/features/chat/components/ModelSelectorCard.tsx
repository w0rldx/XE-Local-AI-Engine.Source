import {
	Badge,
	Box,
	Divider,
	Group,
	Paper,
	Popover,
	ScrollArea,
	Stack,
	Text,
	TextInput,
	Tooltip,
	UnstyledButton,
} from "@mantine/core";
import {
	IconBrandAzure,
	IconChevronDown,
	IconChevronRight,
	IconCloud,
	IconCpu,
	IconPlugConnected,
	IconSearch,
	IconSparkles,
} from "@tabler/icons-react";
import type { ReactElement, ReactNode } from "react";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { COMPACT_CONTROLS_BREAKPOINT } from "@/core/layout/constants/LayoutBreakpoints";
import useWindowDimensions from "@/core/layout/hooks/useWindowDimensions";
import { cx } from "@/features/chat/components/ModelSelectorCard.helpers";
import type { ModelOption } from "@/features/chat/models/ChatModels";
import type { ModelDisplay } from "@/features/chat/models/ModelDisplay";
import { deriveModelDisplay } from "@/features/chat/models/ModelDisplay";
import { EXTERNAL_PROVIDER, groupExternalModelOptions, hasNoLocalChatModels } from "@/features/chat/pages/ChatModelOptions";
import { AZURE_FOUNDRY_PROVIDER } from "@/features/chat/queries/useCodexModelOptions";

import classes from "./ModelSelectorCard.module.css";

// The identity a shortened name left out — friendly label, raw id, serving connection — on hover and on focus.
// Never on touch: on a phone the tap has to reach the control underneath, which is the only way to open the picker.
//
// DISABLED rather than unmounted when the name was not shortened (so the tooltip never just repeats the text it
// covers): the trigger sits inside `Popover.Target`, which clones its single child, and swapping that child between
// `<Tooltip><Paper/></Tooltip>` and a bare `<Paper/>` as the selection changes remounts the target out from under the
// popover — which left the trigger showing the model it had before.
function ModelNameTooltip({ display, children }: { display: ModelDisplay; children: ReactElement }): ReactElement {
	return (
		<Tooltip
			label={display.full}
			disabled={display.full === display.primary}
			withinPortal={true}
			openDelay={350}
			multiline={true}
			maw={300}
			events={{ hover: true, focus: true, touch: false }}
		>
			{children}
		</Tooltip>
	);
}

// Stable empty default for `cloudModelOptions` so an unset prop doesn't mint a fresh array each render
// (a new `[]` literal as a default value breaks memo/identity comparisons in this component's useMemo deps).
const EMPTY_CLOUD_MODEL_OPTIONS: ModelOption[] = [];

interface ModelSelectorCardProps {
	modelOptions: ModelOption[];
	// Cloud (Codex) model options shown in a separate section. Only rendered when non-empty
	// (i.e. when the user is signed into Codex). Absent or empty = section hidden entirely.
	cloudModelOptions?: ModelOption[];
	selectedModel: string;
	disabled?: boolean;
	onModelChange: (model: string) => void;
}

interface ModelSelectorOptionProps {
	option: ModelOption;
	selected: boolean;
	reasoningLabel: string;
	nativeReasoningLabel: string;
	statusFallback: (option: ModelOption) => string;
	onSelect: (value: string) => void;
}

function ModelSelectorOption({
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

interface CloudModelOptionProps {
	option: ModelOption;
	selected: boolean;
	egressCue: string;
	egressCueColor: string;
	onSelect: (value: string) => void;
}

// A cloud or external row. Line two is the EGRESS CUE rather than the derived secondary: for these models "where does
// this turn go" outranks "what does it weigh", and it is the one fact a local model never has to answer.
function CloudModelOption({ option, selected, egressCue, egressCueColor, onSelect }: CloudModelOptionProps) {
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

interface CloudModelSectionProps {
	items: ModelOption[];
	title: string;
	egressCue: string;
	// Colour of the egress cue. Orange (the default) means the turn leaves this network; a declared-local external
	// endpoint uses the dimmed colour instead, because saying "Local network" in a warning colour misreads.
	egressCueColor?: string;
	icon: ReactNode;
	selectedModel: string;
	onSelect: (value: string) => void;
}

function CloudModelSection({
	items,
	title,
	egressCue,
	egressCueColor = "orange.6",
	icon,
	selectedModel,
	onSelect,
}: CloudModelSectionProps) {
	if (items.length === 0) {
		return null;
	}

	return (
		<Box>
			<Group gap={6} px="sm" py={6} wrap="nowrap">
				<Text size="xs" fw={700} c="dimmed" tt="uppercase" className={classes["dropdown-label"]} style={{ flex: 1 }}>
					{`${title} (${items.length})`}
				</Text>
				{icon}
			</Group>
			{items.map((option) => (
				<CloudModelOption
					key={option.value}
					option={option}
					selected={option.value === selectedModel}
					egressCue={egressCue}
					egressCueColor={egressCueColor}
					onSelect={onSelect}
				/>
			))}
		</Box>
	);
}

interface ModelSelectorSectionProps {
	items: ModelOption[];
	title: string;
	reasoningLabel: string;
	nativeReasoningLabel: string;
	selectedModel: string;
	statusFallback: (option: ModelOption) => string;
	onSelect: (value: string) => void;
}

function ModelSelectorSection({
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

export function ModelSelectorCard({
	modelOptions,
	cloudModelOptions = EMPTY_CLOUD_MODEL_OPTIONS,
	selectedModel,
	disabled = false,
	onModelChange,
}: ModelSelectorCardProps) {
	const { t } = useTranslation();
	const [pickerOpened, setPickerOpened] = useState(false);
	const [searchQuery, setSearchQuery] = useState("");

	const hasCloudOptions = cloudModelOptions.length > 0;
	// Look up the selected option in both local and cloud lists so the trigger label reflects
	// whichever group the active selection belongs to.
	const allOptions = useMemo(() => [...modelOptions, ...cloudModelOptions], [modelOptions, cloudModelOptions]);
	const selected = allOptions.find((option) => option.value === selectedModel);
	const placeholder = t("pages.chat.modelPlaceholder", "Select model");
	const selectedDisplay = deriveModelDisplay(selected, placeholder);
	// Below the theme's `sm` the composer's control row has no width to spare, so the trigger keeps only the line that
	// identifies the model. It stays a NAMED control rather than the bare icon it used to collapse to — an icon alone
	// left the operator with no way to tell which model a phone was about to send to.
	const { width } = useWindowDimensions();
	const isCompactViewport = width < COMPACT_CONTROLS_BREAKPOINT;
	const hasOptions = modelOptions.length > 0;
	const isDisabled = disabled || !hasOptions;
	const showSearch = modelOptions.length > 5;
	// The chat picker is strictly filtered to chat-capable models, so a node whose only installed
	// models are embedding/unknown shows just the local-default option. Detect that to explain the otherwise-bare list.
	const hasNoChatModels = hasNoLocalChatModels(modelOptions);
	const reasoningLabel = t("pages.chat.reasoningLabel", "Reasoning");
	// Distinct from `reasoningLabel`: that badge means "a graded reasoning control is available", this one means
	// "the model reasons by default, on/off only".
	const nativeReasoningLabel = t("pages.chat.nativeReasoningLabel", "Native reasoning");
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

	// Cloud options are also filtered by the search query so a user typing "codex" or a model name
	// narrows both sections simultaneously.
	const filteredCloud = useMemo(() => {
		const query = searchQuery.trim().toLowerCase();
		if (!query) {
			return cloudModelOptions;
		}

		return cloudModelOptions.filter(
			(option) =>
				option.value.toLowerCase().includes(query) ||
				option.label.toLowerCase().includes(query) ||
				option.displayName?.toLowerCase().includes(query),
		);
	}, [cloudModelOptions, searchQuery]);

	// Cloud options render in one labeled group per provider. Azure deployments carry the AzureFoundry tag; external
	// endpoints get one group per CONNECTION (below); everything else (Codex, or an untagged cloud option) falls into
	// the Codex group.
	const azureCloudOptions = useMemo(
		() => filteredCloud.filter((option) => option.provider === AZURE_FOUNDRY_PROVIDER),
		[filteredCloud],
	);
	const codexCloudOptions = useMemo(
		() => filteredCloud.filter((option) => option.provider !== AZURE_FOUNDRY_PROVIDER && option.provider !== EXTERNAL_PROVIDER),
		[filteredCloud],
	);
	const externalGroups = useMemo(
		() => groupExternalModelOptions(filteredCloud.filter((option) => option.provider === EXTERNAL_PROVIDER)),
		[filteredCloud],
	);

	const availableOptions = filtered.filter((option) => option.isAvailable);
	const unavailableOptions = filtered.filter((option) => !option.isAvailable);

	const closePicker = (): void => {
		setPickerOpened(false);
		setSearchQuery("");
	};

	const select = (value: string): void => {
		// Accept selections from either local or cloud lists; cloud options are always available.
		const localOption = modelOptions.find((o) => o.value === value);
		if (localOption !== undefined && !localOption.isAvailable) {
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
			width={320}
		>
			<Popover.Target>
				{/* The tooltip sits INSIDE Popover.Target so the popover's props still reach the paper underneath (the
				    same nesting the composer's action icons use). Hover and focus only: a tap must open the picker. */}
				<ModelNameTooltip display={selectedDisplay}>
					<Paper
						radius="md"
						data-testid="chat-model-selector-selected"
						className={cx(
							classes["trigger-paper"],
							classes["compact-trigger"],
							isCompactViewport && classes["compact-trigger-narrow"],
							classes["compact-paper"],
						)}
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
							<Group gap="xs" wrap="nowrap" align="center" style={{ width: "100%" }}>
								<IconCpu size={16} color="var(--mantine-color-dimmed)" style={{ flexShrink: 0 }} />
								<Stack gap={0} style={{ flex: 1, minWidth: 0 }}>
									<Text lineClamp={1} className={classes["trigger-primary"]} data-testid="chat-model-selector-trigger-name">
										{selectedDisplay.primary}
									</Text>
									{!isCompactViewport && selectedDisplay.secondary !== undefined ? (
										<Text
											lineClamp={1}
											c="dimmed"
											className={classes["trigger-secondary"]}
											data-testid="chat-model-selector-trigger-detail"
										>
											{selectedDisplay.secondary}
										</Text>
									) : null}
								</Stack>
								{isCompactViewport ? null : (
									<IconChevronDown
										size={12}
										color="var(--mantine-color-dimmed)"
										className={cx(classes["chevron"], pickerOpened && classes["chevron-open"])}
									/>
								)}
							</Group>
						</UnstyledButton>
					</Paper>
				</ModelNameTooltip>
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
									nativeReasoningLabel={nativeReasoningLabel}
									selectedModel={selectedModel}
									statusFallback={statusFallback}
									onSelect={select}
								/>
								<ModelSelectorSection
									items={unavailableOptions}
									title={t("pages.chat.modelSelector.unavailableModels", "Unavailable")}
									reasoningLabel={reasoningLabel}
									nativeReasoningLabel={nativeReasoningLabel}
									selectedModel={selectedModel}
									statusFallback={statusFallback}
									onSelect={select}
								/>
								{filtered.length === 0 && filteredCloud.length === 0 ? (
									<Text size="sm" c="dimmed" px="sm" py="xs" ta="center">
										{t("pages.chat.modelSelector.noResults", "No models found")}
									</Text>
								) : null}
								{hasNoChatModels && searchQuery.trim().length === 0 ? (
									<Text size="sm" c="dimmed" px="sm" py="xs" ta="center" data-testid="chat-model-selector-no-chat-models">
										{t("pages.chat.modelSelector.noChatModels", "No chat-capable models")}
									</Text>
								) : null}
								{hasCloudOptions ? (
									<>
										<Divider my={4} />
										<CloudModelSection
											items={codexCloudOptions}
											title={t("pages.chat.modelSelector.cloudGroup", "Cloud (Codex)")}
											egressCue={t("pages.chat.modelSelector.cloudEgressCue", "Sent to OpenAI")}
											icon={<IconCloud size={12} color="var(--mantine-color-dimmed)" />}
											selectedModel={selectedModel}
											onSelect={select}
										/>
										<CloudModelSection
											items={azureCloudOptions}
											title={t("pages.chat.modelSelector.cloudGroupAzure", "Cloud (Azure Foundry)")}
											egressCue={t("pages.chat.modelSelector.cloudEgressCueAzure", "Sent to Azure")}
											icon={<IconBrandAzure size={12} color="var(--mantine-color-dimmed)" />}
											selectedModel={selectedModel}
											onSelect={select}
										/>
										{/* One section per external connection, because that — not the shared `external` provider
										    tag — is what tells the operator where a turn actually goes. The cue follows the
										    connection's DECLARED trust: a declared-local endpoint stays on the network, a
										    declared-cloud one leaves it. */}
										{externalGroups.map((group) => (
											<CloudModelSection
												key={group.connectionId}
												items={group.items}
												title={t("pages.chat.modelSelector.externalGroup", {
													defaultValue: "External · {{name}}",
													name: group.connectionName,
												})}
												egressCue={
													group.isDeclaredCloud
														? t("pages.chat.modelSelector.externalEgressCueCloud", {
																defaultValue: "Sent to {{name}}",
																name: group.connectionName,
															})
														: t("pages.chat.modelSelector.externalEgressCueLocal", "Local network")
												}
												egressCueColor={group.isDeclaredCloud ? undefined : "dimmed"}
												icon={<IconPlugConnected size={12} color="var(--mantine-color-dimmed)" />}
												selectedModel={selectedModel}
												onSelect={select}
											/>
										))}
									</>
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
