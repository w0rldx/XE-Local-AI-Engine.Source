import { Alert, Button, CloseButton, Group, Text } from "@mantine/core";
import { IconArrowUpCircle } from "@tabler/icons-react";
import { useNavigate } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";

import { useLlamaCppRuntimeStatus } from "@/features/node-settings/queries/useLocalRuntime";
import { useRuntimeUpdateBannerStore } from "@/features/node-settings/stores/RuntimeUpdateBannerStore";

// Global, subtle "a newer llama.cpp runtime is available" banner, mounted once in the app shell. It subscribes to the
// read-only runtime-status query (safe on mount — no download side effect) and renders only when a runtime is installed,
// the backend's `updateAvailable` flag is set AND the operator has not dismissed THIS recommended tag. The installed
// check matters on first run: the backend reports an update whenever nothing is installed yet, which is provisioning,
// not an update. Dismiss is per-tag (a later, newer recommended tag re-shows the banner). The CTA deep-links to the
// Node Settings "runtimes" section, where the updater panel lives.
export function LlamaCppUpdateBanner() {
	const { t } = useTranslation();
	const navigate = useNavigate();
	const statusQuery = useLlamaCppRuntimeStatus();
	const dismissedTag = useRuntimeUpdateBannerStore((state) => state.dismissedTag);
	const dismiss = useRuntimeUpdateBannerStore((state) => state.dismiss);

	const status = statusQuery.data;
	const recommendedTag = status?.recommendedTag ?? "";
	const shouldShow =
		status?.installed != null && status.updateAvailable && recommendedTag.length > 0 && dismissedTag !== recommendedTag;

	if (!shouldShow) {
		return null;
	}

	return (
		<Alert
			color="primary"
			variant="light"
			icon={<IconArrowUpCircle size={18} />}
			radius={0}
			withCloseButton={false}
			data-testid="llamacpp-update-banner"
		>
			<Group justify="space-between" align="center" wrap="nowrap" gap="sm">
				<Text size="sm">
					{t("pages.nodeSettings.llamaCpp.updateBanner.message", "A newer llama.cpp runtime ({{tag}}) is available.", {
						tag: recommendedTag,
					})}
				</Text>
				<Group gap="xs" wrap="nowrap">
					<Button
						size="xs"
						variant="filled"
						onClick={() => navigate({ to: "/node-settings", search: { section: "runtimes" } })}
						data-testid="llamacpp-update-banner-cta"
					>
						{t("pages.nodeSettings.llamaCpp.updateBanner.review", "Review update")}
					</Button>
					<CloseButton
						aria-label={t("pages.nodeSettings.llamaCpp.updateBanner.dismiss", "Dismiss")}
						onClick={() => dismiss(recommendedTag)}
						data-testid="llamacpp-update-banner-dismiss"
					/>
				</Group>
			</Group>
		</Alert>
	);
}
