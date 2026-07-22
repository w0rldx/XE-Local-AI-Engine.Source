import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	applyDevelopmentPatchMutation,
	cancelDevelopmentAttemptMutation,
	createDevelopmentProjectMutation,
	getDevelopmentCapabilityOptions,
	getDevelopmentProjectOptions,
	listDevelopmentRepositoriesOptions,
	listDevelopmentProjectsOptions,
	previewDevelopmentPatchMutation,
	reconnectDevelopmentRepositoryMutation,
	registerDevelopmentRepositoryMutation,
	startDevelopmentNextActionMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import type { DevelopmentRepository } from "@/features/development/models/DevelopmentModels";

export const developmentQueryIds = {
	capability: "getDevelopmentCapability",
	listRepositories: "listDevelopmentRepositories",
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
		onSuccess: () =>
			queryClient.invalidateQueries({
				queryKey: developmentInvalidationKey(developmentQueryIds.listRepositories),
			}),
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
