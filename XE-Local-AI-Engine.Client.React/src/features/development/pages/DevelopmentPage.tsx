import {
	Accordion,
	Alert,
	Badge,
	Button,
	Code,
	Divider,
	Grid,
	Group,
	Loader,
	ScrollArea,
	Select,
	Stack,
	Table,
	Text,
	Title,
} from "@mantine/core";
import {
	IconAlertTriangle,
	IconCheck,
	IconCode,
	IconGitPullRequest,
	IconLink,
	IconPlayerPlay,
	IconRefresh,
	IconX,
} from "@tabler/icons-react";
import { Fragment, useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { CodeEditor } from "@/core/ui/components/CodeEditor/CodeEditor";
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { DevelopmentContainerRuntimePanel } from "@/features/development/components/DevelopmentContainerRuntimePanel";
import { DevelopmentLivePanel } from "@/features/development/components/DevelopmentLivePanel";
import {
	type CreateDevelopmentRepositoryFromTemplateValues,
	type CreatedDevelopmentRepositoryFromTemplate,
	DevelopmentProjectForm,
	type DevelopmentProjectFormValues,
	type RegisterDevelopmentRepositoryValues,
	type RegisterDevelopmentTemplateValues,
} from "@/features/development/components/DevelopmentProjectForm";
import { useDevelopmentAttemptHub } from "@/features/development/hooks/useDevelopmentAttemptHub";
import {
	type DevelopmentRepository,
	type DevelopmentTemplate,
	isActiveAttempt,
} from "@/features/development/models/DevelopmentModels";
import {
	useApplyDevelopmentPatch,
	useCancelDevelopmentAttempt,
	useConfirmDevelopmentContainerRuntime,
	useCreateDevelopmentProject,
	useCreateDevelopmentRepositoryFromTemplate,
	useDevelopmentCapability,
	useDevelopmentProject,
	useDevelopmentProjects,
	useDevelopmentProfileDetection,
	useDevelopmentRepositories,
	useDevelopmentTemplates,
	usePreviewDevelopmentPatch,
	useReconnectDevelopmentRepository,
	useRegisterDevelopmentRepository,
	useRegisterDevelopmentTemplate,
	useRemoveDevelopmentTemplate,
	useStartDevelopmentNextAction,
} from "@/features/development/queries/useDevelopment";

const nextActionStatuses = new Set(["Planned", "Ready", "InProgress", "ChangesRequested", "InReview"]);

function operationId(): string {
	return globalThis.crypto.randomUUID();
}

/**
 * Splits an engine-authored terminal reason into its stable code and its prose.
 *
 * The backend emits `[some_code] Sentence…` for failures it diagnosed itself, and a bare sentence
 * for everything else. Both render; only the first gets a code chip. Parsing rather than adding a
 * second wire field keeps this a display concern — an unrecognised shape degrades to plain prose.
 */
function splitTerminalReason(reason: string): { code: string | null; message: string } {
	const match = /^\[([a-z0-9_]+)]\s*(.*)$/s.exec(reason);
	if (match === null) {
		return { code: null, message: reason };
	}

	return { code: match[1] ?? null, message: match[2] ?? "" };
}

function AttemptTerminalReason({ reason, color }: { reason: string; color: string }) {
	const { code, message } = splitTerminalReason(reason);

	return (
		<Group gap="xs" align="flex-start" wrap="nowrap">
			{code === null ? null : (
				<Code c={color} data-testid="development-attempt-reason-code">
					{code}
				</Code>
			)}
			<Text size="sm" c="dimmed">
				{message}
			</Text>
		</Group>
	);
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

/**
 * The next-action button's label as a translation key paired with its English default.
 *
 * The label names the SPECIFIC action the engine will take next, which is the only thing on the page that tells the
 * operator what the button is about to do. Returning the key rather than the sentence keeps that selection here, in
 * plain control flow, while leaving the wording to the locale files.
 */
function nextActionLabel(status: string | undefined, latestAttemptStatus: string | undefined): readonly [string, string] {
	if (latestAttemptStatus === "Interrupted") {
		return ["pages.development.nextAction.replacement", "Start replacement attempt"];
	}
	if (status === "InReview") {
		return ["pages.development.nextAction.review", "Start independent review"];
	}
	if (status === "InProgress" && latestAttemptStatus === "Succeeded") {
		return ["pages.development.nextAction.validation", "Run deterministic validation"];
	}
	if (status === "ChangesRequested") {
		return ["pages.development.nextAction.revision", "Start coder revision"];
	}
	return ["pages.development.nextAction.default", "Start next action"];
}

export function DevelopmentPage() {
	const { t } = useTranslation();
	const capabilityQuery = useDevelopmentCapability();
	const developmentEnabled = capabilityQuery.data?.enabled === true;
	const containerRuntime = capabilityQuery.data?.containerRuntime;
	// The provider the backend actually resolved. Every surface that describes the isolation posture reads it from
	// here rather than asserting one, so the two providers cannot both be described by the same static sentence.
	const sandboxProvider = capabilityQuery.data?.sandboxProvider;
	const confirmContainerRuntimeMutation = useConfirmDevelopmentContainerRuntime();
	const repositoriesQuery = useDevelopmentRepositories(developmentEnabled);
	const templatesQuery = useDevelopmentTemplates(developmentEnabled);
	const projectsQuery = useDevelopmentProjects(developmentEnabled);
	const [selectedProjectId, setSelectedProjectId] = useState<string | null>(null);
	const [reconnectFolderId, setReconnectFolderId] = useState<string | null>(null);
	const [previewTaskId, setPreviewTaskId] = useState<string | null>(null);
	const [profileFolderId, setProfileFolderId] = useState<string | null>(null);
	const detectionQuery = useDevelopmentProfileDetection(profileFolderId, developmentEnabled);
	const projectQuery = useDevelopmentProject(selectedProjectId, developmentEnabled);
	const registerMutation = useRegisterDevelopmentRepository();
	const registerTemplateMutation = useRegisterDevelopmentTemplate();
	const removeTemplateMutation = useRemoveDevelopmentTemplate();
	const createFromTemplateMutation = useCreateDevelopmentRepositoryFromTemplate();
	const createMutation = useCreateDevelopmentProject();
	const reconnectMutation = useReconnectDevelopmentRepository();
	const startMutation = useStartDevelopmentNextAction();
	const cancelMutation = useCancelDevelopmentAttempt();
	const previewMutation = usePreviewDevelopmentPatch();
	const applyMutation = useApplyDevelopmentPatch();

	const repositories = useMemo(() => repositoriesQuery.data ?? [], [repositoriesQuery.data]);
	const templates = useMemo(() => templatesQuery.data ?? [], [templatesQuery.data]);
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
	const [nextActionKey, nextActionDefault] = nextActionLabel(task?.status, latestAttempt?.status);
	const live = useDevelopmentAttemptHub(detail?.project?.id ?? null, task?.id ?? null, activeAttempt?.id ?? null);
	const projectRepository = repositories.find((repository) => repository.id === detail?.project?.selectedFolderId);
	const repositoryConnectionRequired = detail?.project?.repositoryConnectionRequired === true;
	const repositoryReady =
		!repositoryConnectionRequired && projectRepository?.availability === "Available" && !repositoriesQuery.error;
	const reconnectOptions = repositories
		.filter((repository) => repository.availability === "Available")
		.map((repository) => ({ value: repository.id, label: repository.alias }));

	const registerRepository = async (values: RegisterDevelopmentRepositoryValues): Promise<DevelopmentRepository> => {
		const created = await registerMutation.mutateAsync({ body: values });
		if (!created.id || !created.alias) {
			throw new Error("The repository registration response was incomplete.");
		}

		return {
			id: created.id,
			alias: created.alias,
			availability: created.availability ?? "Available",
		};
	};

	const createRepositoryFromTemplate = async (
		values: CreateDevelopmentRepositoryFromTemplateValues,
	): Promise<CreatedDevelopmentRepositoryFromTemplate> => {
		const created = await createFromTemplateMutation.mutateAsync({ body: values });
		const repository = created.repository;
		if (!repository?.id || !repository.alias) {
			throw new Error("The template repository creation response was incomplete.");
		}

		return {
			repository: {
				id: repository.id,
				alias: repository.alias,
				availability: repository.availability ?? "Available",
			},
			templateAlias: created.templateAlias,
			templateCommit: created.templateCommit,
		};
	};

	const addTemplate = async (values: RegisterDevelopmentTemplateValues): Promise<DevelopmentTemplate> => {
		const created = await registerTemplateMutation.mutateAsync({ body: values });
		if (!created.id || !created.alias) {
			throw new Error("The template registration response was incomplete.");
		}

		return {
			id: created.id,
			alias: created.alias,
			availability: created.availability ?? "Available",
		};
	};

	const removeTemplate = async (templateId: string): Promise<void> => {
		await removeTemplateMutation.mutateAsync({ path: { templateId } });
	};

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
					setSelectedProjectId(created.project?.id ?? null);
				},
			},
		);
	};

	const startNext = (): void => {
		if (!repositoryReady || !detail?.project?.id || !task?.id) {
			return;
		}
		setPreviewTaskId(null);
		startMutation.mutate({
			path: { projectId: detail.project.id, taskId: task.id },
			body: { operationId: operationId() },
		});
	};

	const cancelActive = (): void => {
		if (!detail?.project?.id || !task?.id || !activeAttempt?.id) {
			return;
		}
		cancelMutation.mutate({ path: { projectId: detail.project.id, taskId: task.id, attemptId: activeAttempt.id } });
	};

	const preview = (): void => {
		if (!repositoryReady || !detail?.project?.id || !task?.id) {
			return;
		}
		previewMutation.mutate(
			{
				path: { projectId: detail.project.id, taskId: task.id },
				body: { operationId: operationId() },
			},
			{ onSuccess: () => setPreviewTaskId(task.id ?? null) },
		);
	};

	const apply = (): void => {
		if (!repositoryReady || !detail?.project?.id || !task?.id || previewTaskId !== task.id) {
			return;
		}
		applyMutation.mutate({
			path: { projectId: detail.project.id, taskId: task.id },
			body: { operationId: operationId() },
		});
	};

	const reconnectRepository = (): void => {
		if (!detail?.project?.id || !reconnectFolderId) {
			return;
		}

		reconnectMutation.mutate(
			{
				path: { projectId: detail.project.id },
				body: {
					selectedFolderId: reconnectFolderId,
					expectedVersion: detail.project.version ?? 0,
				},
			},
			{ onSuccess: () => setReconnectFolderId(null) },
		);
	};

	if (capabilityQuery.isLoading) {
		return (
			<PageShell>
				<Group gap="sm">
					<Loader size="sm" />
					<Text c="dimmed">{t("pages.development.loading.capability", "Loading Development capability")}</Text>
				</Group>
			</PageShell>
		);
	}

	if (capabilityQuery.error || !developmentEnabled) {
		return (
			<PageShell>
				<Alert color={capabilityQuery.error ? "red" : "yellow"} icon={<IconAlertTriangle size={16} />}>
					{capabilityQuery.error
						? apiErrorMessage(capabilityQuery.error, "Could not verify whether Development Mode is available.")
						: t("pages.development.disabled", "Development Mode is disabled by this node's runtime configuration.")}
				</Alert>
			</PageShell>
		);
	}

	return (
		<PageShell>
			{/*
			 * Above the page body rather than replacing it. ADR 0004 makes a container runtime a hard requirement for
			 * Development Mode execution, but execution has not moved to the container provider yet, so blocking the
			 * page on this preflight would break the workflow that ships today. The panel says so explicitly rather
			 * than leaving the operator to reconcile a red banner with a page that plainly works.
			 */}
			<DevelopmentContainerRuntimePanel
				runtime={containerRuntime}
				sandboxProvider={sandboxProvider}
				onConfirm={(daemonId) => confirmContainerRuntimeMutation.mutate({ body: { daemonId } })}
				confirming={confirmContainerRuntimeMutation.isPending}
				confirmError={
					confirmContainerRuntimeMutation.error
						? apiErrorMessage(confirmContainerRuntimeMutation.error, "Could not confirm the container runtime.")
						: undefined
				}
			/>

			<PageHeader
				icon={<IconCode size={24} />}
				title={t("pages.development.title", "Development Mode")}
				subtitle={t(
					"pages.development.subtitle",
					"Run one durable coder → validation → independent review → explicit apply workflow outside Chat.",
				)}
			/>

			<Accordion variant="contained" defaultValue={projects.length === 0 ? "create" : null}>
				<Accordion.Item value="create">
					<Accordion.Control>{t("pages.development.newProject", "New Development project")}</Accordion.Control>
					<Accordion.Panel>
						<DevelopmentProjectForm
							sandboxProvider={sandboxProvider}
							repositories={repositories}
							repositoriesLoading={repositoriesQuery.isLoading}
							repositoriesError={
								repositoriesQuery.error
									? apiErrorMessage(repositoriesQuery.error, "Could not load registered Development repositories.")
									: undefined
							}
							isRegistering={registerMutation.isPending}
							isSubmitting={createMutation.isPending}
							error={
								createMutation.error
									? apiErrorMessage(
											createMutation.error,
											t("pages.development.errors.create", "Could not create the Development project."),
										)
									: undefined
							}
							detection={profileFolderId ? (detectionQuery.data ?? null) : null}
							detectionLoading={detectionQuery.isFetching}
							detectionError={
								detectionQuery.error
									? apiErrorMessage(
											detectionQuery.error,
											t("pages.development.errors.profileDetection", "Could not inspect the repository for a build system."),
										)
									: undefined
							}
							templates={templates}
							templatesLoading={templatesQuery.isLoading}
							onRepositoryChange={setProfileFolderId}
							onRegister={registerRepository}
							onCreateFromTemplate={createRepositoryFromTemplate}
							onAddTemplate={addTemplate}
							onRemoveTemplate={removeTemplate}
							onSubmit={createProject}
						/>
					</Accordion.Panel>
				</Accordion.Item>
			</Accordion>

			{projectsQuery.isLoading ? (
				<Group gap="sm">
					<Loader size="sm" />
					<Text c="dimmed">{t("pages.development.loading.projects", "Loading Development projects")}</Text>
				</Group>
			) : null}
			{projectsQuery.error ? (
				<Alert color="red" icon={<IconAlertTriangle size={16} />}>
					{apiErrorMessage(projectsQuery.error, "Could not load Development projects.")}
				</Alert>
			) : null}
			{projects.length === 0 && !projectsQuery.isLoading ? (
				<SectionCard gap="xs" data-testid="development-empty-state">
					<Text fw={600}>{t("pages.development.empty.title", "No Development projects yet")}</Text>
					<Text c="dimmed">
						{t("pages.development.empty.body", "Create the initial project and task above. This workflow never enters Chat.")}
					</Text>
				</SectionCard>
			) : null}

			{projects.length > 0 ? (
				<Grid>
					<Grid.Col span={{ base: 12, lg: 3 }}>
						<SectionCard title={t("pages.development.projects", "Projects")} gap="xs">
							{projects.map((project) => (
								<Button
									key={project.id}
									variant={project.id === selectedProjectId ? "light" : "subtle"}
									justify="space-between"
									onClick={() => {
										setSelectedProjectId(project.id ?? null);
										setReconnectFolderId(null);
										setPreviewTaskId(null);
									}}
									data-testid={`development-project-${project.id}`}
								>
									{project.objective ?? t("pages.development.untitledProject", "Untitled project")}
								</Button>
							))}
						</SectionCard>
					</Grid.Col>

					<Grid.Col span={{ base: 12, lg: 9 }}>
						{projectQuery.isLoading ? (
							<Group gap="sm">
								<Loader size="sm" />
								<Text c="dimmed">{t("pages.development.loading.project", "Loading Development project")}</Text>
							</Group>
						) : null}
						{projectQuery.error ? (
							<Alert color="red" icon={<IconAlertTriangle size={16} />}>
								{apiErrorMessage(projectQuery.error, "Could not load the Development project.")}
							</Alert>
						) : null}
						{detail?.project && task ? (
							<Stack gap="lg" data-testid="development-project-detail">
								<SectionCard>
									<Group justify="space-between" align="flex-start">
										<div>
											<Title order={2}>{detail.project.objective}</Title>
											<Text c="dimmed">
												{detail.project.baseBranch} · {detail.project.egressPolicy} ·{" "}
												{projectRepository?.alias ?? t("pages.development.repositoryNotConnected", "Repository not connected")}
											</Text>
										</div>
										<Badge color={statusColor(task.status)}>{task.status}</Badge>
									</Group>
									<Divider />
									<Stack gap="xs">
										<Title order={3}>{task.title}</Title>
										<Text>{task.requirements}</Text>
									</Stack>
									{repositoryConnectionRequired ? (
										<Alert color="yellow" icon={<IconLink size={16} />} data-testid="development-reconnect-panel">
											<Stack gap="sm">
												<Text>
													{t(
														"pages.development.reconnect.description",
														"This existing project must be reconnected to its original registered repository before actions can run.",
													)}
												</Text>
												<Group align="end">
													<Select
														label={t("pages.development.reconnect.repository", "Original repository")}
														data={reconnectOptions}
														value={reconnectFolderId}
														onChange={setReconnectFolderId}
														loading={repositoriesQuery.isLoading}
														data-testid="development-reconnect-select"
													/>
													<Button
														leftSection={<IconLink size={16} />}
														onClick={reconnectRepository}
														loading={reconnectMutation.isPending}
														disabled={!reconnectFolderId}
														data-testid="development-reconnect-repository"
													>
														{t("pages.development.reconnect.submit", "Reconnect repository")}
													</Button>
												</Group>
												{reconnectMutation.error ? (
													<Text c="red" size="sm">
														{apiErrorMessage(reconnectMutation.error, "Could not reconnect the repository.")}
													</Text>
												) : null}
											</Stack>
										</Alert>
									) : repositoriesQuery.isLoading ? (
										<Group gap="sm">
											<Loader size="sm" />
											<Text c="dimmed">
												{t("pages.development.loading.repositories", "Loading registered Development repositories")}
											</Text>
										</Group>
									) : projectRepository?.availability !== "Available" ? (
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
												leftSection={<IconPlayerPlay size={16} />}
												onClick={startNext}
												loading={startMutation.isPending}
												disabled={!repositoryReady || activeAttempt !== null}
												data-testid="development-start-next"
											>
												{t(nextActionKey, nextActionDefault)}
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
												{t("pages.development.cancelAttempt", "Cancel attempt")}
											</Button>
										) : null}
									</Group>
									{startMutation.error ? (
										<Alert color="red">{apiErrorMessage(startMutation.error, "Could not start the next action.")}</Alert>
									) : null}
									{task.blockedReason ? <Alert color="red">{task.blockedReason}</Alert> : null}
								</SectionCard>

								<SectionCard>
									<DevelopmentLivePanel
										attempt={activeAttempt ?? latestAttempt}
										live={live}
										artifacts={artifacts}
										events={events}
									/>
								</SectionCard>

								<SectionCard title={t("pages.development.attempts.title", "Attempts")}>
									<Table.ScrollContainer minWidth={700}>
										<Table striped={true} highlightOnHover={true}>
											<Table.Thead>
												<Table.Tr>
													<Table.Th>{t("pages.development.attempts.role", "Role")}</Table.Th>
													<Table.Th>{t("pages.development.attempts.model", "Model")}</Table.Th>
													<Table.Th>{t("pages.development.attempts.provider", "Provider")}</Table.Th>
													<Table.Th>{t("pages.development.attempts.status", "Status")}</Table.Th>
													<Table.Th>{t("pages.development.attempts.tokens", "Tokens")}</Table.Th>
													<Table.Th>{t("pages.development.attempts.predecessor", "Predecessor")}</Table.Th>
												</Table.Tr>
											</Table.Thead>
											<Table.Tbody>
												{attempts.map((attempt) => (
													<Fragment key={attempt.id}>
														<Table.Tr data-testid={`development-attempt-${attempt.id}`}>
															<Table.Td>{attempt.role}</Table.Td>
															<Table.Td>{attempt.modelId}</Table.Td>
															<Table.Td>{attempt.provider}</Table.Td>
															<Table.Td>
																<Badge color={statusColor(attempt.status)}>{attempt.status}</Badge>
															</Table.Td>
															<Table.Td>{(attempt.inputTokens ?? 0) + (attempt.outputTokens ?? 0)}</Table.Td>
															<Table.Td>{attempt.predecessorAttemptId?.slice(0, 8) ?? "—"}</Table.Td>
														</Table.Tr>
														{/*
															The reason an attempt ended, on its own full-width row rather than in a
															column: it is a sentence, and squeezing it into a cell is what keeps it
															unread. Rendered at all because it previously was not rendered ANYWHERE —
															the operator saw a red FAILED badge and nothing else, while the engine had
															already diagnosed the cause and persisted it.
														*/}
														{attempt.terminalReason ? (
															<Table.Tr data-testid={`development-attempt-reason-${attempt.id}`}>
																<Table.Td colSpan={6} py="xs">
																	<AttemptTerminalReason reason={attempt.terminalReason} color={statusColor(attempt.status)} />
																</Table.Td>
															</Table.Tr>
														) : null}
													</Fragment>
												))}
											</Table.Tbody>
										</Table>
									</Table.ScrollContainer>
								</SectionCard>

								{task.status === "AwaitingApply" ? (
									<SectionCard
										title={t("pages.development.apply.title", "Human-controlled patch apply")}
										actions={<Badge color="green">{t("pages.development.apply.awaiting", "Awaiting explicit approval")}</Badge>}
										data-testid="development-apply-panel"
									>
										<Group>
											<Button
												leftSection={<IconGitPullRequest size={16} />}
												onClick={preview}
												loading={previewMutation.isPending}
												disabled={!repositoryReady}
												data-testid="development-preview-patch"
											>
												{t("pages.development.apply.preview", "Preview current patch")}
											</Button>
											<Button
												color="green"
												leftSection={<IconCheck size={16} />}
												onClick={apply}
												loading={applyMutation.isPending}
												disabled={!repositoryReady || !previewMutation.data || previewTaskId !== task.id}
												data-testid="development-apply-patch"
											>
												{t("pages.development.apply.apply", "Apply verified patch")}
											</Button>
										</Group>
										{previewMutation.data && previewTaskId === task.id ? (
											<Stack>
												<Text size="sm">
													{t("pages.development.apply.subject", "Subject")} <Code>{previewMutation.data.subjectHash}</Code> ·{" "}
													{t("pages.development.apply.patch", "patch")} <Code>{previewMutation.data.patchHash}</Code> ·{" "}
													{t("pages.development.apply.manifest", "manifest")} <Code>{previewMutation.data.manifestHash}</Code>
												</Text>
												<CodeEditor
													value={previewMutation.data.patch ?? ""}
													language="diff"
													readOnly={true}
													height={360}
													aria-label={t("pages.development.apply.previewLabel", "Verified patch preview")}
													data-testid="development-patch-preview"
												/>
											</Stack>
										) : null}
										{applyMutation.data ? (
											<Alert color="green">
												{applyMutation.data.outcome ?? t("pages.development.apply.applied", "Patch applied.")}
											</Alert>
										) : null}
									</SectionCard>
								) : null}

								<SectionCard
									title={t("pages.development.timeline.title", "Durable event timeline")}
									actions={
										<Button
											variant="subtle"
											size="xs"
											leftSection={<IconRefresh size={14} />}
											onClick={() => projectQuery.refetch()}
										>
											{t("common.refresh", "Refresh")}
										</Button>
									}
								>
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
								</SectionCard>
							</Stack>
						) : null}
					</Grid.Col>
				</Grid>
			) : null}
		</PageShell>
	);
}
