import { ActionIcon, Group, Menu, Text, Tooltip } from "@mantine/core";
import { IconCheck, IconLanguage } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { useUserLanguageStore } from "@/core/locales/stores/UserLanguageStore";
import { languageData } from "@/data/language/LanguageMenuData";

export function LanguageMenu() {
	const { i18n, t } = useTranslation();
	const { selectedApplicationLanguage, changeLanguage } = useUserLanguageStore();

	const handleLanguageChange = async (language: string) => {
		await i18n.changeLanguage(language);
		changeLanguage(language);
	};

	if (languageData.length <= 1) {
		return null;
	}

	return (
		<Menu shadow="md" width={220} position="bottom-end">
			<Menu.Target>
				<Tooltip label={t("components.languageMenu.tooltip")}>
					<ActionIcon variant="default" size="xl" radius="md" aria-label={t("components.languageMenu.tooltip")}>
						<IconLanguage stroke={1.5} />
					</ActionIcon>
				</Tooltip>
			</Menu.Target>
			<Menu.Dropdown>
				{languageData.map((language) => (
					<Menu.Item key={language.value} onClick={() => handleLanguageChange(language.value)}>
						<Group gap="xs" justify="space-between" wrap="nowrap">
							<Text>{language.text}</Text>
							{selectedApplicationLanguage === language.value && (
								<span>
									<IconCheck />
								</span>
							)}
						</Group>
					</Menu.Item>
				))}
			</Menu.Dropdown>
		</Menu>
	);
}
