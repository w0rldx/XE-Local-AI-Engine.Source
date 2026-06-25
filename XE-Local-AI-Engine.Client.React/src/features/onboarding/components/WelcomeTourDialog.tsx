import { Button, Group, Stack, Text } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";

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
	const { t } = useTranslation();

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
