import { Alert, Box, Button, Card, Container, List, PasswordInput, Stack, Text, TextInput, Title } from "@mantine/core";
import { IconAlertTriangle, IconCircleKey } from "@tabler/icons-react";
import { useNavigate } from "@tanstack/react-router";
import { isAxiosError } from "axios";
import type { FormEvent } from "react";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { loginNodeAuth, setupNodeAuth } from "@/core/auth/api/NodeAuthApi";
import type { NodeAuthErrorResponse } from "@/core/auth/models/NodeAuthModels";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { LanguageMenu } from "@/core/locales/components/LanguageMenu/LanguageMenu";

interface SetupFormValues {
	email: string;
	password: string;
	confirmPassword: string;
}

// Prefer the node's own error payload (NodeAuthErrorResponse carries the exact Identity policy failures) over axios'
// generic "Request failed with status code 400", which tells the operator nothing about what to fix.
function getErrorMessage(error: unknown, t: (key: string) => string): string {
	if (isAxiosError<NodeAuthErrorResponse>(error)) {
		const data = error.response?.data;
		if (data?.errors && data.errors.length > 0) {
			return data.errors.join(" ");
		}

		if (data?.message) {
			return data.message;
		}

		if (!error.response) {
			return t("auth.setup.errorNodeUnreachable");
		}
	}

	return t("auth.setup.errorGeneric");
}

function validate(
	values: SetupFormValues,
	t: (key: string, options?: Record<string, unknown>) => string,
): Partial<Record<keyof SetupFormValues, string>> {
	const errors: Partial<Record<keyof SetupFormValues, string>> = {};

	if (!/^\S+@\S+\.\S+$/.test(values.email.trim())) {
		errors.email = t("auth.setup.validationEmail");
	}

	const password = values.password;
	if (password.length > 0) {
		const unmet: string[] = [];
		if (password.length < 12) {
			unmet.push("12+ characters");
		}
		if (!/[A-Z]/.test(password)) {
			unmet.push("an uppercase letter");
		}
		if (!/[a-z]/.test(password)) {
			unmet.push("a lowercase letter");
		}
		if (!/[0-9]/.test(password)) {
			unmet.push("a digit");
		}
		if (!/[^a-zA-Z0-9]/.test(password)) {
			unmet.push("a symbol");
		}
		if (unmet.length > 0) {
			errors.password = t("auth.setup.validationPasswordWeak", { unmet: unmet.join(", ") });
		}
	} else {
		errors.password = t("auth.setup.validationPasswordRequired");
	}

	if (values.confirmPassword !== values.password) {
		errors.confirmPassword = t("auth.setup.validationPasswordsNoMatch");
	}

	return errors;
}

export function Setup() {
	const { t } = useTranslation();
	const navigate = useNavigate();
	const setToken = useNodeAuthStore((state) => state.actions.setToken);
	const [values, setValues] = useState<SetupFormValues>({ email: "", password: "", confirmPassword: "" });
	const [error, setError] = useState<string | undefined>();
	const [submitting, setSubmitting] = useState(false);
	const errors = useMemo(() => validate(values, t), [values, t]);
	const hasErrors = Object.keys(errors).length > 0;

	// Mirrors the backend ASP.NET Identity password policy (ConfigureServices.cs) so the operator sees the rules up
	// front and gets immediate client-side feedback instead of a server round-trip.
	const passwordRules = [
		t("auth.setup.passwordRuleLength"),
		t("auth.setup.passwordRuleUppercase"),
		t("auth.setup.passwordRuleLowercase"),
		t("auth.setup.passwordRuleDigit"),
		t("auth.setup.passwordRuleSymbol"),
	];

	const handleSubmit = async (event: FormEvent<HTMLFormElement>): Promise<void> => {
		event.preventDefault();
		setError(undefined);

		if (hasErrors) {
			return;
		}

		setSubmitting(true);
		try {
			const email = values.email.trim();
			await setupNodeAuth({ email, password: values.password });
			const token = await loginNodeAuth({ email, password: values.password });
			setToken(token);
			await navigate({ to: "/" });
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
							<IconCircleKey size={36} aria-hidden="true" />
							<Title order={1}>{t("auth.setup.title")}</Title>
							<Text c="dimmed">{t("auth.setup.subtitle")}</Text>
						</Stack>

						{error ? (
							<Alert color="red" icon={<IconAlertTriangle size={16} />}>
								{error}
							</Alert>
						) : null}

						<form onSubmit={handleSubmit}>
							<Stack gap="md">
								<TextInput
									label={t("auth.setup.emailLabel")}
									description={t("auth.setup.emailLocalOnlyNote")}
									type="email"
									autoComplete="email"
									required={true}
									value={values.email}
									onChange={(event) => {
										const value = event.currentTarget.value;
										setValues((current) => ({ ...current, email: value }));
									}}
									error={errors.email}
								/>
								<Stack gap={4}>
									<PasswordInput
										label={t("auth.setup.passwordLabel")}
										autoComplete="new-password"
										required={true}
										value={values.password}
										onChange={(event) => {
											const value = event.currentTarget.value;
											setValues((current) => ({ ...current, password: value }));
										}}
										error={errors.password}
									/>
									<List size="xs" c="dimmed" spacing={0} withPadding={true}>
										{passwordRules.map((rule) => (
											<List.Item key={rule}>{rule}</List.Item>
										))}
									</List>
								</Stack>
								<PasswordInput
									label={t("auth.setup.confirmPasswordLabel")}
									autoComplete="new-password"
									required={true}
									value={values.confirmPassword}
									onChange={(event) => {
										const value = event.currentTarget.value;
										setValues((current) => ({ ...current, confirmPassword: value }));
									}}
									error={errors.confirmPassword}
								/>
								<Button type="submit" loading={submitting} disabled={hasErrors} fullWidth={true}>
									{t("auth.setup.createButton")}
								</Button>
							</Stack>
						</form>
					</Stack>
				</Card>
			</Container>
		</Box>
	);
}
