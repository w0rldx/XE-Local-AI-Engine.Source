import { Loader, Overlay, Paper, Text } from "@mantine/core";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";

import { refreshRetryWaitSeconds } from "@/core/auth/api/NodeAuthApi";

import "./NavigationLoadingIndicator.css";

export function NavigationLoadingIndicator() {
	const { t } = useTranslation();
	// A rate-limited boot sits out the node's Retry-After inside the token refresh; a once-a-second read counts it down.
	const [waitSeconds, setWaitSeconds] = useState(refreshRetryWaitSeconds);
	useEffect(() => {
		const timer = setInterval(() => setWaitSeconds(refreshRetryWaitSeconds()), 1000);
		return () => clearInterval(timer);
	}, []);

	return (
		<div className="navigation-loading-indicator">
			<Overlay color="#000" backgroundOpacity={0.5} />
			<Paper className="navigation-loading-indicator-panel" radius="md" p="md" shadow="md">
				<Loader size={50} />
				<Text fw={600} className="mt-4">
					{t("common.loading")}
				</Text>
				{waitSeconds > 0 ? (
					<Text size="sm" c="dimmed" data-testid="navigation-loading-rate-limited">
						{t("common.loadingRateLimited", "Too many requests to the node. Retrying in {{seconds}} s.", {
							seconds: waitSeconds,
						})}
					</Text>
				) : null}
			</Paper>
		</div>
	);
}
