import { ActionIcon, Badge, Group, Paper, Stack, Switch, Text } from "@mantine/core";
import { IconArrowDown, IconArrowRight, IconArrowUp, IconFlag, IconPencil, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import {
	memoryScopeColors,
	memoryScopeFallbacks,
	monitorStatusColors,
	monitorStatusFallbacks,
	sourceFallbacks,
} from "@/features/agents/components/PlaybookActionDisplay";
import type { PlaybookAction } from "@/features/agents/models/PlaybookActionModels";
import type { PlaybookMonitorItem } from "@/features/agents/models/PlaybookMonitorModels";

// Render a down-vote rate fraction (0..1) as a whole-percent string for the before→after signal.
function toDownRatePercent(rate: number): string {
	return `${Math.round(rate * 100)}%`;
}

interface MonitorSignalProps {
	actionId: string;
	// The monitoring signal for this Enabled action, or null when there is no signal yet (no enable clock / read
	// failed) — in which case a neutral "—" placeholder renders.
	item: PlaybookMonitorItem | null;
}

// The cohort-monitoring signal for one Enabled action: a status badge (Improved / Flat / Regressed /
// Insufficient data), a compact before→after down-rate (e.g. "12% → 5%"), and a flag marker for operator review
// when the action is flagged (dead/harmful — coarse, agent-level signal; the operator decides, never auto-disabled).
// An action with no monitor item renders a neutral placeholder so the row reads consistently.
function MonitorSignal({ actionId, item }: MonitorSignalProps) {
	const { t } = useTranslation();

	if (item === null) {
		return (
			<Text size="xs" c="dimmed" data-testid={`playbook-monitor-none-${actionId}`}>
				{t("pages.agents.playbook.monitor.none", "—")}
			</Text>
		);
	}

	return (
		<Group gap="xs" align="center" wrap="wrap" data-testid={`playbook-monitor-${actionId}`}>
			<Badge
				size="xs"
				variant="light"
				color={monitorStatusColors[item.status]}
				data-testid={`playbook-monitor-status-${actionId}`}
			>
				{t(`pages.agents.playbook.monitor.status.${item.status}`, monitorStatusFallbacks[item.status])}
			</Badge>
			<Group gap={4} align="center" wrap="nowrap" data-testid={`playbook-monitor-rate-${actionId}`}>
				<Text size="xs" c="dimmed">
					{toDownRatePercent(item.beforeDownRate)}
				</Text>
				<IconArrowRight size={12} aria-hidden={true} />
				<Text size="xs" c="dimmed">
					{toDownRatePercent(item.afterDownRate)}
				</Text>
				<Text size="xs" c="dimmed">
					{t("pages.agents.playbook.monitor.downRateLabel", "down-vote rate")}
				</Text>
			</Group>
			{item.facetToolName ? (
				<Badge size="xs" variant="outline" color="gray" data-testid={`playbook-monitor-facet-${actionId}`}>
					{item.facetToolName}
				</Badge>
			) : null}
			{item.flagged ? (
				<Badge
					size="xs"
					variant="filled"
					color="orange"
					leftSection={<IconFlag size={10} />}
					data-testid={`playbook-monitor-flag-${actionId}`}
				>
					{t("pages.agents.playbook.monitor.flag", "Needs review")}
				</Badge>
			) : null}
		</Group>
	);
}

interface PlaybookActionRowProps {
	action: PlaybookAction;
	index: number;
	isFirst: boolean;
	isLast: boolean;
	isMutating: boolean;
	// The cohort-monitoring signal for this row (joined by id), or null when the action is Disabled / has no signal.
	monitorItem: PlaybookMonitorItem | null;
	onMove: (index: number, direction: "up" | "down") => void;
	onEdit: (id: string) => void;
	onDelete: (action: PlaybookAction) => void;
	onToggleState: (action: PlaybookAction, nextEnabled: boolean) => void;
}

// One manual (Enabled/Disabled) playbook action row: provenance + scope badges, behavior + trigger, reorder/edit/delete
// controls, the enable toggle, and the cohort-monitoring signal for Enabled actions.
export function PlaybookActionRow({
	action,
	index,
	isFirst,
	isLast,
	isMutating,
	monitorItem,
	onMove,
	onEdit,
	onDelete,
	onToggleState,
}: PlaybookActionRowProps) {
	const { t } = useTranslation();

	const isEnabled = action.state === "Enabled";
	// A Failure-scope memory is negative guidance ("don't do X"); flag the row with a red border so it
	// reads distinctly from positive/procedural memories.
	const isFailureScope = action.memoryScope === "Failure";

	return (
		<Paper
			withBorder={true}
			p="xs"
			style={isFailureScope ? { borderColor: "var(--mantine-color-red-4)" } : undefined}
			data-testid={`playbook-action-${action.id}`}
		>
			<Stack gap={6}>
				<Group justify="space-between" align="flex-start" wrap="nowrap">
					<Stack gap={4} style={{ flex: 1, minWidth: 0 }}>
						<Group gap="xs" align="center" wrap="wrap">
							<Badge size="xs" variant="light" color="grape" data-testid={`playbook-source-${action.id}`}>
								{t(`pages.agents.playbook.source.${action.source}`, sourceFallbacks[action.source])}
							</Badge>
							{action.memoryScope ? (
								<Badge
									size="xs"
									variant="light"
									color={memoryScopeColors[action.memoryScope]}
									data-testid={`playbook-scope-${action.id}`}
								>
									{t(`pages.agents.playbook.scope.${action.memoryScope}`, memoryScopeFallbacks[action.memoryScope])}
								</Badge>
							) : null}
							{action.scope ? (
								<Badge size="xs" variant="outline" color="gray">
									{action.scope}
								</Badge>
							) : null}
							<Text size="xs" c="dimmed">
								{t("pages.agents.playbook.priorityLabel", "Priority {{priority}}", {
									priority: action.priority,
								})}
							</Text>
						</Group>
						<Text size="sm">{action.behavior}</Text>
						{action.triggerCondition ? (
							<Text size="xs" c="dimmed">
								{t("pages.agents.playbook.triggerLabel", "When: {{trigger}}", {
									trigger: action.triggerCondition,
								})}
							</Text>
						) : null}
					</Stack>
					<Group gap={4} wrap="nowrap">
						<ActionIcon
							aria-label={t("pages.agents.playbook.moveUpAria", "Move up")}
							variant="subtle"
							size="sm"
							disabled={isMutating || isFirst}
							onClick={() => onMove(index, "up")}
							data-testid={`playbook-move-up-${action.id}`}
						>
							<IconArrowUp size={14} />
						</ActionIcon>
						<ActionIcon
							aria-label={t("pages.agents.playbook.moveDownAria", "Move down")}
							variant="subtle"
							size="sm"
							disabled={isMutating || isLast}
							onClick={() => onMove(index, "down")}
							data-testid={`playbook-move-down-${action.id}`}
						>
							<IconArrowDown size={14} />
						</ActionIcon>
						<ActionIcon
							aria-label={t("pages.agents.playbook.editAria", "Edit action")}
							variant="subtle"
							size="sm"
							disabled={isMutating}
							onClick={() => onEdit(action.id)}
							data-testid={`playbook-edit-${action.id}`}
						>
							<IconPencil size={14} />
						</ActionIcon>
						<ActionIcon
							aria-label={t("pages.agents.playbook.deleteAria", "Delete action")}
							variant="subtle"
							color="red"
							size="sm"
							disabled={isMutating}
							onClick={() => onDelete(action)}
							data-testid={`playbook-delete-${action.id}`}
						>
							<IconTrash size={14} />
						</ActionIcon>
					</Group>
				</Group>
				<Switch
					size="sm"
					checked={isEnabled}
					disabled={isMutating}
					label={
						<Badge size="xs" variant="light" color={isEnabled ? "teal" : "gray"}>
							{isEnabled
								? t("pages.agents.playbook.state.enabled", "enabled")
								: t("pages.agents.playbook.state.disabled", "disabled")}
						</Badge>
					}
					onChange={(event) => onToggleState(action, event.currentTarget.checked)}
					data-testid={`playbook-toggle-${action.id}`}
				/>
				{isEnabled ? <MonitorSignal actionId={action.id} item={monitorItem} /> : null}
			</Stack>
		</Paper>
	);
}
