import {
	Accordion,
	Alert,
	Badge,
	Button,
	Code,
	Container,
	Divider,
	Grid,
	Group,
	Loader,
	Paper,
	ScrollArea,
	Stack,
	Table,
	Text,
	Textarea,
	TextInput,
	Title,
} from "@mantine/core";
import { IconAlertCircle, IconCheck, IconGitPullRequest, IconPlayerPlay, IconRefresh, IconX } from "@tabler/icons-react";
import { useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { DevelopmentLivePanel } from "@/features/development/components/DevelopmentLivePanel";
import {
	DevelopmentProjectForm,
	type DevelopmentProjectFormValues,
} from "@/features/development/components/DevelopmentProjectForm";
import { useDevelopmentAttemptHub } from "@/features/development/hooks/useDevelopmentAttemptHub";
import { isActiveAttempt } from "@/features/development/models/DevelopmentModels";
import {
	useApplyDevelopmentPatch,
	useCancelDevelopmentAttempt,
	useCreateDevelopmentProject,
	useDevelopmentProject,
	useDevelopmentProjects,
	usePreviewDevelopmentPatch,
	useStartDevelopmentNextAction,
} from "@/features/development/queries/useDevelopment";

const nextActionStatuses = new Set(["Planned", "Ready", "InProgress", "ChangesRequested", "InReview"]);

function operationId(): string {
	return globalThis.crypto.randomUUID();
}

function errorMessage(error: unknown, fallback: string): string {
	return error instanceof Error ? error.message : fallback;
}

function statusColor(status?: string): string {
	if (status === "Completed" || status === "Succeeded" || status === "AwaitingApply") {
		return "green";
	}
	if (status === "Failed" || status === "Blocked" || status === "Cancelled") {
		return "red";
	}
	if (status === "Interrupted" || status === "ChangesRequested") {
		return "yellow";
	}
	return "blue";
}

function nextActionLabel(status: string | undefined, latestAttemptStatus: string | undefined): string {
	if (latestAttemptStatus === "Interrupted") {
		return "Start replacement attempt";
	}
	if (status === "InReview") {
		return "Start independent review";
	}
	if (status === "InProgress" && latestAttemptStatus === "Succeeded") {
		return "Run deterministic validation";
	}
	if (status === "ChangesRequested") {
		return "Start coder revision";
	}
	return "Start next action";
}

export function DevelopmentPage() {
	const { t } = useTranslation();
	const projectsQuery = useDevelopmentProjects();
	const [selectedProjectId, setSelectedProjectId] = useState<string | null>(null);
	const [repositoryRoot, setRepositoryRoot] = useState("");
	const [previewTaskId, setPreviewTaskId] = useState<string | null>(null);
	const projectQuery = useDevelopmentProject(selectedProjectId);
	const createMutation = useCreateDevelopmentProject();
	const startMutation = useStartDevelopmentNextAction();
	const cancelMutation = useCancelDevelopmentAttempt();
	const previewMutation = usePreviewDevelopmentPatch();
	const applyMutation = useApplyDevelopmentPatch();

	const projects = useMemo(() => projectsQuery.data ?? [], [projectsQuery.data]);
	useEffect(() => {
		if (!selectedProjectId && projects[0]?.id) {
			setSelectedProjectId(projects[0].id);
		}
	}, [projects, selectedProjectId]);

	const detail = projectQuery.data;
	const taskDetail = detail?.tasks?.[0];
	const task = taskDetail?.task;
	const attempts = taskDetail?.attempts ?? [];
	const artifacts = taskDetail?.artifacts ?? [];
	const events = detail?.events ?? [];
	const latestAttempt = attempts.at(-1) ?? null;
	const activeAttempt = attempts.find(isActiveAttempt) ?? null;
	const live = useDevelopmentAttemptHub(detail?.project?.id ?? null, task?.id ?? null, activeAttempt?.id ?? null);

	const createProject = (values: DevelopmentProjectFormValues): void => {
		createMutation.mutate(
			{
				body: {
					operationId: operationId(),
					...values,
				},
			},
			{
				onSuccess: (created) => {
					setRepositoryRoot(values.repositoryRoot);
					setSelectedProjectId(created.project?.id ?? null);
				},
			},
		);
	};

	const startNext = (): void => {
		if (!detail?.project?.id || !task?.id) {
			return;
		}
		setPreviewTaskId(null);
		startMutation.mutate({
			path: { projectId: detail.project.id, taskId: task.id },
			body: { operationId: operationId(), repositoryRoot },
		});
	};

	const cancelActive = (): void => {
		if (!detail?.project?.id || !task?.id || !activeAttempt?.id) {
			return;
		}
		cancelMutation.mutate({ path: { projectId: detail.project.id, taskId: task.id, attemptId: activeAttempt.id } });
	};

	const preview = (): void => {
		if (!detail?.project?.id || !task?.id) {
			return;
		}
		previewMutation.mutate(
			{
				path: { projectId: detail.project.id, taskId: task.id },
				body: { operationId: operationId(), repositoryRoot },
			},
			{ onSuccess: () => setPreviewTaskId(task.id ?? null) },
		);
	};

	const apply = (): void => {
		if (!detail?.project?.id || !task?.id || previewTaskId !== task.id) {
			return;
		}
		applyMutation.mutate({
			path: { projectId: detail.project.id, taskId: task.id },
			body: { operationId: operationId(), repositoryRoot },
		});
	};

	return (
		<Container fluid={true} py="lg">
			<Stack gap="lg">
				<div>
					<Title order={1}>{t("pages.development.title", "Development Mode")}</Title>
					<Text c="dimmed">
						{t(
							"pages.development.subtitle",
							"Run one durable coder → validation → independent review → explicit apply workflow outside Chat.",
						)}
					</Text>
				</div>

				<Accordion variant="contained" defaultValue={projects.length === 0 ? "create" : null}>
					<Accordion.Item value="create">
						<Accordion.Control>{t("pages.development.newProject", "New Development project")}</Accordion.Control>
						<Accordion.Panel>
							<DevelopmentProjectForm
								isSubmitting={createMutation.isPending}
								error={
									createMutation.error
										? errorMessage(
												createMutation.error,
												t("pages.development.errors.create", "Could not create the Development project."),
											)
										: undefined
								}
								onSubmit={createProject}
							/>
						</Accordion.Panel>
					</Accordion.Item>
				</Accordion>

				{projectsQuery.isLoading ? <Loader aria-label="Loading Development projects" /> : null}
				{projectsQuery.error ? (
					<Alert color="red" icon={<IconAlertCircle size={16} />}>
						{errorMessage(projectsQuery.error, "Could not load Development projects.")}
					</Alert>
				) : null}
				{projects.length === 0 && !projectsQuery.isLoading ? (
					<Paper withBorder={true} p="xl" data-testid="development-empty-state">
						<Text fw={600}>{t("pages.development.empty.title", "No Development projects yet")}</Text>
						<Text c="dimmed">Create the initial project and task above. This workflow never enters Chat.</Text>
					</Paper>
				) : null}

				{projects.length > 0 ? (
					<Grid>
						<Grid.Col span={{ base: 12, lg: 3 }}>
							<Paper withBorder={true} p="md">
								<Stack gap="xs">
									<Text fw={600}>{t("pages.development.projects", "Projects")}</Text>
									{projects.map((project) => (
										<Button
											key={project.id}
											variant={project.id === selectedProjectId ? "light" : "subtle"}
											justify="space-between"
											onClick={() => {
												setSelectedProjectId(project.id ?? null);
												setPreviewTaskId(null);
											}}
											data-testid={`development-project-${project.id}`}
										>
											{project.objective ?? "Untitled project"}
										</Button>
									))}
								</Stack>
							</Paper>
						</Grid.Col>

						<Grid.Col span={{ base: 12, lg: 9 }}>
							{projectQuery.isLoading ? <Loader aria-label="Loading Development project" /> : null}
							{projectQuery.error ? (
								<Alert color="red" icon={<IconAlertCircle size={16} />}>
									{errorMessage(projectQuery.error, "Could not load the Development project.")}
								</Alert>
							) : null}
							{detail?.project && task ? (
								<Stack gap="lg" data-testid="development-project-detail">
									<Paper withBorder={true} p="md">
										<Group justify="space-between" align="flex-start">
											<div>
												<Title order={2}>{detail.project.objective}</Title>
												<Text c="dimmed">
													{detail.project.baseBranch} · {detail.project.egressPolicy}
												</Text>
											</div>
											<Badge color={statusColor(task.status)}>{task.status}</Badge>
										</Group>
										<Divider my="md" />
										<Title order={3}>{task.title}</Title>
										<Text>{task.requirements}</Text>
										<Group mt="md" grow={true} align="end">
											<TextInput
												label={t("pages.development.repositoryForActions", "Repository root for actions")}
												description={t(
													"pages.development.repositoryNotPersisted",
													"The absolute path is verified against the project identity and is not returned by the API.",
												)}
												value={repositoryRoot}
												onChange={(event) => setRepositoryRoot(event.currentTarget.value)}
												data-testid="development-action-repository-root"
											/>
											{nextActionStatuses.has(task.status ?? "") ? (
												<Button
													leftSection={<IconPlayerPlay size={16} />}
													onClick={startNext}
													loading={startMutation.isPending}
													disabled={!repositoryRoot || activeAttempt !== null}
													data-testid="development-start-next"
												>
													{nextActionLabel(task.status, latestAttempt?.status)}
												</Button>
											) : null}
											{activeAttempt ? (
												<Button
													color="red"
													variant="light"
													leftSection={<IconX size={16} />}
													onClick={cancelActive}
													loading={cancelMutation.isPending}
													data-testid="development-cancel-attempt"
												>
													Cancel attempt
												</Button>
											) : null}
										</Group>
										{startMutation.error ? (
											<Alert color="red" mt="md">
												{errorMessage(startMutation.error, "Could not start the next action.")}
											</Alert>
										) : null}
										{task.blockedReason ? (
											<Alert color="red" mt="md">
												{task.blockedReason}
											</Alert>
										) : null}
									</Paper>

									<Paper withBorder={true} p="md">
										<DevelopmentLivePanel
											attempt={activeAttempt ?? latestAttempt}
											live={live}
											artifacts={artifacts}
											events={events}
										/>
									</Paper>

									<Paper withBorder={true} p="md">
										<Title order={3} mb="md">
											Attempts
										</Title>
										<Table.ScrollContainer minWidth={700}>
											<Table striped={true} highlightOnHover={true}>
												<Table.Thead>
													<Table.Tr>
														<Table.Th>Role</Table.Th>
														<Table.Th>Model</Table.Th>
														<Table.Th>Provider</Table.Th>
														<Table.Th>Status</Table.Th>
														<Table.Th>Tokens</Table.Th>
														<Table.Th>Predecessor</Table.Th>
													</Table.Tr>
												</Table.Thead>
												<Table.Tbody>
													{attempts.map((attempt) => (
														<Table.Tr key={attempt.id} data-testid={`development-attempt-${attempt.id}`}>
															<Table.Td>{attempt.role}</Table.Td>
															<Table.Td>{attempt.modelId}</Table.Td>
															<Table.Td>{attempt.provider}</Table.Td>
															<Table.Td>
																<Badge color={statusColor(attempt.status)}>{attempt.status}</Badge>
															</Table.Td>
															<Table.Td>{(attempt.inputTokens ?? 0) + (attempt.outputTokens ?? 0)}</Table.Td>
															<Table.Td>{attempt.predecessorAttemptId?.slice(0, 8) ?? "—"}</Table.Td>
														</Table.Tr>
													))}
												</Table.Tbody>
											</Table>
										</Table.ScrollContainer>
									</Paper>

									{task.status === "AwaitingApply" ? (
										<Paper withBorder={true} p="md" data-testid="development-apply-panel">
											<Group justify="space-between" mb="md">
												<Title order={3}>Human-controlled patch apply</Title>
												<Badge color="green">Awaiting explicit approval</Badge>
											</Group>
											<Group>
												<Button
													leftSection={<IconGitPullRequest size={16} />}
													onClick={preview}
													loading={previewMutation.isPending}
													disabled={!repositoryRoot}
													data-testid="development-preview-patch"
												>
													Preview current patch
												</Button>
												<Button
													color="green"
													leftSection={<IconCheck size={16} />}
													onClick={apply}
													loading={applyMutation.isPending}
													disabled={!previewMutation.data || previewTaskId !== task.id}
													data-testid="development-apply-patch"
												>
													Apply verified patch
												</Button>
											</Group>
											{previewMutation.data && previewTaskId === task.id ? (
												<Stack mt="md">
													<Text size="sm">
														Subject <Code>{previewMutation.data.subjectHash}</Code> · patch{" "}
														<Code>{previewMutation.data.patchHash}</Code> · manifest{" "}
														<Code>{previewMutation.data.manifestHash}</Code>
													</Text>
													<Textarea
														value={previewMutation.data.patch ?? ""}
														readOnly={true}
														autosize={true}
														minRows={8}
														maxRows={24}
														aria-label="Verified patch preview"
													/>
												</Stack>
											) : null}
											{applyMutation.data ? (
												<Alert color="green" mt="md">
													{applyMutation.data.outcome ?? "Patch applied."}
												</Alert>
											) : null}
										</Paper>
									) : null}

									<Paper withBorder={true} p="md">
										<Group justify="space-between" mb="md">
											<Title order={3}>Durable event timeline</Title>
											<Button
												variant="subtle"
												size="xs"
												leftSection={<IconRefresh size={14} />}
												onClick={() => projectQuery.refetch()}
											>
												Refresh
											</Button>
										</Group>
										<ScrollArea h={260}>
											<Stack gap="xs">
												{events.map((event) => (
													<Group key={event.id} justify="space-between" wrap="nowrap">
														<Text size="sm">
															<Code>#{event.sequence}</Code> {event.eventType}
														</Text>
														<Text size="xs" c="dimmed">
															{event.outcome ?? event.operationPhase ?? ""}
														</Text>
													</Group>
												))}
											</Stack>
										</ScrollArea>
									</Paper>
								</Stack>
							) : null}
						</Grid.Col>
					</Grid>
				) : null}
			</Stack>
		</Container>
	);
}
