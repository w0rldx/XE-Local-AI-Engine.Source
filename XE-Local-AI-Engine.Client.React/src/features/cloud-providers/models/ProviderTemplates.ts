// The two ways an `ext:` connection starts. They open the same empty form and differ only in the hints the editor
// shows; both save through the same connection store.
export type ExternalProviderTemplateId = "generic" | "azureOpenAi";

export type ProviderTemplateId = ExternalProviderTemplateId | "azureFoundry" | "codex";

// Why a template is offered but cannot be picked. Azure Foundry and Codex each hold one slot on the node.
export type ProviderTemplateBlock = "azureFoundryConfigured" | "codexSignedIn";

export interface ProviderTemplateOption {
	readonly id: ProviderTemplateId;
	readonly blockedBy: ProviderTemplateBlock | null;
}

export interface ProviderTemplateCapabilities {
	readonly cloudSettings: boolean;
	readonly externalProviders: boolean;
}

export interface ProviderSlots {
	readonly azureFoundryConfigured: boolean;
	readonly codexSignedIn: boolean;
}

/** The templates the "Add provider" picker offers: only those whose capability is on, in display order. */
export function providerTemplateOptions(
	capabilities: ProviderTemplateCapabilities,
	slots: ProviderSlots,
): ProviderTemplateOption[] {
	const options: ProviderTemplateOption[] = [];
	if (capabilities.externalProviders) {
		options.push({ id: "generic", blockedBy: null }, { id: "azureOpenAi", blockedBy: null });
	}
	if (capabilities.cloudSettings) {
		options.push(
			{ id: "azureFoundry", blockedBy: slots.azureFoundryConfigured ? "azureFoundryConfigured" : null },
			{ id: "codex", blockedBy: slots.codexSignedIn ? "codexSignedIn" : null },
		);
	}
	return options;
}

export function isExternalProviderTemplate(id: ProviderTemplateId): id is ExternalProviderTemplateId {
	return id === "generic" || id === "azureOpenAi";
}

// Placeholders only: the operator's host, path and resource name are never guessed into the form.
export const externalTemplateBaseUrlPlaceholder: Partial<Record<ExternalProviderTemplateId, string>> = {
	azureOpenAi: "https://<resource>.openai.azure.com/openai/v1",
};
