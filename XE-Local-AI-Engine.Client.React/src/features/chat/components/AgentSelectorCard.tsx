import { Badge, Box, Divider, Group, Paper, Popover, ScrollArea, Stack, Text, TextInput, UnstyledButton } from "@mantine/core";
import { IconCheck, IconChevronDown, IconChevronRight, IconSearch, IconUsers } from "@tabler/icons-react";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import type { AgentOption } from "@/features/chat/models/ChatModels";

// Compact trigger styling mirrors ModelSelectorCard's compact-trigger pattern. The fixed 36px height matches the
// composer's action icons + send button so the whole control row is one even height (the previous inline style had
// no height and rendered shorter than its neighbours).
const triggerStyle = {
	display: "inline-flex",
	alignItems: "center",
	height: 36,
	boxSizing: "border-box",
	gap: 4,
	padding: "0 8px",
	borderRadius: "var(--mantine-radius-md)",
	border: "1px solid var(--mantine-color-default-border)",
	background: "var(--mantine-color-body)",
	cursor: "pointer",
	maxWidth: 180,
} as const;

// Active state keeps the same neutral border as the off-state (and the model selector) so the control row
// reads as one consistent line; agent-mode-ON is signalled by the light-primary fill + the colored users icon.
const triggerActiveStyle = {
	...triggerStyle,
	background: "var(--mantine-primary-color-light)",
} as const;

// Show a search box once the agent list exceeds this many items.
const AGENT_SEARCH_THRESHOLD = 5;

interface AgentOptionItemProps {
	agent: AgentOption;
	selected: boolean;
	onSelect: (id: string) => void;
}

function AgentOptionItem({ agent, selected, onSelect }: AgentOptionItemProps) {
	const { t } = useTranslation();
	const isOrchestrator = agent.kind === "Orchestrator";
	const hasPinnedModel = agent.modelProfile !== null && agent.modelProfile !== undefined && agent.modelProfile.trim().length > 0;

	return (
		<UnstyledButton
			data-testid={`chat-agent-selector-option-${agent.id}`}
			onClick={() => onSelect(agent.id)}
			style={{
				display: "block",
				width: "100%",
				padding: "6px 10px",
				borderRadius: "var(--mantine-radius-sm)",
				background: selected ? "var(--mantine-primary-color-light)" : undefined,
				cursor: "pointer",
			}}
		>
			<Group gap={6} wrap="nowrap" align="flex-start">
				<Stack gap={2} style={{ flex: 1, minWidth: 0 }}>
					<Group gap={6} wrap="nowrap">
						<Text size="sm" fw={600} lineClamp={1}>
							{agent.name}
						</Text>
						{isOrchestrator ? (
							<Badge size="xs" variant="light" color="orange">
								{t("pages.chat.agentSelector.orchestratorBadge", "Orchestrator")}
							</Badge>
						) : null}
					</Group>
					{agent.description.trim().length > 0 ? (
						<Text size="xs" c="dimmed" lineClamp={1}>
							{agent.description}
						</Text>
					) : null}
					{hasPinnedModel ? (
						<Text size="xs" c="dimmed" lineClamp={1}>
							{t("pages.chat.agentSelector.pinnedModelHint", "Uses model: {{model}}", { model: agent.modelProfile })}
						</Text>
					) : null}
				</Stack>
				<IconChevronRight
					size={12}
					color="var(--mantine-color-dimmed)"
					style={{ opacity: selected ? 1 : 0, flexShrink: 0, marginTop: 2 }}
				/>
			</Group>
		</UnstyledButton>
	);
}

interface AgentSelectorCardProps {
	// The filtered+sorted agent list derived in Chat.tsx (single derivation site). AgentSelectorCard is
	// purely presentational — it does NOT call useAgentDefinitions itself.
	agentOptions: readonly AgentOption[];
	// Whether agent mode is currently on. Combined with selectedAgentId this drives the active trigger state and
	// which row is checked. The merged control no longer has a separate on/off toggle: picking an agent enables
	// mode (onSelectAgent(id)); picking the "Default Assistant" row disables it (onSelectAgent("")).
	agentModeEnabled: boolean;
	selectedAgentId: string;
	disabled?: boolean;
	// "" => Off (Default Assistant, agent mode disabled). Any other id => enable mode + stamp that agent.
	onSelectAgent: (agentId: string) => void;
}

export function AgentSelectorCard({ agentOptions, agentModeEnabled, selectedAgentId, disabled = false, onSelectAgent }: AgentSelectorCardProps) {
	const { t } = useTranslation();
	const [pickerOpened, setPickerOpened] = useState(false);
	const [searchQuery, setSearchQuery] = useState("");

	// Stale/empty-selection reconcile: a live agent is "selected" only when mode is on AND the persisted id still
	// maps to an agent in the live list. Otherwise the control reads as Off (Default Assistant).
	const selectedAgent = agentModeEnabled ? agentOptions.find((agent) => agent.id === selectedAgentId) : undefined;
	const isOff = selectedAgent === undefined;

	const defaultAgentLabel = t("pages.chat.defaultAgentName", "Default Assistant");
	const triggerLabel = selectedAgent?.name ?? defaultAgentLabel;
	const hasOptions = agentOptions.length > 0;
	const isDisabled = disabled || !hasOptions;
	const showSearch = agentOptions.length > AGENT_SEARCH_THRESHOLD;

	const filtered = useMemo(() => {
		const query = searchQuery.trim().toLowerCase();
		if (!query) {
			return agentOptions;
		}

		return agentOptions.filter(
			(agent) => agent.name.toLowerCase().includes(query) || agent.description.toLowerCase().includes(query),
		);
	}, [agentOptions, searchQuery]);

	const closePicker = (): void => {
		setPickerOpened(false);
		setSearchQuery("");
	};

	const select = (agentId: string): void => {
		onSelectAgent(agentId);
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
				<Paper radius="md" data-testid="chat-agent-selector-selected" style={isOff ? triggerStyle : triggerActiveStyle}>
					<UnstyledButton
						type="button"
						data-testid="chat-agent-selector-trigger"
						disabled={isDisabled}
						onClick={() => {
							if (!isDisabled) {
								setPickerOpened((previous) => !previous);
							}
						}}
						aria-disabled={isDisabled}
						aria-expanded={pickerOpened}
						aria-label={t("pages.chat.agentSelector.triggerLabel", "Agent")}
						style={{ width: "100%" }}
					>
						<Group gap="xs" wrap="nowrap" align="center">
							<IconUsers
								size={16}
								color={isOff ? "var(--mantine-color-dimmed)" : "var(--mantine-primary-color-filled)"}
								style={{ flexShrink: 0 }}
							/>
							<Text size="xs" fw={600} lineClamp={1} style={{ flex: 1, minWidth: 0 }}>
								{triggerLabel}
							</Text>
							<IconChevronDown size={12} color="var(--mantine-color-dimmed)" />
						</Group>
					</UnstyledButton>
				</Paper>
			</Popover.Target>
			<Popover.Dropdown p={6}>
				<Stack gap={2}>
					{showSearch ? (
						<Box px="xs" pt={4} pb={2}>
							<TextInput
								placeholder={t("pages.chat.agentSelector.search", "Search agents...")}
								size="xs"
								leftSection={<IconSearch size={14} />}
								value={searchQuery}
								onChange={(event) => setSearchQuery(event.currentTarget.value)}
								data-testid="chat-agent-selector-search"
							/>
						</Box>
					) : null}
					{/* Off / Default Assistant row — picking it disables agent mode (onSelectAgent("")). Always shown so the
					    user can return to the node default in one click without a separate toggle. */}
					<UnstyledButton
						data-testid="chat-agent-selector-option-off"
						onClick={() => select("")}
						style={{
							display: "block",
							width: "100%",
							padding: "6px 10px",
							borderRadius: "var(--mantine-radius-sm)",
							background: isOff ? "var(--mantine-primary-color-light)" : undefined,
							cursor: "pointer",
						}}
					>
						<Group gap={6} wrap="nowrap" align="flex-start">
							<Stack gap={2} style={{ flex: 1, minWidth: 0 }}>
								<Text size="sm" fw={600} lineClamp={1}>
									{t("pages.chat.defaultAgentName", "Default Assistant")}
								</Text>
								<Text size="xs" c="dimmed" lineClamp={1}>
									{t("pages.chat.agentSelector.offOptionHint", "No agent — uses the node default")}
								</Text>
							</Stack>
							<IconCheck
								size={12}
								color="var(--mantine-primary-color-filled)"
								style={{ opacity: isOff ? 1 : 0, flexShrink: 0, marginTop: 2 }}
							/>
						</Group>
					</UnstyledButton>
					<Divider my={4} />
					<ScrollArea.Autosize mah={320} type="hover" offsetScrollbars={true}>
						<Stack gap={2}>
							{filtered.map((agent) => (
								<AgentOptionItem key={agent.id} agent={agent} selected={agent.id === selectedAgentId && !isOff} onSelect={select} />
							))}
							{filtered.length === 0 ? (
								<Text size="sm" c="dimmed" px="sm" py="xs" ta="center">
									{t("pages.chat.agentSelector.noResults", "No agents found")}
								</Text>
							) : null}
						</Stack>
					</ScrollArea.Autosize>
					<Divider my={4} />
					<Text size="xs" c="dimmed" px="sm" py={4} lh={1.4}>
						{t("pages.chat.agentSelector.hint", "Choose an agent defined on this node.")}
					</Text>
				</Stack>
			</Popover.Dropdown>
		</Popover>
	);
}
