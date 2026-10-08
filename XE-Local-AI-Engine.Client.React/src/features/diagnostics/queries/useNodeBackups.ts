import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	createNodeBackupMutation,
	listNodeBackupsOptions,
	listNodeBackupsQueryKey,
	restoreNodeBackupMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";

// `enabled` goes false once a restore was accepted: the node is stopping, so a refetch could only fail.
export function useNodeBackups(enabled: boolean) {
	return useQuery({ ...withResponseValidation(listNodeBackupsOptions()), enabled, retry: false });
}

export function useCreateNodeBackup() {
	const queryClient = useQueryClient();
	return useMutation({
		...withResponseValidation(createNodeBackupMutation()),
		onSuccess: () => queryClient.invalidateQueries({ queryKey: listNodeBackupsQueryKey() }),
	});
}

export function useRestoreNodeBackup() {
	return useMutation(withResponseValidation(restoreNodeBackupMutation()));
}
