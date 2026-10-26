import { Badge, Group, Menu, Text, Tooltip, UnstyledButton } from "@mantine/core";
import { IconCheck, IconChevronDown, IconSitemap } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import classes from "@/features/chat/components/ChatInputArea/ChatComposerToolbar.module.css";

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
	const currentLabel = t("pages.chat.workflow.selector.triggerCurrent", "Workflow: {{name}}", {
		name: selected?.name ?? noneLabel,
	});
	const chatBadge = t("pages.graphWorkflows.settings.kindOption.Chat", "Chat");

	return (
		<Menu position="top-start" offset={4} withinPortal={true} shadow="md" width={260} disabled={disabled}>
			<Menu.Target>
				{/* Same trigger as the agent picker, folding with it on a narrow composer. The tooltip carries the current
				    choice, which is all a folded trigger shows. */}
				<Tooltip
					label={currentLabel}
					withinPortal={true}
					openDelay={350}
					multiline={true}
					maw={300}
					events={{ hover: true, focus: true, touch: false }}
				>
					<UnstyledButton
						type="button"
						className={classes["select-trigger"]}
						data-active={selected ? "true" : undefined}
						disabled={disabled}
						aria-disabled={disabled}
						aria-label={currentLabel}
						data-testid="chat-workflow-selector-trigger"
					>
						<Group gap="xs" wrap="nowrap" align="center" className={classes["select-content"]}>
							<IconSitemap
								size={16}
								color={selected ? "var(--mantine-primary-color-filled)" : "var(--mantine-color-dimmed)"}
								style={{ flexShrink: 0 }}
							/>
							<Text size="xs" fw={600} truncate="end" className={classes["select-label"]}>
								{selected?.name ?? noneLabel}
							</Text>
							<IconChevronDown size={12} color="var(--mantine-color-dimmed)" className={classes["select-chevron"]} />
						</Group>
					</UnstyledButton>
				</Tooltip>
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
								{/* Wraps rather than clamps: the menu keeps its own width, so a long name stays readable in full. */}
								<Text size="sm" fw={600} style={{ minWidth: 0, overflowWrap: "anywhere" }}>
									{option.name}
								</Text>
								<Badge size="xs" variant="light" style={{ flexShrink: 0 }}>
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
