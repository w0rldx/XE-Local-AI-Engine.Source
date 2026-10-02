import { Box, Button, Card, Container, PasswordInput, Stack, Text, Title } from "@mantine/core";
import { IconShieldLock } from "@tabler/icons-react";
import { useNavigate } from "@tanstack/react-router";
import { isAxiosError } from "axios";
import type { FormEvent } from "react";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { confirmNodeVault } from "@/core/auth/api/NodeAuthApi";
import { RecoveryCodeReveal } from "@/core/auth/components/RecoveryCodeReveal";
import type { NodeAuthErrorResponse } from "@/core/auth/models/NodeAuthModels";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { StandaloneScreenControls } from "@/core/ui/components/StandaloneScreenControls/StandaloneScreenControls";

function getErrorMessage(error: unknown, t: (key: string) => string): string {
	if (isAxiosError<NodeAuthErrorResponse>(error)) {
		const status = error.response?.status;
		if (status === 409) {
			return t("auth.vault.errorAlreadyProtected");
		}

		// A wrong password is a 400 carrying the node's own message.
		const errors = error.response?.data?.errors;
		if (status === 400 && errors && errors.length > 0) {
			return errors.join(" ");
		}

		if (!error.response) {
			return t("auth.vault.errorNodeUnreachable");
		}
	}

	return t("auth.vault.errorGeneric");
}

// One-time step for a data dir from before the vault: wraps the existing node key with the login password.
export function VaultSetup() {
	const { t } = useTranslation();
	const navigate = useNavigate();
	const [password, setPassword] = useState("");
	const [error, setError] = useState<string | undefined>();
	const [submitting, setSubmitting] = useState(false);
	const [recoveryCode, setRecoveryCode] = useState<string | undefined>();

	const handleSubmit = async (event: FormEvent<HTMLFormElement>): Promise<void> => {
		event.preventDefault();
		setError(undefined);
		setSubmitting(true);
		try {
			const response = await confirmNodeVault({ password });
			setPassword("");
			setRecoveryCode(response.recoveryCode);
		} catch (submitError) {
			setError(getErrorMessage(submitError, t));
		} finally {
			setSubmitting(false);
		}
	};

	return (
		<Box pos="relative">
			<StandaloneScreenControls />
			<Container size="xs" py="xl" className="min-h-dvh flex items-center">
				<Card withBorder={true} radius="lg" p="xl" className="w-full">
					<Stack gap="lg">
						<Stack gap={4} align="center" ta="center">
							<IconShieldLock size={36} aria-hidden="true" />
							<Title order={1}>{t("auth.vault.setupTitle")}</Title>
						</Stack>
						{recoveryCode ? (
							<RecoveryCodeReveal
								recoveryCode={recoveryCode}
								onContinue={() => {
									navigate({ to: "/" }).catch(() => undefined);
								}}
							/>
						) : (
							<>
								<Text>{t("auth.vault.setupExplanation")}</Text>
								{error ? <InlineErrorAlert message={error} /> : null}
								<form onSubmit={handleSubmit}>
									<Stack gap="md">
										<PasswordInput
											label={t("auth.vault.currentPasswordLabel")}
											autoComplete="current-password"
											required={true}
											value={password}
											onChange={(event) => {
												setPassword(event.currentTarget.value);
											}}
										/>
										<Button type="submit" loading={submitting} disabled={password.length === 0} fullWidth={true}>
											{t("auth.vault.setupButton")}
										</Button>
									</Stack>
								</form>
							</>
						)}
					</Stack>
				</Card>
			</Container>
		</Box>
	);
}
