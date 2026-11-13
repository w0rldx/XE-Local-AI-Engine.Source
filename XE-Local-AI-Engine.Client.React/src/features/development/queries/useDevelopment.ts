import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";

import { ApiError } from "@/core/api/errors/ApiError";
import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import {
	applyDevelopmentPatchMutation,
	cancelDevelopmentAttemptMutation,
	confirmDevelopmentContainerRuntimeMutation,
	createDevelopmentProjectMutation,
	createDevelopmentRepositoryFromTemplateMutation,
	detectDevelopmentRepositoryProfileOptions,
	getDevelopmentArtifactOptions,
	getDevelopmentCapabilityOptions,
	getDevelopmentProjectOptions,
	getGraphWorkflowRunOptions,
	listDevelopmentProjectsOptions,
	listDevelopmentTemplatesOptions,
	previewDevelopmentPatchMutation,
	reconnectDevelopmentRepositoryMutation,
	registerDevelopmentRepositoryMutation,
	registerDevelopmentTemplateMutation,
	removeDevelopmentTemplateMutation,
	startDevelopmentNextActionMutation,
	stopDevelopmentAttemptCommandMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { toast } from "@/core/ui/notifications/Toast";
import {
	type DevelopmentProjectDetail,
	type DevelopmentTemplate,
	isActiveAttempt,
} from "@/features/development/models/DevelopmentModels";

export const developmentQueryIds = {
	capability: "getDevelopmentCapability",
	listRepositories: "listDevelopmentRepositories",
	listTemplates: "listDevelopmentTemplates",
	detectRepositoryProfile: "detectDevelopmentRepositoryProfile",
	listProjects: "listDevelopmentProjects",
	getProject: "getDevelopmentProject",
	getTask: "getDevelopmentTask",
	listEvents: "listDevelopmentEvents",
	listArtifacts: "listDevelopmentArtifacts",
	getArtifact: "getDevelopmentArtifact",
} as const;

export function developmentInvalidationKey(operationId: string): readonly [{ _id: string }] {
	return [{ _id: operationId }];
}

export function useDevelopmentCapability() {
	return useQuery({
		...getDevelopmentCapabilityOptions(),
		staleTime: 30_000,
	});
}

/**
 * Records the operator's explicit approval of the container runtime currently reachable.
 *
 * Invalidates the capability query on settle so the panel re-reads the preflight rather than trusting the mutation's
 * own response — the daemon can change again between the confirmation and the next render, and the capability query is
 * the single place that answer is derived.
 */
export function useConfirmDevelopmentContainerRuntime() {
	const queryClient = useQueryClient();
	return useMutation({
		...confirmDevelopmentContainerRuntimeMutation(),
		onSettled: () => queryClient.invalidateQueries({ queryKey: developmentInvalidationKey(developmentQueryIds.capability) }),
	});
}

export function useDevelopmentTemplates(enabled = true) {
	return useQuery({
		...listDevelopmentTemplatesOptions(),
		enabled,
		select: (data): readonly DevelopmentTemplate[] =>
			(data.templates ?? []).flatMap((template) =>
				template.id && template.alias
					? [
							{
								id: template.id,
								alias: template.alias,
								availability: template.availability ?? "Unavailable",
							},
						]
					: [],
			),
	});
}

/**
 * The detection proposal for a repository the operator is about to bind. Read-only: it is what the confirmation step
 * shows, and the confirmed answer is what the create request carries.
 */
export function useDevelopmentProfileDetection(selectedFolderId: string | null, enabled = true) {
	return useQuery({
		...detectDevelopmentRepositoryProfileOptions({ path: { selectedFolderId: selectedFolderId ?? "" } }),
		enabled: enabled && selectedFolderId !== null && selectedFolderId !== "",
		staleTime: 30_000,
	});
}

export function useDevelopmentProjects(enabled = true) {
	return useQuery({
		...listDevelopmentProjectsOptions(),
		enabled,
		select: (data) => data.items ?? [],
	});
}

export function useDevelopmentProject(projectId: string | null, enabled = true) {
	return useQuery({
		...getDevelopmentProjectOptions({ path: { projectId: projectId ?? "" } }),
		enabled: enabled && projectId !== null,
		refetchInterval: developmentProjectPollInterval,
	});
}

/**
 * Case (c) while an attempt runs: its hub streams output but refreshes this detail only on `Terminal`, so persisted
 * events, artifacts and a coalesced terminal arrive here. Case (d) otherwise: a graph workflow starts attempts and tasks
 * outside this page, and no hub announces them.
 */
export function developmentProjectPollInterval(query: { state: { data?: DevelopmentProjectDetail } }): number {
	// A task in Validation runs commands with no active attempt, and only this poll says when it starts and ends.
	return query.state.data?.tasks?.some((task) => task.attempts?.some(isActiveAttempt) || task.task?.status === "Validation")
		? 3000
		: 5000;
}

/**
 * The Graph Workflows run that drives a workflow-driven task. The banner links it by its own id, so this read is only
 * for the status. The run never changes owner, so this never polls.
 */
export function useDevelopmentTaskWorkflowRun(workflowRunId: string | null | undefined, enabled = true) {
	return useQuery({
		...getGraphWorkflowRunOptions({ path: { runId: workflowRunId ?? "" } }),
		select: (data): { status: string } => ({ status: data.run.status }),
		enabled: enabled && hasIdentifier(workflowRunId),
	});
}

function hasIdentifier(value: string | null | undefined): boolean {
	return typeof value === "string" && value.length > 0;
}

/**
 * The decrypted body of one artifact. The response carries the artifact row plus `content`, an opaque string the
 * caller parses itself — validation reports have no generated body type because they are stored as an encrypted blob.
 *
 * Gated on all three route ids so the query never fires for an attempt that has produced no such artifact yet.
 */
export function useDevelopmentArtifactContent(
	projectId: string | null | undefined,
	taskId: string | null | undefined,
	artifactId: string | null | undefined,
) {
	return useQuery({
		...getDevelopmentArtifactOptions({
			path: { projectId: projectId ?? "", taskId: taskId ?? "", artifactId: artifactId ?? "" },
		}),
		enabled: hasIdentifier(projectId) && hasIdentifier(taskId) && hasIdentifier(artifactId),
		// An artifact is content-hash addressed and immutable for a given id: a new run writes a new artifact rather
		// than rewriting this one, so refetching the same id is pure waste.
		staleTime: Number.POSITIVE_INFINITY,
	});
}

export function useRegisterDevelopmentRepository() {
	const queryClient = useQueryClient();
	return useMutation({
		...registerDevelopmentRepositoryMutation(),
		// Detection is keyed by selected folder, so it goes with the repository list: re-registering an existing path
		// returns the same folder id, and a cached proposal for it can predate whatever the repository looks like now.
		onSuccess: () =>
			Promise.all([
				queryClient.invalidateQueries({
					queryKey: developmentInvalidationKey(developmentQueryIds.listRepositories),
				}),
				queryClient.invalidateQueries({
					queryKey: developmentInvalidationKey(developmentQueryIds.detectRepositoryProfile),
				}),
			]),
	});
}

export function useRegisterDevelopmentTemplate() {
	const queryClient = useQueryClient();
	return useMutation({
		...registerDevelopmentTemplateMutation(),
		onSuccess: () =>
			queryClient.invalidateQueries({
				queryKey: developmentInvalidationKey(developmentQueryIds.listTemplates),
			}),
	});
}

export function useRemoveDevelopmentTemplate() {
	const queryClient = useQueryClient();
	return useMutation({
		...removeDevelopmentTemplateMutation(),
		onSuccess: () =>
			queryClient.invalidateQueries({
				queryKey: developmentInvalidationKey(developmentQueryIds.listTemplates),
			}),
	});
}

export function useCreateDevelopmentRepositoryFromTemplate() {
	const queryClient = useQueryClient();
	return useMutation({
		...createDevelopmentRepositoryFromTemplateMutation(),
		// Seeding from a template registers a repository, so this carries the same pairing as
		// useRegisterDevelopmentRepository: the list gains an entry, and detection is keyed by the folder id that entry
		// carries, so a cached proposal for a reused id must not outlive the new clone.
		onSuccess: () =>
			Promise.all([
				queryClient.invalidateQueries({
					queryKey: developmentInvalidationKey(developmentQueryIds.listRepositories),
				}),
				queryClient.invalidateQueries({
					queryKey: developmentInvalidationKey(developmentQueryIds.detectRepositoryProfile),
				}),
			]),
	});
}

async function invalidateDevelopment(queryClient: ReturnType<typeof useQueryClient>): Promise<void> {
	await Promise.all(
		Object.values(developmentQueryIds).map((operationId) =>
			queryClient.invalidateQueries({ queryKey: developmentInvalidationKey(operationId) }),
		),
	);
}

export function useCreateDevelopmentProject() {
	const queryClient = useQueryClient();
	return useMutation({
		...createDevelopmentProjectMutation(),
		onSuccess: () => invalidateDevelopment(queryClient),
	});
}

export function useReconnectDevelopmentRepository() {
	const queryClient = useQueryClient();
	return useMutation({
		...reconnectDevelopmentRepositoryMutation(),
		onSuccess: () => invalidateDevelopment(queryClient),
	});
}

export function useStartDevelopmentNextAction() {
	const queryClient = useQueryClient();
	return useMutation({
		...startDevelopmentNextActionMutation(),
		onSettled: () => invalidateDevelopment(queryClient),
	});
}

export function useCancelDevelopmentAttempt() {
	const queryClient = useQueryClient();
	return useMutation({
		...cancelDevelopmentAttemptMutation(),
		onSettled: () => invalidateDevelopment(queryClient),
	});
}

/**
 * Stops one running attempt command by its execution id. Its completed update says it was stopped, so a success needs
 * no message; a 404 means the command had already ended.
 */
export function useStopDevelopmentAttemptCommand() {
	const { t } = useTranslation();
	return useMutation({
		...stopDevelopmentAttemptCommandMutation(),
		onError: (error) => {
			if (error instanceof ApiError && error.statusCode === 404) {
				toast.info(t("pages.development.live.stopNothingRunning", "The command had already finished."));
				return;
			}
			toast.error(apiErrorMessage(error, t("pages.development.live.stopFailed", "Could not stop the command.")));
		},
	});
}

export function usePreviewDevelopmentPatch() {
	return useMutation({ ...previewDevelopmentPatchMutation() });
}

export function useApplyDevelopmentPatch() {
	const queryClient = useQueryClient();
	return useMutation({
		...applyDevelopmentPatchMutation(),
		onSettled: () => invalidateDevelopment(queryClient),
	});
}
