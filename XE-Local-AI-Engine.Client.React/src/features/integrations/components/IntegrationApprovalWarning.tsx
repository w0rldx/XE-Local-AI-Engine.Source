import { Alert, Stack, Text } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { resolveApprovalRequiringTools } from "@/features/integrations/models/IntegrationApproval";
import type { IntegrationToolFacts } from "@/features/integrations/models/IntegrationModels";

interface IntegrationApprovalWarningProps {
	allowedToolNames: readonly string[];
	toolApprovals: Readonly<Record<string, boolean>>;
	toolsByName: ReadonlyMap<string, IntegrationToolFacts>;
}

// Warns that the selected agent resolves tools which need a human. An integration run is unattended and fails
// closed instead of asking, so reaching one of these tools ends the run with an error. The resolution is
// fail-closed: a tool the live catalog does not know is treated as approval-requiring.
export function IntegrationApprovalWarning({ allowedToolNames, toolApprovals, toolsByName }: IntegrationApprovalWarningProps) {
	const { t } = useTranslation();
	const tools = resolveApprovalRequiringTools(allowedToolNames, toolApprovals, toolsByName);

	if (tools.length === 0) {
		return null;
	}

	return (
		<Alert
			color="yellow"
			variant="light"
			icon={<IconAlertTriangle size={18} />}
			title={t("pages.integrations.triggers.approvalWarning.title", "Approval-requiring tools")}
			data-testid="integration-approval-warning"
		>
			<Stack gap="xs">
				<Text size="sm">
					{t(
						"pages.integrations.triggers.approvalWarning.body",
						"This agent may call tools that need manual approval. Integration runs are unattended and fail instead of asking, so a run that reaches one of these tools ends with an error.",
					)}
				</Text>
				<Text size="sm" fw={500} data-testid="integration-approval-warning-tools">
					{t("pages.integrations.triggers.approvalWarning.toolsLabel", "Tools: {{tools}}", { tools: tools.join(", ") })}
				</Text>
			</Stack>
		</Alert>
	);
}
