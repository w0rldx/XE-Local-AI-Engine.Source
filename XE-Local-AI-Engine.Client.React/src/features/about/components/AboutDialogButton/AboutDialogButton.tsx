import { ActionIcon, Tooltip } from "@mantine/core";
import { useDisclosure } from "@mantine/hooks";
import { IconInfoCircle } from "@tabler/icons-react";
import { lazy, Suspense } from "react";
import { useTranslation } from "react-i18next";

const AboutDialog = lazy(async () => {
	const module = await import("@/features/about/components/AboutDialog/AboutDialog");
	return { default: module.AboutDialog };
});

/**
 * Self-contained About trigger for the HeaderBar action cluster. Owns its own
 * open state and lazy-loads the {@link AboutDialog} on first open. Follows the
 * same pattern as ThemeConfiguratorDialogButton so new header dialogs can be
 * added by dropping another such button into the cluster.
 */
export function AboutDialogButton() {
	const { t } = useTranslation();
	const [opened, { open, close }] = useDisclosure(false);
	const label = t("pages.about.title", "About");

	return (
		<>
			<Tooltip label={label}>
				<ActionIcon onClick={open} variant="default" size="xl" radius="md" aria-label={label}>
					<IconInfoCircle stroke={1.5} />
				</ActionIcon>
			</Tooltip>

			{opened ? (
				<Suspense fallback={null}>
					<AboutDialog opened={opened} onClose={close} />
				</Suspense>
			) : null}
		</>
	);
}
