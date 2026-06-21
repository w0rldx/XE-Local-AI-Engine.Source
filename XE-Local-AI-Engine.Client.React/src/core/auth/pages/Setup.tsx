import { Alert, Button, Card, Container, List, PasswordInput, Stack, Text, TextInput, Title } from "@mantine/core";
import { IconAlertTriangle, IconCircleKey } from "@tabler/icons-react";
import { useNavigate } from "@tanstack/react-router";
import { isAxiosError } from "axios";
import type { FormEvent } from "react";
import { useMemo, useState } from "react";

import { loginNodeAuth, setupNodeAuth } from "@/core/auth/api/NodeAuthApi";
import type { NodeAuthErrorResponse } from "@/core/auth/models/NodeAuthModels";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";

interface SetupFormValues {
	email: string;
	password: string;
	confirmPassword: string;
}

// Mirrors the backend ASP.NET Identity password policy (ConfigureServices.cs) so the operator sees the rules up
// front and gets immediate client-side feedback instead of a server round-trip.
const PASSWORD_RULES = [
	"At least 12 characters",
	"At least one uppercase letter (A–Z)",
	"At least one lowercase letter (a–z)",
	"At least one digit (0–9)",
	"At least one symbol (e.g. !@#$%)",
] as const;

// Prefer the node's own error payload (NodeAuthErrorResponse carries the exact Identity policy failures) over axios'
// generic "Request failed with status code 400", which tells the operator nothing about what to fix.
function getErrorMessage(error: unknown): string {
	if (isAxiosError<NodeAuthErrorResponse>(error)) {
		const data = error.response?.data;
		if (data?.errors && data.errors.length > 0) {
			return data.errors.join(" ");
		}

		if (data?.message) {
			return data.message;
		}

		if (!error.response) {
			return "Can't reach the node. Check that it's running and try again.";
		}
	}

	return "Setup failed. Check the inputs and try again.";
}

function validate(values: SetupFormValues): Partial<Record<keyof SetupFormValues, string>> {
	const errors: Partial<Record<keyof SetupFormValues, string>> = {};

	if (!/^\S+@\S+\.\S+$/.test(values.email.trim())) {
		errors.email = "Enter a valid email address.";
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
			errors.password = `Password needs ${unmet.join(", ")}.`;
		}
	} else {
		errors.password = "Password is required.";
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
							<Stack gap={4}>
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
								<List size="xs" c="dimmed" spacing={0} withPadding={true}>
									{PASSWORD_RULES.map((rule) => (
										<List.Item key={rule}>{rule}</List.Item>
									))}
								</List>
							</Stack>
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
