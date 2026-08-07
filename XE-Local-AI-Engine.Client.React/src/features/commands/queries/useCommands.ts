import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import type { CreateSlashCommandResponse, DeleteSlashCommandResponse, UpdateSlashCommandResponse } from "@/core/api/generated";
import {
	createSlashCommandMutation,
	deleteSlashCommandMutation,
	listSlashCommandsOptions,
	listSlashCommandsQueryKey,
	updateSlashCommandMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { toSlashCommands } from "@/features/commands/models/CommandMappers";

export function useCommands() {
	return useQuery({
		...withResponseValidation(listSlashCommandsOptions()),
		select: (data) => toSlashCommands(data.items),
	});
}

function invalidateCommands(queryClient: ReturnType<typeof useQueryClient>): Promise<void> {
	return queryClient.invalidateQueries({ queryKey: listSlashCommandsQueryKey() });
}

export function useCreateCommand() {
	const queryClient = useQueryClient();
	return useMutation({
		...withResponseValidation(createSlashCommandMutation()),
		onSuccess: (_data: CreateSlashCommandResponse) => invalidateCommands(queryClient),
	});
}

export function useUpdateCommand() {
	const queryClient = useQueryClient();
	return useMutation({
		...withResponseValidation(updateSlashCommandMutation()),
		onSuccess: (_data: UpdateSlashCommandResponse) => invalidateCommands(queryClient),
	});
}

export function useDeleteCommand() {
	const queryClient = useQueryClient();
	return useMutation({
		...withResponseValidation(deleteSlashCommandMutation()),
		onSuccess: (_data: DeleteSlashCommandResponse) => invalidateCommands(queryClient),
	});
}
