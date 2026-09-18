// The node's ASP.NET Identity password policy (ConfigureServices.cs sets RequiredLength = 12 and keeps Identity's
// defaults for uppercase/lowercase/digit/non-alphanumeric), mirrored client-side so the operator learns what is wrong
// without a server round-trip. Kept in one place because two screens set a password — first-run setup and the
// node-settings change-password card — and a second copy of these five regexes would silently drift from the backend.
//
// The returned fragments are the untranslated pieces `auth.setup.validationPasswordWeak` interpolates. A caller that
// needs translated copy renders the `auth.setup.passwordRule*` keys instead and uses this only as a pass/fail check.
const passwordRules: readonly { readonly unmet: string; readonly isSatisfied: (password: string) => boolean }[] = [
	{ unmet: "12+ characters", isSatisfied: (password) => password.length >= 12 },
	{ unmet: "an uppercase letter", isSatisfied: (password) => /[A-Z]/.test(password) },
	{ unmet: "a lowercase letter", isSatisfied: (password) => /[a-z]/.test(password) },
	{ unmet: "a digit", isSatisfied: (password) => /[0-9]/.test(password) },
	{ unmet: "a symbol", isSatisfied: (password) => /[^a-zA-Z0-9]/.test(password) },
];

/** The policy rules the password fails, in rule order. Empty means the password is policy-compliant. */
export function unmetPasswordRules(password: string): string[] {
	return passwordRules.filter((rule) => !rule.isSatisfied(password)).map((rule) => rule.unmet);
}
