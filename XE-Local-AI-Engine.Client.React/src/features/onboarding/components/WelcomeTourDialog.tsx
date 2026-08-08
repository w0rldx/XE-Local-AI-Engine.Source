import { Button, Group, Input, SegmentedControl, Stack, Text } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { useUserLanguageStore } from "@/core/locales/stores/UserLanguageStore";
import { languageData } from "@/data/language/LanguageMenuData";

export interface WelcomeTourDialogProps {
	opened: boolean;
	// Start runs the controlled tour from the first step. Skip records the tour as skipped (so it never re-prompts).
	onStart: () => void;
	onSkip: () => void;
}

// Opt-in gate for the first-response tour (not a Joyride step itself). Built on the shared DialogShell so it
// matches every other modal. Copy is fully i18n-keyed. Closing via the title bar is treated as Skip so a dismiss still
// records a terminal outcome and the dialog never re-prompts.
export function WelcomeTourDialog({ opened, onStart, onSkip }: WelcomeTourDialogProps) {
	const { i18n, t } = useTranslation();
	const { selectedApplicationLanguage, changeLanguage } = useUserLanguageStore();

	// Switch the UI language live before the tour begins so every subsequent screen (and the tour itself) renders in the
	// chosen language. Mirrors LanguageMenu: i18next.changeLanguage drives react-i18next + persists to localStorage
	// (i18nextLng), and the store action keeps the selected-language UI state in sync.
	const handleLanguageChange = async (language: string) => {
		await i18n.changeLanguage(language);
		changeLanguage(language);
	};

	const languageOptions = languageData.map((language) => ({
		value: language.value,
		label: language.icon ? `${language.icon} ${language.text}` : language.text,
	}));

	return (
		<DialogShell
			opened={opened}
			onClose={onSkip}
			title={t("onboarding.welcome.title")}
			size="md"
			data-testid="onboarding-welcome-dialog"
		>
			<Stack gap="lg" px="md" pb="md">
				<Text>{t("onboarding.welcome.body")}</Text>
				{languageOptions.length > 1 && (
					<Input.Wrapper label={t("onboarding.welcome.languageLabel")}>
						<SegmentedControl
							fullWidth={true}
							value={selectedApplicationLanguage}
							onChange={handleLanguageChange}
							data={languageOptions}
							aria-label={t("onboarding.welcome.languageLabel")}
							data-testid="onboarding-welcome-language"
						/>
					</Input.Wrapper>
				)}
				<Group justify="flex-end" gap="sm">
					<Button variant="default" onClick={onSkip} data-testid="onboarding-welcome-skip">
						{t("onboarding.welcome.skip")}
					</Button>
					<Button onClick={onStart} data-testid="onboarding-welcome-start">
						{t("onboarding.welcome.start")}
					</Button>
				</Group>
			</Stack>
		</DialogShell>
	);
}
