import { Alert, Badge, Button, Card, Group, SimpleGrid, Skeleton, Stack, Text } from "@mantine/core";
import { IconAlertTriangle, IconBriefcase, IconPlus } from "@tabler/icons-react";
import { useNavigate } from "@tanstack/react-router";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { CreateWorkSessionDialog } from "@/features/workSessions/components/CreateWorkSessionDialog";
import { WorkSessionStatusBadge } from "@/features/workSessions/components/WorkSessionStatusBadge";
import { useWorkSessionAgentOptions } from "@/features/workSessions/hooks/useWorkSessionAgentOptions";
import { toWorkSessionKind, toWorkSessionStatus } from "@/features/workSessions/models/WorkSessionModels";
import { useCreateWorkSession, useWorkSessionList } from "@/features/workSessions/queries/useWorkSessions";

export function WorkSessionsPage() {
	const { t } = useTranslation();
	const navigate = useNavigate();
	const [dialogOpened, setDialogOpened] = useState(false);
	const listQuery = useWorkSessionList();
	const createMutation = useCreateWorkSession();
	const { options: agentOptions } = useWorkSessionAgentOptions();

	const sessions = listQuery.data?.items ?? [];

	return (
		<PageShell data-testid="work-sessions-page">
			<PageHeader
				title={t("pages.workSessions.title", "Work Sessions")}
				icon={<IconBriefcase size={24} />}
				subtitle={t("pages.workSessions.subtitle", "Long-running agent work with its own plan, findings and artifacts.")}
				actions={
					<Button leftSection={<IconPlus size={16} />} onClick={() => setDialogOpened(true)} data-testid="work-sessions-create">
						{t("pages.workSessions.create.open", "New work session")}
					</Button>
				}
			/>

			{listQuery.isPending ? (
				<SimpleGrid cols={{ base: 1, sm: 2, lg: 3 }} data-testid="work-sessions-loading">
					<Skeleton height={120} radius="md" />
					<Skeleton height={120} radius="md" />
					<Skeleton height={120} radius="md" />
				</SimpleGrid>
			) : listQuery.isError ? (
				<Alert color="red" variant="light" icon={<IconAlertTriangle size={16} />} data-testid="work-sessions-error">
					<Stack gap="sm" align="flex-start">
						<Text size="sm">{apiErrorMessage(listQuery.error, t("pages.workSessions.loadFailed", "Could not load work sessions."))}</Text>
						<Button size="xs" variant="light" onClick={() => {
									listQuery.refetch().catch(() => undefined);
								}} data-testid="work-sessions-retry">
							{t("pages.workSessions.retry", "Retry")}
						</Button>
					</Stack>
				</Alert>
			) : sessions.length === 0 ? (
				<Alert color="blue" variant="light" data-testid="work-sessions-empty">
					<Stack gap="sm" align="flex-start">
						<Text size="sm">
							{t("pages.workSessions.empty", "No work sessions yet. Give an agent an objective and it will plan and run the work.")}
						</Text>
						<Button size="xs" onClick={() => setDialogOpened(true)} data-testid="work-sessions-empty-create">
							{t("pages.workSessions.create.first", "Create your first work session")}
						</Button>
					</Stack>
				</Alert>
			) : (
				<SimpleGrid cols={{ base: 1, sm: 2, lg: 3 }} data-testid="work-sessions-list">
					{sessions.map((session) => (
						<Card
							key={session.id}
							withBorder={true}
							padding="md"
							data-testid={`work-session-card-${session.id}`}
							onClick={() => {
									navigate({ to: "/work-sessions/$sessionId", params: { sessionId: session.id ?? "" } });
								}}
							style={{ cursor: "pointer" }}
						>
							<Stack gap="xs">
								<Text fw={600} lineClamp={2}>
									{session.title}
								</Text>
								<Group gap="xs" wrap="wrap">
									<WorkSessionStatusBadge status={toWorkSessionStatus(session.status)} testId={`work-session-card-status-${session.id}`} />
									<Badge size="sm" variant="light" color="gray">
										{t(`pages.workSessions.kind.${toWorkSessionKind(session.kind)}`, session.kind ?? "")}
									</Badge>
								</Group>
								<Text size="xs" c="dimmed">
									{t("pages.workSessions.card.meta", "{{steps}} steps · updated {{updated}}", {
										steps: session.stepCount ?? 0,
										updated: new Date(session.updatedAtUtc ?? 0).toLocaleString(),
									})}
								</Text>
							</Stack>
						</Card>
					))}
				</SimpleGrid>
			)}

			<CreateWorkSessionDialog
				opened={dialogOpened}
				agentOptions={agentOptions}
				isSubmitting={createMutation.isPending}
				errorMessage={
					createMutation.isError
						? apiErrorMessage(createMutation.error, t("pages.workSessions.create.failed", "Could not create the work session."))
						: undefined
				}
				onClose={() => {
					createMutation.reset();
					setDialogOpened(false);
				}}
				onSubmit={(values) => {
					createMutation.mutate(
						{ body: values },
						{
							onSuccess: (created) => {
								setDialogOpened(false);
								navigate({ to: "/work-sessions/$sessionId", params: { sessionId: created.id ?? "" } });
							},
						},
					);
				}}
			/>
		</PageShell>
	);
}
