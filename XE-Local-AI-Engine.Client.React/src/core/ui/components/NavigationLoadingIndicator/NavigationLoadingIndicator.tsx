import { Loader, Overlay, Paper, Text } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { useAppTheme as useTheme } from "@/core/theme/hooks/useAppTheme";

import "./NavigationLoadingIndicator.css";

export function NavigationLoadingIndicator() {
	const { t } = useTranslation();
	const theme = useTheme();

	return (
		<div className="navigation-loading-indicator" style={{ zIndex: theme.zIndex.drawer + 1 }}>
			<Overlay color="#000" backgroundOpacity={0.5} />
			<Paper
				className="navigation-loading-indicator-panel"
				style={{
					backgroundColor: theme.palette.background.default,
					borderRadius: theme.shape.borderRadius,
					padding: theme.spacing(2),
					boxShadow: theme.shadows[2],
				}}
			>
				<Loader size={50} color={theme.palette.primary.main} />
				<Text fw={600} className="mt-4">
					{t("common.loading")}
				</Text>
			</Paper>
		</div>
	);
}
