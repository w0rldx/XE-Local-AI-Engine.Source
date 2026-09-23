import { Badge, Group, Menu, Text, UnstyledButton } from "@mantine/core";
import { IconCheck, IconChevronDown, IconSitemap } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

// Same compact trigger as AgentSelectorCard, so the composer's control row stays one even height.
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
	maxWidth: 200,
} as const;

const triggerActiveStyle = { ...triggerStyle, background: "var(--mantine-primary-color-light)" } as const;

export interface WorkflowSelectorOption {
	readonly id: string;
	readonly name: string;
	readonly description?: string | null;
}

interface WorkflowSelectorCardProps {
	/** Chat-kind definitions only; the caller filters. */
	readonly options: readonly WorkflowSelectorOption[];
	/** `""` = no workflow (normal chat). */
	readonly selectedDefinitionId: string;
	readonly disabled?: boolean;
	readonly onSelect: (definitionId: string) => void;
}

/** Picks the Chat workflow a conversation's messages go to. "No workflow" leaves the composer on normal chat. */
export function WorkflowSelectorCard({ options, selectedDefinitionId, disabled = false, onSelect }: WorkflowSelectorCardProps) {
	const { t } = useTranslation();
	const selected = options.find((option) => option.id === selectedDefinitionId);
	const noneLabel = t("pages.chat.workflow.selector.none", "No workflow");
	const chatBadge = t("pages.graphWorkflows.settings.kindOption.Chat", "Chat");

	return (
		<Menu position="top-start" offset={4} withinPortal={true} shadow="md" width={260} disabled={disabled}>
			<Menu.Target>
				<UnstyledButton
					type="button"
					style={selected ? triggerActiveStyle : triggerStyle}
					disabled={disabled}
					aria-disabled={disabled}
					aria-label={t("pages.chat.workflow.selector.triggerLabel", "Workflow")}
					data-testid="chat-workflow-selector-trigger"
				>
					<Group gap="xs" wrap="nowrap" align="center">
						<IconSitemap
							size={16}
							color={selected ? "var(--mantine-primary-color-filled)" : "var(--mantine-color-dimmed)"}
							style={{ flexShrink: 0 }}
						/>
						<Text size="xs" fw={600} lineClamp={1} style={{ flex: 1, minWidth: 0 }}>
							{selected?.name ?? noneLabel}
						</Text>
						<IconChevronDown size={12} color="var(--mantine-color-dimmed)" />
					</Group>
				</UnstyledButton>
			</Menu.Target>
			<Menu.Dropdown data-testid="chat-workflow-selector-dropdown">
				<Menu.Item
					onClick={() => onSelect("")}
					rightSection={selected ? null : <IconCheck size={12} />}
					data-testid="chat-workflow-selector-option-none"
				>
					<Text size="sm" fw={600}>
						{noneLabel}
					</Text>
					<Text size="xs" c="dimmed">
						{t("pages.chat.workflow.selector.noneHint", "Messages go to the model or agent as usual")}
					</Text>
				</Menu.Item>
				<Menu.Divider />
				{options.length === 0 ? (
					<Text size="xs" c="dimmed" px="sm" py={6} data-testid="chat-workflow-selector-empty">
						{t("pages.chat.workflow.selector.empty", "No chat workflows yet. Create one on the Graph Workflows page.")}
					</Text>
				) : (
					options.map((option) => (
						<Menu.Item
							key={option.id}
							onClick={() => onSelect(option.id)}
							rightSection={option.id === selectedDefinitionId ? <IconCheck size={12} /> : null}
							data-testid={`chat-workflow-selector-option-${option.id}`}
						>
							<Group gap={6} wrap="nowrap">
								<Text size="sm" fw={600} lineClamp={1}>
									{option.name}
								</Text>
								<Badge size="xs" variant="light">
									{chatBadge}
								</Badge>
							</Group>
							{option.description ? (
								<Text size="xs" c="dimmed" lineClamp={1}>
									{option.description}
								</Text>
							) : null}
						</Menu.Item>
					))
				)}
			</Menu.Dropdown>
		</Menu>
	);
}
