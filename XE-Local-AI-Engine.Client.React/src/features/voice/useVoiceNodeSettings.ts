import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	getNodeSettingsOptions,
	getNodeSettingsQueryKey,
	getVoiceManifestQueryKey,
	saveNodeSettingsMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";

// Operator-owned voice node settings. Reuses the existing node-settings GET/PUT (the same operator-gated endpoint the
// Node Settings page already drives) to read + write the node-level voice fields: the master gate `voiceFeatureEnabled`
// (which composes server-side into the voice manifest's `enabled`) and the `defaultVoiceProfile`. The PUT is additive —
// the backend merges a null field into the current stored value — so writing only `{ voiceFeatureEnabled }` leaves every
// other node setting untouched. On a successful save we invalidate BOTH the node-settings query (so this card re-reads
// the persisted value) and the voice manifest query (so the surface gate re-evaluates immediately).

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
			// Re-read the persisted node settings, then re-evaluate the operator gate by refreshing the manifest.
			await Promise.all([
				queryClient.invalidateQueries({ queryKey: getNodeSettingsQueryKey() }),
				queryClient.invalidateQueries({ queryKey: getVoiceManifestQueryKey() }),
			]);
		},
	});

	const save = (patch: VoiceNodeSettingsPatch, options?: SaveOptions): void => {
		saveMutation.mutate(
			{ body: patch },
			{ onError: (error) => options?.onError?.(error) },
		);
	};

	return {
		voiceFeatureEnabled: settingsQuery.data?.voiceFeatureEnabled ?? false,
		defaultVoiceProfile: settingsQuery.data?.defaultVoiceProfile ?? undefined,
		isLoading: settingsQuery.isLoading,
		isSaving: saveMutation.isPending,
		save,
	};
}
