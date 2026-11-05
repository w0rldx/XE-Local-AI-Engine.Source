import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { toast } from "@/core/ui/notifications/Toast";
import { HfTokenPanel } from "@/features/node-settings/components/HfTokenPanel";
import { useHfTokenStatus, useSetHfToken } from "@/features/node-settings/queries/useLocalRuntime";
import { useHfTokenStore } from "@/features/node-settings/stores/HfTokenStore";

// HF token: the draft lives in a store so it survives a remount; the token itself is write-only (never read back into
// the draft). It saves on its own endpoint, outside the save bar. The handlers await `mutateAsync` rather than passing
// `mutate` callbacks because those are dropped when the card unmounts, and switching section mid-save unmounts it.
export function NodeSettingsHfTokenCard() {
	const { t } = useTranslation();
	const tokenDraft = useHfTokenStore((state) => state.tokenDraft);
	const setTokenDraft = useHfTokenStore((state) => state.actions.setTokenDraft);
	const clearTokenDraft = useHfTokenStore((state) => state.actions.clearTokenDraft);

	const hfTokenQuery = useHfTokenStatus();
	const setHfToken = useSetHfToken();

	const handleSaveToken = async (): Promise<void> => {
		try {
			await setHfToken.mutateAsync(tokenDraft.trim());
		} catch (error) {
			toast.error(apiErrorMessage(error, t("pages.nodeSettings.hfToken.saveError", "Could not save the token.")));
			return;
		}
		clearTokenDraft();
		toast.success(t("pages.nodeSettings.hfToken.saved", "Token saved."));
	};

	const handleClearToken = async (): Promise<void> => {
		try {
			await setHfToken.mutateAsync(undefined);
		} catch (error) {
			toast.error(apiErrorMessage(error, t("pages.nodeSettings.hfToken.clearError", "Could not clear the token.")));
			return;
		}
		clearTokenDraft();
		toast.success(t("pages.nodeSettings.hfToken.cleared", "Token cleared."));
	};

	return (
		<HfTokenPanel
			hasToken={hfTokenQuery.data ?? false}
			isLoading={hfTokenQuery.isLoading}
			tokenDraft={tokenDraft}
			onTokenDraftChange={setTokenDraft}
			onSave={handleSaveToken}
			onClear={handleClearToken}
			isSaving={setHfToken.isPending}
		/>
	);
}
