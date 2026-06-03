import { ActionIcon, Text, Tooltip } from "@mantine/core";
import { IconPalette } from "@tabler/icons-react";
import { lazy, Suspense, useState } from "react";
import { useTranslation } from "react-i18next";

import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";

const ThemeConfigurator = lazy(async () => {
	const module = await import("@/modules/theme-configurator/components/ThemeConfigurator");
	return { default: module.ThemeConfigurator };
});

export function ThemeConfiguratorDialogButton() {
	const { t } = useTranslation();
	const [open, setOpen] = useState(false);

	return (
		<>
			<Tooltip label={t("pages.userSettings.themeConfigurator.title")}>
				<ActionIcon
					onClick={() => setOpen(true)}
					variant="default"
					size="xl"
					radius="md"
					aria-label={t("pages.userSettings.themeConfigurator.title")}
				>
					<IconPalette stroke={1.5} />
				</ActionIcon>
			</Tooltip>

			<DialogShell
				opened={open}
				onClose={() => setOpen(false)}
				title={t("pages.userSettings.themeConfigurator.title")}
				size="70rem"
				showCloseButton={true}
				closeOnClickOutside={false}
			>
				<div className="px-4 pb-4">
					<Suspense fallback={<Text size="sm">{t("common.loading")}</Text>}>
						<ThemeConfigurator />
					</Suspense>
				</div>
			</DialogShell>
		</>
	);
}
