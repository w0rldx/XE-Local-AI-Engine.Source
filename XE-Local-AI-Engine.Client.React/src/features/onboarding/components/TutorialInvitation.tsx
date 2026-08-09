import { Alert, Button, Group, Text } from "@mantine/core";
import { IconRoute } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { useOnboarding } from "@/features/onboarding/context/OnboardingContext";
import type { TutorialId } from "@/features/onboarding/data/TutorialRegistry";

export function TutorialInvitation({ tutorialId }: { tutorialId: Exclude<TutorialId, "quick-start"> }) {
	const { t } = useTranslation();
	const onboarding = useOnboarding();
	if (!onboarding) {
		return null;
	}
	const tutorial = onboarding.tutorials[tutorialId];
	if (!onboarding.isStateSuccessful || !tutorial.isAvailable || tutorial.status !== undefined) {
		return null;
	}
	const action = tutorial.hasProgress ? "resume" : "start";
	return (
		<Alert
			icon={<IconRoute size={18} />}
			title={t(`onboarding.tutorials.${tutorialId}.invitationTitle`)}
			withCloseButton={true}
			onClose={() => onboarding.dismiss(tutorialId)}
			data-testid={`tutorial-invitation-${tutorialId}`}
		>
			<Text size="sm" mb="sm">
				{t(`onboarding.tutorials.${tutorialId}.invitationBody`)}
			</Text>
			<Group gap="sm">
				<Button size="xs" onClick={() => onboarding[action](tutorialId)}>
					{t(`onboarding.actions.${action}`)}
				</Button>
				<Button size="xs" variant="subtle" onClick={() => onboarding.dismiss(tutorialId)}>
					{t("onboarding.actions.notNow")}
				</Button>
			</Group>
		</Alert>
	);
}
