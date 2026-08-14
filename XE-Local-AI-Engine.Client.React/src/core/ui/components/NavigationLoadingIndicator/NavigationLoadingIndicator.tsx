import { Loader, Overlay, Paper, Text } from "@mantine/core";
import { useTranslation } from "react-i18next";

import "./NavigationLoadingIndicator.css";

export function NavigationLoadingIndicator() {
	const { t } = useTranslation();

	return (
		<div className="navigation-loading-indicator">
			<Overlay color="#000" backgroundOpacity={0.5} />
			<Paper className="navigation-loading-indicator-panel" radius="md" p="md" shadow="md">
				<Loader size={50} />
				<Text fw={600} className="mt-4">
					{t("common.loading")}
				</Text>
			</Paper>
		</div>
	);
}
