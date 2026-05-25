import { Alert, Button, Card, Container, PasswordInput, Stack, Text, Title } from "@mantine/core";
import { IconAlertTriangle, IconLock } from "@tabler/icons-react";
import { useNavigate, useSearch } from "@tanstack/react-router";
import type { FormEvent } from "react";
import { useState } from "react";

import { loginNodeAuth } from "@/core/auth/api/NodeAuthApi";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { getSafeRedirectPath } from "@/core/auth/utils/RedirectPath";

function getErrorMessage(error: unknown): string {
	if (error instanceof Error) {
		return error.message;
	}

	return "Login failed. Check the password and try again.";
}

export function Login() {
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
						<IconLock size={36} aria-hidden="true" />
						<Title order={1}>Sign in</Title>
						<Text c="dimmed">Enter the local node admin password.</Text>
					</Stack>

					{error ? (
						<Alert color="red" icon={<IconAlertTriangle size={16} />}>
							{error}
						</Alert>
					) : null}

					<form onSubmit={handleSubmit}>
						<Stack gap="md">
							<PasswordInput
								label="Password"
								autoComplete="current-password"
								required={true}
								value={password}
								onChange={(event) => {
									setPassword(event.currentTarget.value);
								}}
							/>
							<Button type="submit" loading={submitting} disabled={password.length === 0} fullWidth={true}>
								Sign in
							</Button>
						</Stack>
					</form>
				</Stack>
			</Card>
		</Container>
	);
}
