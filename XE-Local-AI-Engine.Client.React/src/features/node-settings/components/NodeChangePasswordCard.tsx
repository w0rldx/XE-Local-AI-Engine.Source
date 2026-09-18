import { Button, Card, Group, List, PasswordInput, Stack, Text, Title } from "@mantine/core";
import { IconLock } from "@tabler/icons-react";
import { useMutation } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { isAxiosError } from "axios";
import { t } from "i18next";
import type { FormEvent } from "react";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { changeNodePassword } from "@/core/auth/api/NodeAuthApi";
import type { NodeAuthErrorResponse, NodeChangePasswordRequest } from "@/core/auth/models/NodeAuthModels";
import { unmetPasswordRules } from "@/core/auth/models/PasswordPolicy";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { toast } from "@/core/ui/notifications/Toast";

interface ChangePasswordFormValues {
	currentPassword: string;
	newPassword: string;
	confirmPassword: string;
}

const emptyForm: ChangePasswordFormValues = { currentPassword: "", newPassword: "", confirmPassword: "" };

// The node answers a rejected change with `{ message: "Password change failed.", errors: [...] }`, and only `errors`
// distinguishes a wrong current password ("Incorrect password.") from a new password Identity refused. The generic
// `message` would be echoed by apiErrorMessage and tell the operator nothing, so the array wins when it is present;
// apiErrorMessage still handles every other shape (an unreachable node, a non-auth failure).
//
// 429 is handled first and by hand. The endpoint shares the auth throttle, and this card's client is the dedicated
// `authClient`, which carries none of the shared instance's interceptors — so `addRateLimitingInterceptor`'s toast
// never fires here. Its i18n key is reused rather than echoing the node's own English sentence through
// apiErrorMessage, which is exactly the 401/429 raw-axios shape that helper documents as the caller's job.
function changePasswordErrorMessage(error: unknown, fallback: string): string {
	if (isAxiosError<NodeAuthErrorResponse>(error)) {
		if (error.response?.status === 429) {
			return t("errorMessages.tooManyRequests", "Too many requests. Please try again later.");
		}

		const errors = error.response?.data?.errors;
		if (errors !== undefined && errors.length > 0) {
			return errors.join(" ");
		}
	}

	return apiErrorMessage(error, fallback);
}

function validate(
	values: ChangePasswordFormValues,
	t: (key: string, fallback: string) => string,
): Partial<Record<keyof ChangePasswordFormValues, string>> {
	const errors: Partial<Record<keyof ChangePasswordFormValues, string>> = {};

	if (values.currentPassword.length === 0) {
		errors.currentPassword = t("pages.nodeSettings.changePassword.validationCurrentRequired", "Enter your current password.");
	}

	if (values.newPassword.length === 0) {
		errors.newPassword = t("pages.nodeSettings.changePassword.validationNewRequired", "Enter a new password.");
	} else if (unmetPasswordRules(values.newPassword).length > 0) {
		errors.newPassword = t(
			"pages.nodeSettings.changePassword.validationNewWeak",
			"The new password does not meet all the requirements listed below.",
		);
	} else if (values.newPassword === values.currentPassword) {
		errors.newPassword = t(
			"pages.nodeSettings.changePassword.validationNewSameAsCurrent",
			"The new password must differ from the current one.",
		);
	}

	if (values.confirmPassword !== values.newPassword) {
		errors.confirmPassword = t("pages.nodeSettings.changePassword.validationNoMatch", "The new passwords do not match.");
	}

	return errors;
}

// Changing the node password ends EVERY session, this one included: the server rotates the security stamp (killing the
// access token already in memory), revokes every refresh token and clears the caller's refresh cookie, then answers 204
// with no replacement pair. There is no session left to keep, so a success is handled as a deliberate sign-out —
// clear the auth store exactly as useNodeLogout does and route to /login. The card says so in its description up front
// rather than letting the redirect come as a surprise.
export function NodeChangePasswordCard() {
	const { t } = useTranslation();
	const navigate = useNavigate();
	const clearAuth = useNodeAuthStore((state) => state.actions.clear);
	const [values, setValues] = useState<ChangePasswordFormValues>(emptyForm);
	// Errors are computed every render; this gates which of them are SHOWN, so an untouched field is never red before
	// the operator has typed anything. `hasErrors` reads the ungated result, so submit stays disabled regardless.
	const [touched, setTouched] = useState<Partial<Record<keyof ChangePasswordFormValues, boolean>>>({});
	const errors = useMemo(() => validate(values, t), [values, t]);
	const hasErrors = Object.keys(errors).length > 0;
	const visibleError = (field: keyof ChangePasswordFormValues): string | undefined =>
		touched[field] === true ? errors[field] : undefined;
	const markTouched = (field: keyof ChangePasswordFormValues): void => {
		setTouched((current) => (current[field] === true ? current : { ...current, [field]: true }));
	};

	// Mirrors the backend ASP.NET Identity policy; the same rule copy first-run setup shows, so the operator reads one
	// set of requirements in both places.
	const passwordRules = [
		t("auth.setup.passwordRuleLength", "At least 12 characters"),
		t("auth.setup.passwordRuleUppercase", "At least one uppercase letter (A–Z)"),
		t("auth.setup.passwordRuleLowercase", "At least one lowercase letter (a–z)"),
		t("auth.setup.passwordRuleDigit", "At least one digit (0–9)"),
		t("auth.setup.passwordRuleSymbol", "At least one symbol (e.g. !@#$%)"),
	];

	const changePassword = useMutation({
		// Wrapped rather than passed directly: TanStack Query hands the mutationFn a second argument (its own context
		// object), which would land in `changeNodePassword`'s AxiosRequestConfig slot and be spread into the request.
		mutationFn: (request: NodeChangePasswordRequest) => changeNodePassword(request),
		onSuccess: async () => {
			setValues(emptyForm);
			setTouched({});
			toast.success(t("pages.nodeSettings.changePassword.success", "Password changed. Sign in again with your new password."));
			clearAuth();
			await navigate({ to: "/login" });
		},
		onError: (error: unknown) => {
			toast.error(
				changePasswordErrorMessage(error, t("pages.nodeSettings.changePassword.error", "Could not change the password.")),
			);
		},
	});

	const handleSubmit = (event: FormEvent<HTMLFormElement>): void => {
		event.preventDefault();
		if (hasErrors || changePassword.isPending) {
			return;
		}

		changePassword.mutate({ currentPassword: values.currentPassword, newPassword: values.newPassword });
	};

	return (
		<Card withBorder={true} radius="md" p="lg" data-testid="node-change-password-card">
			<Stack gap="md">
				<Group gap="xs" align="center">
					<IconLock size={20} />
					<Title order={2} size="h4">
						{t("pages.nodeSettings.changePassword.title", "Change password")}
					</Title>
				</Group>

				<Text size="sm" c="dimmed" data-testid="node-change-password-description">
					{t(
						"pages.nodeSettings.changePassword.description",
						"Changing the password signs you out of this node everywhere, including this browser. Every session ends the moment the change succeeds, so you will be sent to the sign-in page to enter the new password.",
					)}
				</Text>

				<form onSubmit={handleSubmit}>
					<Stack gap="md">
						<PasswordInput
							label={t("pages.nodeSettings.changePassword.currentPasswordLabel", "Current password")}
							autoComplete="current-password"
							required={true}
							value={values.currentPassword}
							onChange={(event) => {
								const value = event.currentTarget.value;
								setValues((current) => ({ ...current, currentPassword: value }));
								markTouched("currentPassword");
							}}
							onBlur={() => markTouched("currentPassword")}
							error={visibleError("currentPassword")}
							data-testid="node-change-password-current"
						/>
						<Stack gap={4}>
							<PasswordInput
								label={t("pages.nodeSettings.changePassword.newPasswordLabel", "New password")}
								autoComplete="new-password"
								required={true}
								value={values.newPassword}
								onChange={(event) => {
									const value = event.currentTarget.value;
									setValues((current) => ({ ...current, newPassword: value }));
									markTouched("newPassword");
								}}
								onBlur={() => markTouched("newPassword")}
								error={visibleError("newPassword")}
								data-testid="node-change-password-new"
							/>
							<List size="xs" c="dimmed" spacing={0} withPadding={true}>
								{passwordRules.map((rule) => (
									<List.Item key={rule}>{rule}</List.Item>
								))}
							</List>
						</Stack>
						<PasswordInput
							label={t("pages.nodeSettings.changePassword.confirmPasswordLabel", "Confirm new password")}
							autoComplete="new-password"
							required={true}
							value={values.confirmPassword}
							onChange={(event) => {
								const value = event.currentTarget.value;
								setValues((current) => ({ ...current, confirmPassword: value }));
								markTouched("confirmPassword");
							}}
							onBlur={() => markTouched("confirmPassword")}
							error={visibleError("confirmPassword")}
							data-testid="node-change-password-confirm"
						/>
						<Group>
							<Button
								type="submit"
								loading={changePassword.isPending}
								disabled={hasErrors || changePassword.isPending}
								data-testid="node-change-password-submit"
							>
								{t("pages.nodeSettings.changePassword.submit", "Change password")}
							</Button>
						</Group>
					</Stack>
				</form>
			</Stack>
		</Card>
	);
}
