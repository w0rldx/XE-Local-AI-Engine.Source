import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	getNodeSettingsOptions,
	getNodeSettingsQueryKey,
	saveNodeSettingsMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";

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

export function useVoiceNodeSettings(): UseVoiceNodeSettingsResult {
	const queryClient = useQueryClient();
	// The read is gated on a session, not on a caller-supplied flag: `ClientAiRuntimeProvider` mounts at the app root,
	// outside the `_layout` auth guard, and the access token lives in memory only. Without this gate every fresh page
	// load — including the login and setup screens, where no session exists at all — would fire a 401 the interceptor
	// then has to refresh and retry. Reading the store here rather than taking a parameter keeps both call sites from
	// being able to get it wrong.
	const isAuthenticated = useNodeAuthStore((state) => Boolean(state.accessToken));
	const settingsQuery = useQuery({
		...withResponseValidation(getNodeSettingsOptions()),
		enabled: isAuthenticated,
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

	// The session is read a second time on the way out, not only on the way in. Logout clears the auth store alone
	// (`useNodeLogout` → `clearAuth`), and TanStack Query keeps a cache entry's `data` when `enabled` flips to false —
	// it stops refetching, it does not forget. Without this, a node gate that was on before logout is still believed
	// on the login screen for as long as that entry lives, which is exactly the state the token gate exists to end.
	const settings = isAuthenticated ? settingsQuery.data : undefined;

	return {
		voiceFeatureEnabled: settings?.voiceFeatureEnabled ?? false,
		defaultVoiceProfile: settings?.defaultVoiceProfile ?? undefined,
		isLoading: settingsQuery.isLoading,
		isSaving: saveMutation.isPending,
		save,
	};
}
