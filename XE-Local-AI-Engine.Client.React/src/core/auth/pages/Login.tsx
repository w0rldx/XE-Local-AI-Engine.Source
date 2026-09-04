import { Alert, Box, Button, Card, Container, PasswordInput, Stack, Text, Title } from "@mantine/core";
import { IconAlertTriangle, IconLock } from "@tabler/icons-react";
import { useNavigate, useSearch } from "@tanstack/react-router";
import { isAxiosError } from "axios";
import type { FormEvent } from "react";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { loginNodeAuth } from "@/core/auth/api/NodeAuthApi";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { getSafeRedirectPath } from "@/core/auth/utils/RedirectPath";
import { LanguageMenu } from "@/core/locales/components/LanguageMenu/LanguageMenu";

// The node answers a locked account with a 401 carrying this body; a wrong password answers a body-less 401.
interface LoginLockedOutBody {
	code?: string;
	retryAfterSeconds?: number;
}

// Translate the raw failure into an operator-facing message instead of surfacing axios' "Request failed with
// status code 401". 401 = wrong password, unless the body says the account is locked out; a missing response = the
// node is unreachable; anything else is generic.
function getErrorMessage(error: unknown, t: (key: string, options?: Record<string, unknown>) => string): string {
	if (isAxiosError(error)) {
		if (error.response?.status === 401) {
			const body = error.response.data as LoginLockedOutBody | undefined;
			if (body?.code === "locked-out") {
				// Round up so "0 min" is never shown; the node already floors the value at one second.
				return t("auth.login.errorLockedOut", { minutes: Math.max(1, Math.ceil((body.retryAfterSeconds ?? 0) / 60)) });
			}

			return t("auth.login.errorIncorrectPassword");
		}

		if (!error.response) {
			return t("auth.login.errorNodeUnreachable");
		}
	}

	return t("auth.login.errorGeneric");
}

export function Login() {
	const { t } = useTranslation();
	const navigate = useNavigate();
	const { redirect } = useSearch({ from: "/login" });
	const setToken = useNodeAuthStore((state) => state.actions.setToken);
	const [password, setPassword] = useState("");
	const [error, setError] = useState<string | undefined>();
	const [submitting, setSubmitting] = useState(false);
	const safeRedirect = getSafeRedirectPath(redirect);

	const handleSubmit = async (event: FormEvent<HTMLFormElement>): Promise<void> => {
		event.preventDefault();
		setError(undefined);
		setSubmitting(true);

		try {
			const token = await loginNodeAuth({ password });
			setToken(token);
			await navigate({ to: safeRedirect });
		} catch (submitError) {
			setError(getErrorMessage(submitError, t));
		} finally {
			setSubmitting(false);
		}
	};

	return (
		<Box pos="relative">
			<Box pos="absolute" top={16} right={16} style={{ zIndex: 10 }}>
				<LanguageMenu />
			</Box>
			<Container size="xs" py="xl" className="min-h-dvh flex items-center">
				<Card withBorder={true} radius="lg" p="xl" className="w-full">
					<Stack gap="lg">
						<Stack gap={4} align="center" ta="center">
							<IconLock size={36} aria-hidden="true" />
							<Title order={1}>{t("auth.login.title")}</Title>
							<Text c="dimmed">{t("auth.login.subtitle")}</Text>
						</Stack>

						{error ? (
							<Alert color="red" icon={<IconAlertTriangle size={16} />}>
								{error}
							</Alert>
						) : null}

						<form onSubmit={handleSubmit}>
							<Stack gap="md">
								<PasswordInput
									label={t("auth.login.passwordLabel")}
									autoComplete="current-password"
									required={true}
									value={password}
									onChange={(event) => {
										setPassword(event.currentTarget.value);
									}}
								/>
								<Button type="submit" loading={submitting} disabled={password.length === 0} fullWidth={true}>
									{t("auth.login.signInButton")}
								</Button>
							</Stack>
						</form>
					</Stack>
				</Card>
			</Container>
		</Box>
	);
}
