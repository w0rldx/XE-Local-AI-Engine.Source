import { ActionIcon, Tooltip } from "@mantine/core";
import { IconMoon, IconSun } from "@tabler/icons-react";
import cx from "clsx";
import { useTranslation } from "react-i18next";

import { useTheme } from "@/core/theme/hooks/useTheme";

import classes from "./ThemeModeToggle.module.css";

export function ThemeModeToggle() {
	const { t } = useTranslation();
	const { mode, toggleColorMode } = useTheme();

	return (
		<Tooltip label={mode === "light" ? t("theme.switchToDark") : t("theme.switchToLight")}>
			<ActionIcon onClick={() => toggleColorMode()} variant="default" size="xl" radius="md" aria-label="Toggle color scheme">
				<IconSun className={cx(classes["icon"], classes["light"])} stroke={1.5} />
				<IconMoon className={cx(classes["icon"], classes["dark"])} stroke={1.5} />
			</ActionIcon>
		</Tooltip>
	);
}
