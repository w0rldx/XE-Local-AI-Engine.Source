import { Badge } from "@mantine/core";
import { useTranslation } from "react-i18next";

// Marks a control that is stored in this browser, not on the node, and so is neither part of the save bar nor shared
// with other users.
export function NodeSettingsBrowserOnlyBadge() {
	const { t } = useTranslation();
	return (
		<Badge size="sm" variant="outline" color="gray" data-testid="node-settings-browser-only-badge">
			{t("pages.nodeSettings.browserOnly", "This browser only")}
		</Badge>
	);
}
