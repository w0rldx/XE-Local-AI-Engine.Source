import { Alert, Badge, Button, Divider, Group, Loader, Select, Stack, Text, Title } from "@mantine/core";
import { IconAlertTriangle, IconLink, IconPlayerPlay, IconX } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import type { DevelopmentProjectDetail, DevelopmentTaskDetail } from "@/features/development/models/DevelopmentModels";
import { nextActionStatuses, statusColor } from "@/features/development/models/DevelopmentStatusModel";

type Project = NonNullable<DevelopmentProjectDetail["project"]>;
type Task = NonNullable<DevelopmentTaskDetail["task"]>;

interface DevelopmentProjectOverviewProps {
	readonly project: Project;
	readonly task: Task;
	readonly repository: {
		readonly connectedAlias?: string;
		readonly connectionRequired: boolean;
		readonly loading: boolean;
		readonly ready: boolean;
	};
	readonly reconnect: {
		readonly error?: string;
		readonly folderId: string | null;
		readonly loading: boolean;
		readonly options: readonly { value: string; label: string }[];
		readonly run: () => void;
		readonly select: (value: string | null) => void;
	};
	readonly attempt: { readonly active: boolean; readonly cancel: () => void; readonly canceling: boolean };
	readonly nextAction: {
		readonly error?: string;
		readonly label: readonly [string, string];
		readonly run: () => void;
		readonly running: boolean;
	};
}

export function DevelopmentProjectOverview({
	project,
	task,
	repository,
	reconnect,
	attempt,
	nextAction,
}: DevelopmentProjectOverviewProps) {
	const { t } = useTranslation();
	return (
		<SectionCard>
			<Group align="flex-start" justify="space-between">
				<div>
					<Title order={2}>{project.objective}</Title>
					<Text c="dimmed">
						{project.baseBranch} · {project.egressPolicy} ·{" "}
						{repository.connectedAlias ?? t("pages.development.repositoryNotConnected", "Repository not connected")}
					</Text>
				</div>
				<Badge color={statusColor(task.status)}>{task.status}</Badge>
			</Group>
			<Divider />
			<Stack gap="xs">
				<Title order={3}>{task.title}</Title>
				<Text>{task.requirements}</Text>
			</Stack>
			{repository.connectionRequired ? (
				<Alert color="yellow" data-testid="development-reconnect-panel" icon={<IconLink size={16} />}>
					<Stack gap="sm">
						<Text>
							{t(
								"pages.development.reconnect.description",
								"This existing project must be reconnected to its original registered repository before actions can run.",
							)}
						</Text>
						<Group align="end">
							<Select
								data={reconnect.options}
								data-testid="development-reconnect-select"
								label={t("pages.development.reconnect.repository", "Original repository")}
								loading={repository.loading}
								onChange={reconnect.select}
								value={reconnect.folderId}
							/>
							<Button
								data-testid="development-reconnect-repository"
								disabled={!reconnect.folderId}
								leftSection={<IconLink size={16} />}
								loading={reconnect.loading}
								onClick={reconnect.run}
							>
								{t("pages.development.reconnect.submit", "Reconnect repository")}
							</Button>
						</Group>
						{reconnect.error ? (
							<Text c="red" size="sm">
								{reconnect.error}
							</Text>
						) : null}
					</Stack>
				</Alert>
			) : repository.loading ? (
				<Group gap="sm">
					<Loader size="sm" />
					<Text c="dimmed">{t("pages.development.loading.repositories", "Loading registered Development repositories")}</Text>
				</Group>
			) : !repository.ready ? (
				<Alert color="red" icon={<IconAlertTriangle size={16} />}>
					{t(
						"pages.development.repositoryUnavailableDescription",
						"The registered repository is unavailable or no longer matches this project. Development actions are blocked.",
					)}
				</Alert>
			) : null}
			<Group align="end">
				{nextActionStatuses.has(task.status ?? "") ? (
					<Button
						data-testid="development-start-next"
						disabled={!repository.ready || attempt.active}
						leftSection={<IconPlayerPlay size={16} />}
						loading={nextAction.running}
						onClick={nextAction.run}
					>
						{t(nextAction.label[0], nextAction.label[1])}
					</Button>
				) : null}
				{attempt.active ? (
					<Button
						color="red"
						data-testid="development-cancel-attempt"
						leftSection={<IconX size={16} />}
						loading={attempt.canceling}
						onClick={attempt.cancel}
						variant="light"
					>
						{t("pages.development.cancelAttempt", "Cancel attempt")}
					</Button>
				) : null}
			</Group>
			{nextAction.error ? <Alert color="red">{nextAction.error}</Alert> : null}
			{task.blockedReason ? <Alert color="red">{task.blockedReason}</Alert> : null}
		</SectionCard>
	);
}
