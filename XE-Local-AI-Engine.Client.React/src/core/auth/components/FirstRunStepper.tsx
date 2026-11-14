import { Progress, Stack, Text } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { type FirstRunStep, firstRunSteps } from "@/core/auth/utils/FirstRunGuard";

// "Step n of N" above each first-run screen, numbered from the guard's own step list so the two cannot disagree.
export function FirstRunStepper({ step }: { readonly step: FirstRunStep }) {
	const { t } = useTranslation();
	const current = firstRunSteps.indexOf(step) + 1;
	const names: Record<FirstRunStep, string> = {
		account: t("auth.setup.stepper.account", "Account"),
		vault: t("auth.setup.stepper.vault", "Protect your data"),
		externalAccess: t("auth.setup.stepper.externalAccess", "Internet access"),
	};

	return (
		<Stack gap={6} data-testid="first-run-stepper">
			<Text size="sm" c="dimmed" ta="center">
				{t("auth.setup.stepper.progress", "Step {{current}} of {{total}}: {{name}}", {
					current,
					total: firstRunSteps.length,
					name: names[step],
				})}
			</Text>
			<Progress value={(current / firstRunSteps.length) * 100} size="sm" aria-hidden="true" />
		</Stack>
	);
}
