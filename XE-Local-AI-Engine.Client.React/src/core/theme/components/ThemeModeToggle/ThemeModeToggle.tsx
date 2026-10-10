import { ActionIcon, Tooltip, useComputedColorScheme, useMantineColorScheme } from "@mantine/core";
import { IconMoon, IconSun } from "@tabler/icons-react";
import cx from "clsx";
import { useTranslation } from "react-i18next";

import classes from "./ThemeModeToggle.module.css";

export function ThemeModeToggle() {
	const { t } = useTranslation();
	const { setColorScheme } = useMantineColorScheme();
	// The computed scheme resolves "auto" to what is on screen, so the first click from System is an explicit choice.
	const computedColorScheme = useComputedColorScheme("light");

	return (
		<Tooltip label={computedColorScheme === "light" ? t("theme.switchToDark") : t("theme.switchToLight")}>
			<ActionIcon
				onClick={() => setColorScheme(computedColorScheme === "dark" ? "light" : "dark")}
				variant="default"
				size="xl"
				radius="md"
				aria-label={t("theme.toggleColorScheme")}
			>
				<IconSun className={cx(classes["icon"], classes["light"])} stroke={1.5} />
				<IconMoon className={cx(classes["icon"], classes["dark"])} stroke={1.5} />
			</ActionIcon>
		</Tooltip>
	);
}
