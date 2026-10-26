import { Button, Group, List, Stack, Text } from "@mantine/core";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";

import {
	acknowledgeAiMutation,
	getAiAcknowledgementOptions,
	getAiAcknowledgementQueryKey,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { toast } from "@/core/ui/notifications/Toast";

const noop = () => undefined;

// One-time AI responsibility notice, recorded per user on the node. It opens only once the GET says the user has not
// acknowledged (a failed read keeps it closed rather than blocking chat on a transient error) and cannot be dismissed
// any other way than accepting. Raised so it sits above the welcome-tour dialog that can open on the same first visit.
export function AiAcknowledgementGate() {
	const { t } = useTranslation();
	const queryClient = useQueryClient();
	const acknowledgement = useQuery(getAiAcknowledgementOptions());
	const accept = useMutation({
		...acknowledgeAiMutation(),
		onSuccess: (data) => queryClient.setQueryData(getAiAcknowledgementQueryKey(), data),
		onError: () => toast.error(t("onboarding.aiAcknowledgement.saveFailed")),
	});

	return (
		<DialogShell
			opened={acknowledgement.data?.acknowledged === false}
			onClose={noop}
			title={t("onboarding.aiAcknowledgement.title")}
			size="md"
			showCloseButton={false}
			enableFullScreenToggle={false}
			closeOnClickOutside={false}
			closeOnEscape={false}
			raised={true}
			data-testid="ai-acknowledgement-dialog"
		>
			<Stack gap="md" px="md" pb="md">
				<Text>{t("onboarding.aiAcknowledgement.intro")}</Text>
				<List spacing="xs">
					<List.Item>{t("onboarding.aiAcknowledgement.mistakes")}</List.Item>
					<List.Item>{t("onboarding.aiAcknowledgement.checkResults")}</List.Item>
					<List.Item>{t("onboarding.aiAcknowledgement.agentChanges")}</List.Item>
				</List>
				<Group justify="flex-end">
					<Button onClick={() => accept.mutate({})} loading={accept.isPending} data-testid="ai-acknowledgement-accept">
						{t("onboarding.aiAcknowledgement.accept")}
					</Button>
				</Group>
			</Stack>
		</DialogShell>
	);
}
