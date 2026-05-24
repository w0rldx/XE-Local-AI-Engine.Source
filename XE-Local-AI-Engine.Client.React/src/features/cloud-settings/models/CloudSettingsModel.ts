export interface CloudSettingsFormValues {
	endpoint: string;
	apiKey: string;
	deploymentName: string;
}

export function isHttpsAbsoluteUrl(value: string): boolean {
	try {
		return new URL(value).protocol === "https:";
	} catch {
		return false;
	}
}

export function validateCloudSettingsForm(values: CloudSettingsFormValues): Partial<Record<keyof CloudSettingsFormValues, string>> {
	const errors: Partial<Record<keyof CloudSettingsFormValues, string>> = {};

	if (!isHttpsAbsoluteUrl(values.endpoint.trim())) {
		errors.endpoint = "Enter an absolute HTTPS Azure OpenAI endpoint.";
	}

	if (values.apiKey.trim().length === 0) {
		errors.apiKey = "Enter the API key. Saved keys are never returned to this page.";
	}

	if (values.deploymentName.trim().length === 0) {
		errors.deploymentName = "Enter the Azure OpenAI deployment name.";
	}

	return errors;
}
