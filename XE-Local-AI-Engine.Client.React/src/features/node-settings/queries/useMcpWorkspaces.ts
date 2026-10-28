import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import type { CreateWorkspaceResponse, DeleteWorkspaceResponse } from "@/core/api/generated";
import {
	createWorkspaceMutation,
	deleteWorkspaceMutation,
	listWorkspacesOptions,
	listWorkspacesQueryKey,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { toMcpWorkspace } from "@/features/node-settings/models/McpWorkspaceModels";

export function useMcpWorkspaces() {
	return useQuery({
		...listWorkspacesOptions(),
		select: (data) => (data.items ?? []).map(toMcpWorkspace),
	});
}

function invalidateWorkspaces(queryClient: ReturnType<typeof useQueryClient>): Promise<void> {
	return queryClient.invalidateQueries({ queryKey: listWorkspacesQueryKey() });
}

export function useCreateMcpWorkspace() {
	const queryClient = useQueryClient();

	return useMutation({
		...createWorkspaceMutation(),
		onSuccess: (_data: CreateWorkspaceResponse) => invalidateWorkspaces(queryClient),
	});
}

export function useDeleteMcpWorkspace() {
	const queryClient = useQueryClient();

	return useMutation({
		...deleteWorkspaceMutation(),
		onSuccess: (_data: DeleteWorkspaceResponse) => invalidateWorkspaces(queryClient),
	});
}
