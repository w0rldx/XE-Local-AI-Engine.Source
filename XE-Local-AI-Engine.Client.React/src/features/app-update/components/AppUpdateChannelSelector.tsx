import { Select, Stack, Text } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { channelCopy } from "@/features/app-update/models/AppUpdateChannelCopy";
import { useAppUpdateStatus, useSetAppUpdateChannel } from "@/features/app-update/queries/useAppUpdate";

/**
 * The update-channel picker inside the About dialog's update section. Changing the channel only changes what the
 * node looks for — it never downloads, applies or restarts anything.
 */
export function AppUpdateChannelSelector() {
	const { t } = useTranslation();
	const { data: status } = useAppUpdateStatus();
	const channelMutation = useSetAppUpdateChannel();

	// Every condition is the server's answer, never client-side eligibility logic. A build with no update source
	// hides the picker for the same reason it hides the refresh button.
	if (!status?.isDesktop || !status.isConfigured || status.availableChannels.length === 0) {
		return null;
	}

	const copy = channelCopy(t);
	const options = status.availableChannels.map((value) => ({ value, label: copy[value]?.label ?? value }));
	const selectedChannel = status.selectedChannel;

	return (
		<Stack gap="xs">
			{channelMutation.isError ? (
				<InlineErrorAlert
					message={apiErrorMessage(channelMutation.error, t("pages.about.appUpdate.channelChangeError"))}
					data-testid="app-update-channel-error"
				/>
			) : null}

			<Select
				label={t("pages.about.appUpdate.channelLabel")}
				description={t("pages.about.appUpdate.channelDescription")}
				data={options}
				value={selectedChannel}
				onChange={(value) => {
					// null is the clear action, and re-picking the stored channel would cost a live GitHub check for
					// nothing — the endpoint bypasses the rate floor deliberately.
					if (value !== null && value !== selectedChannel) {
						channelMutation.mutate({ body: { channel: value } });
					}
				}}
				allowDeselect={false}
				disabled={channelMutation.isPending}
				data-testid="app-update-channel-select"
			/>

			<Text size="sm" c="dimmed" data-testid="app-update-channel-description">
				{copy[selectedChannel]?.description ?? ""}
			</Text>

			{status.recommendedVersion != null && status.recommendedVersion !== status.currentVersion ? (
				<Text size="sm" c="dimmed" data-testid="app-update-recommended-version">
					{t("pages.about.appUpdate.recommendedVersion", { version: status.recommendedVersion })}
				</Text>
			) : null}
		</Stack>
	);
}
