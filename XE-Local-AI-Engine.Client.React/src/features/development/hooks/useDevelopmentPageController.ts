import { useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import type {
	CreateDevelopmentRepositoryFromTemplateValues,
	CreatedDevelopmentRepositoryFromTemplate,
	DevelopmentProjectFormValues,
	RegisterDevelopmentRepositoryValues,
	RegisterDevelopmentTemplateValues,
} from "@/features/development/components/DevelopmentProjectForm";
import { useDevelopmentAttemptHub } from "@/features/development/hooks/useDevelopmentAttemptHub";
import {
	type DevelopmentRepository,
	type DevelopmentTemplate,
	isActiveAttempt,
} from "@/features/development/models/DevelopmentModels";
import { nextActionLabel, operationId } from "@/features/development/models/DevelopmentStatusModel";
import {
	useApplyDevelopmentPatch,
	useCancelDevelopmentAttempt,
	useConfirmDevelopmentContainerRuntime,
	useCreateDevelopmentProject,
	useCreateDevelopmentRepositoryFromTemplate,
	useDevelopmentCapability,
	useDevelopmentProfileDetection,
	useDevelopmentProject,
	useDevelopmentProjects,
	useDevelopmentRepositories,
	useDevelopmentTemplates,
	usePreviewDevelopmentPatch,
	useReconnectDevelopmentRepository,
	useRegisterDevelopmentRepository,
	useRegisterDevelopmentTemplate,
	useRemoveDevelopmentTemplate,
	useStartDevelopmentNextAction,
} from "@/features/development/queries/useDevelopment";

export interface DevelopmentPageControllerOptions {
	/** From the route's optional `?project=` / `?task=` (X8). Both seed the INITIAL selection and nothing else. */
	readonly initialProjectId?: string;
	readonly initialTaskId?: string;
}

export function useDevelopmentPageController({ initialProjectId, initialTaskId }: DevelopmentPageControllerOptions = {}) {
	const { t } = useTranslation();
	const capabilityQuery = useDevelopmentCapability();
	const developmentEnabled = capabilityQuery.data?.enabled === true;
	const containerRuntime = capabilityQuery.data?.containerRuntime;
	const sandboxIsolation = capabilityQuery.data?.isolation;
	// The provider the backend actually resolved. Every surface that describes the isolation posture reads it from
	// here rather than asserting one, so the two providers cannot both be described by the same static sentence.
	const sandboxProvider = capabilityQuery.data?.sandboxProvider;
	const confirmContainerRuntimeMutation = useConfirmDevelopmentContainerRuntime();
	const repositoriesQuery = useDevelopmentRepositories(developmentEnabled);
	const templatesQuery = useDevelopmentTemplates(developmentEnabled);
	const projectsQuery = useDevelopmentProjects(developmentEnabled);
	const [selectedProjectId, setSelectedProjectId] = useState<string | null>(initialProjectId ?? null);
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
	// A project carries exactly one task today (the project id is uniquely indexed on the task table), so the deep
	// link's `?task=` picks the same row the fallback does. It is honoured anyway: it costs one predicate, and it is
	// the difference between a link that keeps working and a link that quietly opens the wrong task if that ever changes.
	const taskDetail = detail?.tasks?.find((entry) => entry.task?.id === initialTaskId) ?? detail?.tasks?.[0];
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
	const reconnectOptions = repositories.flatMap((repository) =>
		repository.availability === "Available" ? [{ value: repository.id, label: repository.alias }] : [],
	);

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

	return {
		t,
		capabilityQuery,
		developmentEnabled,
		containerRuntime,
		sandboxIsolation,
		sandboxProvider,
		confirmContainerRuntimeMutation,
		repositoriesQuery,
		templatesQuery,
		projectsQuery,
		selectedProjectId,
		setSelectedProjectId,
		reconnectFolderId,
		setReconnectFolderId,
		previewTaskId,
		setPreviewTaskId,
		profileFolderId,
		setProfileFolderId,
		detectionQuery,
		projectQuery,
		repositories,
		templates,
		projects,
		detail,
		task,
		attempts,
		artifacts,
		events,
		latestAttempt,
		activeAttempt,
		nextActionKey,
		nextActionDefault,
		live,
		projectRepository,
		repositoryConnectionRequired,
		repositoryReady,
		reconnectOptions,
		registerRepository,
		createRepositoryFromTemplate,
		addTemplate,
		removeTemplate,
		createProject,
		startNext,
		cancelActive,
		preview,
		apply,
		reconnectRepository,
		registerMutation,
		createMutation,
		reconnectMutation,
		startMutation,
		cancelMutation,
		previewMutation,
		applyMutation,
	};
}
