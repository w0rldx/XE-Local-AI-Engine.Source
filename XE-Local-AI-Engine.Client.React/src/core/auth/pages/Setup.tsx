import { Alert, Button, Card, Container, PasswordInput, Stack, Text, TextInput, Title } from "@mantine/core";
import { IconAlertTriangle, IconCircleKey } from "@tabler/icons-react";
import { useNavigate } from "@tanstack/react-router";
import type { FormEvent } from "react";
import { useMemo, useState } from "react";

import { loginNodeAuth, setupNodeAuth } from "@/core/auth/api/NodeAuthApi";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";

interface SetupFormValues {
	email: string;
	password: string;
	confirmPassword: string;
}

function getErrorMessage(error: unknown): string {
	if (error instanceof Error) {
		return error.message;
	}

	return "Setup failed. Check the inputs and try again.";
}

function validate(values: SetupFormValues): Partial<Record<keyof SetupFormValues, string>> {
	const errors: Partial<Record<keyof SetupFormValues, string>> = {};

	if (!/^\S+@\S+\.\S+$/.test(values.email.trim())) {
		errors.email = "Enter a valid email address.";
	}

	if (values.password.length < 12) {
		errors.password = "Password must be at least 12 characters.";
	}

	if (values.confirmPassword !== values.password) {
		errors.confirmPassword = "Passwords do not match.";
	}

	return errors;
}

export function Setup() {
	const navigate = useNavigate();
	const setToken = useNodeAuthStore((state) => state.actions.setToken);
	const [values, setValues] = useState<SetupFormValues>({ email: "", password: "", confirmPassword: "" });
	const [error, setError] = useState<string | undefined>();
	const [submitting, setSubmitting] = useState(false);
	const errors = useMemo(() => validate(values), [values]);
	const hasErrors = Object.keys(errors).length > 0;

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
			setError(getErrorMessage(submitError));
		} finally {
			setSubmitting(false);
		}
	};

	return (
		<Container size="xs" py="xl" className="min-h-dvh flex items-center">
			<Card withBorder={true} radius="lg" p="xl" className="w-full">
				<Stack gap="lg">
					<Stack gap={4} align="center" ta="center">
						<IconCircleKey size={36} aria-hidden="true" />
						<Title order={1}>Set up local admin</Title>
						<Text c="dimmed">Create the one-time admin account for this node.</Text>
					</Stack>

					{error ? (
						<Alert color="red" icon={<IconAlertTriangle size={16} />}>
							{error}
						</Alert>
					) : null}

					<form onSubmit={handleSubmit}>
						<Stack gap="md">
							<TextInput
								label="Email"
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
							<PasswordInput
								label="Password"
								autoComplete="new-password"
								required={true}
								value={values.password}
								onChange={(event) => {
									const value = event.currentTarget.value;
									setValues((current) => ({ ...current, password: value }));
								}}
								error={errors.password}
							/>
							<PasswordInput
								label="Confirm password"
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
								Create admin
							</Button>
						</Stack>
					</form>
				</Stack>
			</Card>
		</Container>
	);
}
