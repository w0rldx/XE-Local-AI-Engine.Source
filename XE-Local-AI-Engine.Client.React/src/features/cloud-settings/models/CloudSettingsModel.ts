// Auth modes accepted by the Azure Foundry connection. Mirrors the backend `authMode` string
// ("ApiKey" | "ManagedIdentity"); managed identity uses DefaultAzureCredential and needs no key.
export type CloudAuthMode = "ApiKey" | "ManagedIdentity";

// One editable deployment row in the models list. `deploymentName` is the Foundry portal deployment
// name (not the model family); `displayLabel` is an optional friendly label shown in the picker.
export interface CloudFoundryModelDraft {
	deploymentName: string;
	displayLabel: string;
}

export interface CloudSettingsFormValues {
	endpoint: string;
	authMode: CloudAuthMode;
	apiKey: string;
	models: CloudFoundryModelDraft[];
}

export function isHttpsAbsoluteUrl(value: string): boolean {
	try {
		return new URL(value).protocol === "https:";
	} catch {
		return false;
	}
}

// A models list is valid once at least one row carries a non-blank deployment name.
export function hasAtLeastOneModel(models: CloudFoundryModelDraft[]): boolean {
	return models.some((model) => model.deploymentName.trim().length > 0);
}

export function validateCloudSettingsForm(
	values: CloudSettingsFormValues,
): Partial<Record<keyof CloudSettingsFormValues, string>> {
	const errors: Partial<Record<keyof CloudSettingsFormValues, string>> = {};

	if (!isHttpsAbsoluteUrl(values.endpoint.trim())) {
		errors.endpoint = "Enter an absolute HTTPS Azure OpenAI endpoint.";
	}

	// The API key is only required for API-key auth; managed identity is keyless (DefaultAzureCredential).
	if (values.authMode === "ApiKey" && values.apiKey.trim().length === 0) {
		errors.apiKey = "Enter the API key. Saved keys are never returned to this page.";
	}

	if (!hasAtLeastOneModel(values.models)) {
		errors.models = "Add at least one deployment name.";
	}

	return errors;
}
