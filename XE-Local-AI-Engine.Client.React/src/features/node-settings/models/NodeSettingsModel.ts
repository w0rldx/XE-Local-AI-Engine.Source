export type NodeSettingsTimeoutInput = number | string;

export const nodeSettingsDefaults: {
	maxMessageRequestTimeoutSeconds: number;
	minMessageRequestTimeoutSeconds: number;
	maxAllowedMessageRequestTimeoutSeconds: number;
} = {
	maxMessageRequestTimeoutSeconds: 600,
	minMessageRequestTimeoutSeconds: 5,
	maxAllowedMessageRequestTimeoutSeconds: 3600,
};

export function toValidNodeSettingsTimeoutSeconds(
	value: NodeSettingsTimeoutInput,
	min = nodeSettingsDefaults.minMessageRequestTimeoutSeconds,
	max = nodeSettingsDefaults.maxAllowedMessageRequestTimeoutSeconds,
): number | undefined {
	const numericValue = typeof value === "number" ? value : Number(value);

	if (!Number.isInteger(numericValue) || numericValue < min || numericValue > max) {
		return undefined;
	}

	return numericValue;
}
