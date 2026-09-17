import { Box, Divider, Group, Paper, Popover, ScrollArea, Stack, Text, TextInput, UnstyledButton } from "@mantine/core";
import { IconBrandAzure, IconChevronDown, IconCloud, IconCpu, IconPlugConnected, IconSearch } from "@tabler/icons-react";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { COMPACT_CONTROLS_BREAKPOINT } from "@/core/layout/constants/LayoutBreakpoints";
import useWindowDimensions from "@/core/layout/hooks/useWindowDimensions";
import { EXTERNAL_PROVIDER } from "@/core/models/LocalModelProviders";
import { CloudModelSection } from "@/features/chat/components/ModelSelectorCard/CloudModelSection";
import { ModelNameTooltip } from "@/features/chat/components/ModelSelectorCard/ModelNameTooltip";
import { ModelSelectorSection } from "@/features/chat/components/ModelSelectorCard/ModelSelectorSection";
import { cx } from "@/features/chat/components/ModelSelectorCard.helpers";
import type { ModelOption } from "@/features/chat/models/ChatModels";
import { deriveModelDisplay } from "@/features/chat/models/ModelDisplay";
import { groupExternalModelOptions, hasNoLocalChatModels } from "@/features/chat/pages/ChatModelOptions";
import { AZURE_FOUNDRY_PROVIDER } from "@/features/chat/queries/useCodexModelOptions";

import classes from "./ModelSelectorCard.module.css";

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
	const hasOptions = allOptions.length > 0;
	const isDisabled = disabled || !hasOptions;
	// Counted over EVERY option the dropdown renders, not just the node's own: a node whose models all come from cloud
	// or external connections has just as long a list and just as much need of a search box.
	const showSearch = allOptions.length > 5;
	// The chat picker is strictly filtered to chat-capable models, so a node whose only installed
	// models are embedding/unknown shows just the local-default option. Detect that to explain the otherwise-bare list.
	// Suppressed once cloud or external options exist: the list is not bare then, and this message's guidance is about
	// installing a LOCAL model, which is not what an external-only install is missing.
	const hasNoChatModels = hasNoLocalChatModels(modelOptions) && !hasCloudOptions;
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
				{/* The Paper must be Popover.Target's DIRECT child: the target clones its child to attach the popover's
				    anchor ref, and a Tooltip in between swallows that ref — the dropdown then positions against the
				    viewport's top-left corner instead of this trigger. The tooltip therefore wraps the BUTTON inside.
				    Hover and focus only: a tap must open the picker. */}
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
					<ModelNameTooltip display={selectedDisplay}>
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
					</ModelNameTooltip>
				</Paper>
			</Popover.Target>
			<Popover.Dropdown p={6}>
				{hasOptions ? (
					<Stack gap={2}>
						{showSearch ? (
							<Box px="xs" pt={4} pb={2}>
								<TextInput
									placeholder={t("pages.chat.modelSelector.search", "Search models...")}
									aria-label={t("pages.chat.modelSelector.search", "Search models...")}
									size="xs"
									leftSection={<IconSearch size={14} />}
									value={searchQuery}
									onChange={(event) => setSearchQuery(event.currentTarget.value)}
									className={classes["search-input"]}
									data-testid="chat-model-selector-search"
								/>
							</Box>
						) : null}
						<ScrollArea.Autosize mah={320} type="auto" offsetScrollbars={true}>
							<Stack gap={2}>
								<ModelSelectorSection
									items={availableOptions}
									title={t("pages.chat.modelSelector.availableModels", "Available models")}
									reasoningLabel={reasoningLabel}
									nativeReasoningLabel={nativeReasoningLabel}
									selectedModel={selectedModel}
									statusFallback={statusFallback}
									onSelect={select}
								/>
								<ModelSelectorSection
									items={unavailableOptions}
									title={t("pages.chat.modelSelector.unavailableModels", "Unavailable models")}
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
