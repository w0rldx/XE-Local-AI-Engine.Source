import { ActionIcon, Alert, Badge, Drawer, Group, Menu, Stack, Text, Tooltip } from "@mantine/core";
import {
	IconAlertTriangle,
	IconDotsVertical,
	IconLayoutSidebar,
	IconLayoutSidebarRight,
	IconPencil,
	IconTrash,
} from "@tabler/icons-react";
import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";

import { FullHeightPage } from "@/core/ui/components/FullHeightPage/FullHeightPage";

interface WorkSessionDetailLayoutProps {
	readonly title: string;
	readonly kindLabel: string;
	readonly isMobile: boolean;
	readonly deleteError?: string;
	readonly planDrawerOpened: boolean;
	readonly sideDrawerOpened: boolean;
	readonly onOpenPlan: () => void;
	readonly onClosePlan: () => void;
	readonly onOpenSide: () => void;
	readonly onCloseSide: () => void;
	readonly onEdit: () => void;
	readonly onDelete: () => void;
	readonly planPanel: ReactNode;
	readonly sidePanel: ReactNode;
	readonly conversationPane: ReactNode;
	readonly editDialog: ReactNode;
}

export function WorkSessionDetailLayout(props: WorkSessionDetailLayoutProps) {
	const { t } = useTranslation();
	return (
		<FullHeightPage data-testid="work-session-detail-page">
			<Stack gap="sm" h="100%" style={{ minHeight: 0 }}>
				<Group gap="xs" wrap="nowrap">
					{props.isMobile ? (
						<Tooltip label={t("pages.workSessions.detail.showPlan", "Show plan")}>
							<ActionIcon
								variant="subtle"
								onClick={props.onOpenPlan}
								aria-label={t("pages.workSessions.detail.showPlan", "Show plan")}
								data-testid="work-session-plan-toggle"
							>
								<IconLayoutSidebar size={18} />
							</ActionIcon>
						</Tooltip>
					) : null}
					<Text fw={700} lineClamp={1} style={{ flex: 1, minWidth: 0 }} data-testid="work-session-title">
						{props.title}
					</Text>
					<Badge size="sm" variant="light" color="gray">
						{props.kindLabel}
					</Badge>
					<Menu position="bottom-end" withinPortal={true}>
						<Menu.Target>
							<ActionIcon
								variant="subtle"
								aria-label={t("pages.workSessions.detail.actions", "Session actions")}
								data-testid="work-session-actions"
							>
								<IconDotsVertical size={18} />
							</ActionIcon>
						</Menu.Target>
						<Menu.Dropdown>
							<Menu.Item leftSection={<IconPencil size={14} />} onClick={props.onEdit} data-testid="work-session-edit">
								{t("pages.workSessions.edit.open", "Edit")}
							</Menu.Item>
							<Menu.Item
								color="red"
								leftSection={<IconTrash size={14} />}
								onClick={props.onDelete}
								data-testid="work-session-delete"
							>
								{t("pages.workSessions.delete.open", "Delete")}
							</Menu.Item>
						</Menu.Dropdown>
					</Menu>
					{props.isMobile ? (
						<Tooltip label={t("pages.workSessions.detail.showDetails", "Show findings and artifacts")}>
							<ActionIcon
								variant="subtle"
								onClick={props.onOpenSide}
								aria-label={t("pages.workSessions.detail.showDetails", "Show findings and artifacts")}
								data-testid="work-session-side-toggle"
							>
								<IconLayoutSidebarRight size={18} />
							</ActionIcon>
						</Tooltip>
					) : null}
				</Group>
				{props.deleteError ? (
					<Alert color="red" variant="light" icon={<IconAlertTriangle size={16} />} data-testid="work-session-delete-error">
						{props.deleteError}
					</Alert>
				) : null}
				{props.editDialog}
				{props.isMobile ? (
					<>
						<div style={{ flex: 1, minHeight: 0 }}>{props.conversationPane}</div>
						<Drawer
							opened={props.planDrawerOpened}
							onClose={props.onClosePlan}
							position="left"
							size="85%"
							title={t("pages.workSessions.plan.title", "Plan")}
							attributes={{ content: { "data-testid": "work-session-plan-drawer" } }}
						>
							{props.planPanel}
						</Drawer>
						<Drawer
							opened={props.sideDrawerOpened}
							onClose={props.onCloseSide}
							position="right"
							size="85%"
							title={t("pages.workSessions.detail.details", "Details")}
							attributes={{ content: { "data-testid": "work-session-side-drawer" } }}
						>
							{props.sidePanel}
						</Drawer>
					</>
				) : (
					<div
						data-testid="work-session-detail-grid"
						// Same three-column template, same floor, for the same reason as the dev-workflow run page:
						// between 1024 and roughly 1180 the unfloored centre track was squeezed under its own chrome.
						// A message thread degrades more gracefully than a tab header, so it read as cramped rather
						// than broken — the geometry was identical either way.
						style={{
							display: "grid",
							gridTemplateColumns: "320px minmax(240px, 1fr) minmax(380px, 420px)",
							gridTemplateRows: "minmax(0, 1fr)",
							gap: "var(--mantine-spacing-md)",
							flex: 1,
							minHeight: 0,
							overflowX: "auto",
						}}
					>
						{props.planPanel}
						{props.conversationPane}
						{props.sidePanel}
					</div>
				)}
			</Stack>
		</FullHeightPage>
	);
}
