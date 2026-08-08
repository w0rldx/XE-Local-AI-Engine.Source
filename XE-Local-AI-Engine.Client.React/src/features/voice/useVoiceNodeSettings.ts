import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	getNodeSettingsOptions,
	getNodeSettingsQueryKey,
	saveNodeSettingsMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";

// Operator-owned voice node settings. Reuses the existing node-settings GET/PUT (the same operator-gated endpoint the
// Node Settings page already drives) to read + write the node-level voice fields: the master gate `voiceFeatureEnabled`
// and the `defaultVoiceProfile`. The PUT is additive —
// the backend merges a null field into the current stored value — so writing only `{ voiceFeatureEnabled }` leaves every
// other node setting untouched. On success the shared node-settings query is invalidated so both the card and the
// app-root runtime re-read the persisted gate immediately.

interface VoiceNodeSettingsPatch {
	readonly voiceFeatureEnabled?: boolean;
	readonly defaultVoiceProfile?: string;
}

interface SaveOptions {
	readonly onError?: (error: unknown) => void;
}

export interface UseVoiceNodeSettingsResult {
	readonly voiceFeatureEnabled: boolean;
	readonly defaultVoiceProfile: string | undefined;
	readonly isLoading: boolean;
	readonly isSaving: boolean;
	readonly save: (patch: VoiceNodeSettingsPatch, options?: SaveOptions) => void;
}

export function useVoiceNodeSettings(enabled: boolean): UseVoiceNodeSettingsResult {
	const queryClient = useQueryClient();
	const settingsQuery = useQuery({
		...withResponseValidation(getNodeSettingsOptions()),
		enabled,
	});

	const saveMutation = useMutation({
		...withResponseValidation(saveNodeSettingsMutation()),
		onSuccess: async () => {
			await queryClient.invalidateQueries({ queryKey: getNodeSettingsQueryKey() });
		},
	});

	const save = (patch: VoiceNodeSettingsPatch, options?: SaveOptions): void => {
		saveMutation.mutate({ body: patch }, { onError: (error) => options?.onError?.(error) });
	};

	return {
		voiceFeatureEnabled: settingsQuery.data?.voiceFeatureEnabled ?? false,
		defaultVoiceProfile: settingsQuery.data?.defaultVoiceProfile ?? undefined,
		isLoading: settingsQuery.isLoading,
		isSaving: saveMutation.isPending,
		save,
	};
}
