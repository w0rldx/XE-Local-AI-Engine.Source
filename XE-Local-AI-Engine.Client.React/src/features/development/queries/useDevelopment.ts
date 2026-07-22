import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	applyDevelopmentPatchMutation,
	cancelDevelopmentAttemptMutation,
	createDevelopmentProjectMutation,
	getDevelopmentProjectOptions,
	listDevelopmentProjectsOptions,
	previewDevelopmentPatchMutation,
	startDevelopmentNextActionMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";

export const developmentQueryIds = {
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

export function useDevelopmentProjects() {
	return useQuery({
		...withResponseValidation(listDevelopmentProjectsOptions()),
		select: (data) => data.items ?? [],
	});
}

export function useDevelopmentProject(projectId: string | null) {
	return useQuery({
		...withResponseValidation(getDevelopmentProjectOptions({ path: { projectId: projectId ?? "" } })),
		enabled: projectId !== null,
		refetchInterval: projectId === null ? false : 3000,
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
