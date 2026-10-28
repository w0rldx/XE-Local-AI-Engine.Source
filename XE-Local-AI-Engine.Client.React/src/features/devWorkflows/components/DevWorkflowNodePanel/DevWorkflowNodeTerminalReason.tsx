import { Stack, Text } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { devWorkflowRunnerNotInstalledHint } from "@/features/devWorkflows/models/DevWorkflowValidationReport";

/**
 * A stood-down node run's terminal reason, verbatim — except a missing test runner, which gets a sentence saying it is
 * the host's to fix, above the server's install hint. Shared by the Failed alert and the gate panel, which both show it.
 */
export function DevWorkflowNodeTerminalReason({ reason }: { readonly reason: string | null | undefined }) {
	const { t } = useTranslation();
	if (!reason) {
		return null;
	}

	const hint = devWorkflowRunnerNotInstalledHint(reason);
	if (hint === null) {
		return (
			<Text size="xs" c="dimmed" style={{ whiteSpace: "pre-wrap" }}>
				{reason}
			</Text>
		);
	}

	return (
		<Stack gap={2} data-testid="dev-workflow-node-runner-not-installed">
			<Text size="sm">
				{t(
					"pages.development.validation.parseFailure.runnerNotInstalled",
					"These tests' runner could not be started on this host: it is not installed, or the filesystem boundary could not reach it. The detail says which; XE never installs it.",
				)}
			</Text>
			<Text size="xs" c="dimmed" style={{ whiteSpace: "pre-wrap" }}>
				{hint}
			</Text>
		</Stack>
	);
}
