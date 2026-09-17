import { Button, Center, Paper, Stack, Text, Title } from "@mantine/core";
import { IconAlertCircle } from "@tabler/icons-react";

import type { AppErrorFallbackProps } from "@/AppErrorFallback.types";
import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";

export function AppErrorFallback({ error, onRetry }: AppErrorFallbackProps) {
	const errorMessage = apiErrorMessage(error, "Unknown error");

	return (
		<Center mih="100dvh" p="md">
			<Paper withBorder={true} radius="md" p="xl" maw={560} w="100%">
				<Stack gap="md">
					<InlineErrorAlert
						icon={<IconAlertCircle size={18} />}
						title="Something went wrong"
						variant="light"
						message="The application hit an unexpected error while rendering this page."
					/>
					<Stack gap={4}>
						<Title order={1} size="h3">
							Unable to load this view
						</Title>
						<Text c="dimmed">Try again to re-render the current route.</Text>
					</Stack>
					<Text ff="monospace" size="sm">
						{errorMessage}
					</Text>
					<Button onClick={onRetry}>Try again</Button>
				</Stack>
			</Paper>
		</Center>
	);
}
