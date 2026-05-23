import { ActionIcon, Modal, Text, Tooltip } from "@mantine/core";
import { IconPalette } from "@tabler/icons-react";
import { lazy, Suspense, useState } from "react";
import { useTranslation } from "react-i18next";

import { DialogTextTitleBar } from "@/core/ui/components/DialogTextTitleBar/DialogTextTitleBar";

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

			<Modal
				opened={open}
				onClose={() => {
					setOpen(false);
				}}
				size="70rem"
				withCloseButton={false}
				closeOnClickOutside={false}
			>
				<div className="flex flex-col gap-3">
					<DialogTextTitleBar title={t("pages.userSettings.themeConfigurator.title")} handleClose={() => setOpen(false)} />
					<div className="px-4 pb-4">
						<Suspense fallback={<Text size="sm">{t("common.loading")}</Text>}>
							<ThemeConfigurator />
						</Suspense>
					</div>
				</div>
			</Modal>
		</>
	);
}
