import { Badge, Tooltip } from "@mantine/core";
import { useTranslation } from "react-i18next";

// External Apps is frozen and experimental (ADR 0010/0011 amendment 2026-11-12), so every page of it says so.
export function ExternalAppsExperimentalBadge() {
	const { t } = useTranslation();
	return (
		<Tooltip label={t("pages.externalApps.experimental.tooltip")} multiline={true} w={280}>
			<Badge variant="light" color="orange" data-testid="external-apps-experimental-badge">
				{t("pages.externalApps.experimental.badge")}
			</Badge>
		</Tooltip>
	);
}
