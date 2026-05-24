export const localOperatorHeaderName = "X-Local-Operator";

const missingTokenSentinel = "%XE_LOCAL_OPERATOR_TOKEN%";
const localOperatorTokenGlobalKey = "__XE_LOCAL_OPERATOR_TOKEN__";

export function getLocalOperatorToken(): string | undefined {
	const token = (globalThis as unknown as Partial<Record<typeof localOperatorTokenGlobalKey, string>>)[localOperatorTokenGlobalKey]?.trim();

	if (!token || token === missingTokenSentinel) {
		return undefined;
	}

	return token;
}
