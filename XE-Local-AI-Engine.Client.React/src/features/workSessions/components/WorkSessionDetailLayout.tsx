import { ActionIcon, Badge, Drawer, Group, Menu, Stack, Text, Tooltip } from "@mantine/core";
import { IconDotsVertical, IconLayoutSidebar, IconLayoutSidebarRight, IconPencil, IconTrash } from "@tabler/icons-react";
import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";

import { FullHeightPage } from "@/core/ui/components/FullHeightPage/FullHeightPage";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { ResponsivePaneLayout } from "@/core/ui/components/ResponsivePaneLayout/ResponsivePaneLayout";

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
					<InlineErrorAlert message={props.deleteError} variant="light" data-testid="work-session-delete-error" />
				) : null}
				{props.editDialog}
				{/* Both side surfaces reach a phone through the header's two toggles, so the narrow viewport keeps the
				    conversation alone rather than stacking anything above it. */}
				<ResponsivePaneLayout
					narrowMode="mainOnly"
					list={props.planPanel}
					main={props.conversationPane}
					side={props.sidePanel}
					gridTestId="work-session-detail-grid"
				/>
				{props.isMobile ? (
					<>
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
				) : null}
			</Stack>
		</FullHeightPage>
	);
}
