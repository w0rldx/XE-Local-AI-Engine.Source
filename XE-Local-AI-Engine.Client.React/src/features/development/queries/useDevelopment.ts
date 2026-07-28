import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	applyDevelopmentPatchMutation,
	cancelDevelopmentAttemptMutation,
	createDevelopmentProjectMutation,
	createDevelopmentRepositoryFromTemplateMutation,
	detectDevelopmentRepositoryProfileOptions,
	getDevelopmentCapabilityOptions,
	getDevelopmentProjectOptions,
	listDevelopmentRepositoriesOptions,
	listDevelopmentProjectsOptions,
	listDevelopmentTemplatesOptions,
	previewDevelopmentPatchMutation,
	reconnectDevelopmentRepositoryMutation,
	registerDevelopmentRepositoryMutation,
	registerDevelopmentTemplateMutation,
	removeDevelopmentTemplateMutation,
	startDevelopmentNextActionMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import type { DevelopmentRepository, DevelopmentTemplate } from "@/features/development/models/DevelopmentModels";

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
	// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
	return [{ _id: operationId }];
}

export function useDevelopmentCapability() {
	return useQuery({
		...withResponseValidation(getDevelopmentCapabilityOptions()),
		staleTime: 30_000,
	});
}

export function useDevelopmentRepositories(enabled = true) {
	return useQuery({
		...withResponseValidation(listDevelopmentRepositoriesOptions()),
		enabled,
		select: (data): readonly DevelopmentRepository[] =>
			(data.items ?? []).flatMap((repository) =>
				repository.id && repository.alias
					? [
							{
								id: repository.id,
								alias: repository.alias,
								availability: repository.availability ?? "Unavailable",
							},
						]
					: [],
			),
	});
}

export function useDevelopmentTemplates(enabled = true) {
	return useQuery({
		...withResponseValidation(listDevelopmentTemplatesOptions()),
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
		...withResponseValidation(
			detectDevelopmentRepositoryProfileOptions({ path: { selectedFolderId: selectedFolderId ?? "" } }),
		),
		enabled: enabled && selectedFolderId !== null && selectedFolderId !== "",
		staleTime: 30_000,
	});
}

export function useDevelopmentProjects(enabled = true) {
	return useQuery({
		...withResponseValidation(listDevelopmentProjectsOptions()),
		enabled,
		select: (data) => data.items ?? [],
	});
}

export function useDevelopmentProject(projectId: string | null, enabled = true) {
	return useQuery({
		...withResponseValidation(getDevelopmentProjectOptions({ path: { projectId: projectId ?? "" } })),
		enabled: enabled && projectId !== null,
		refetchInterval: projectId === null ? false : 3000,
	});
}

export function useRegisterDevelopmentRepository() {
	const queryClient = useQueryClient();
	return useMutation({
		...withResponseValidation(registerDevelopmentRepositoryMutation()),
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
		...withResponseValidation(registerDevelopmentTemplateMutation()),
		onSuccess: () =>
			queryClient.invalidateQueries({
				queryKey: developmentInvalidationKey(developmentQueryIds.listTemplates),
			}),
	});
}

export function useRemoveDevelopmentTemplate() {
	const queryClient = useQueryClient();
	return useMutation({
		...withResponseValidation(removeDevelopmentTemplateMutation()),
		onSuccess: () =>
			queryClient.invalidateQueries({
				queryKey: developmentInvalidationKey(developmentQueryIds.listTemplates),
			}),
	});
}

export function useCreateDevelopmentRepositoryFromTemplate() {
	const queryClient = useQueryClient();
	return useMutation({
		...withResponseValidation(createDevelopmentRepositoryFromTemplateMutation()),
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
		...withResponseValidation(createDevelopmentProjectMutation()),
		onSuccess: () => invalidateDevelopment(queryClient),
	});
}

export function useReconnectDevelopmentRepository() {
	const queryClient = useQueryClient();
	return useMutation({
		...withResponseValidation(reconnectDevelopmentRepositoryMutation()),
		onSuccess: () => invalidateDevelopment(queryClient),
	});
}

export function useStartDevelopmentNextAction() {
	const queryClient = useQueryClient();
	return useMutation({
		...withResponseValidation(startDevelopmentNextActionMutation()),
		onSettled: () => invalidateDevelopment(queryClient),
	});
}

export function useCancelDevelopmentAttempt() {
	const queryClient = useQueryClient();
	return useMutation({
		...withResponseValidation(cancelDevelopmentAttemptMutation()),
		onSettled: () => invalidateDevelopment(queryClient),
	});
}

export function usePreviewDevelopmentPatch() {
	return useMutation({ ...withResponseValidation(previewDevelopmentPatchMutation()) });
}

export function useApplyDevelopmentPatch() {
	const queryClient = useQueryClient();
	return useMutation({
		...withResponseValidation(applyDevelopmentPatchMutation()),
		onSettled: () => invalidateDevelopment(queryClient),
	});
}
