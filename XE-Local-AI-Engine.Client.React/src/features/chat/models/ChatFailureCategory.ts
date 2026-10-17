import type { i18n as I18n, TFunction } from "i18next";

// The three provider categories share one sentence: the raw server text below it names the provider and the cause.
const providerCategories = new Set(["ProviderUnreachable", "ProviderAuthFailed", "ProviderRateLimited"]);

/**
 * The localized headline of a failed turn's server `FailureCategory`, shown above the raw server text. Undefined for no
 * category or one without a key, so the bubble then shows the raw text alone. `ModelNotInstalled` keeps its own branch.
 */
export function chatFailureHeadline(t: TFunction, i18n: Pick<I18n, "exists">, category: string | undefined): string | undefined {
	if (!category) {
		return undefined;
	}
	const key = providerCategories.has(category) ? "pages.chat.error.providerError" : `pages.chat.error.categories.${category}`;
	return i18n.exists(key) ? t(key) : undefined;
}

/** The badge label of a failure category; a category this client does not know renders as the raw wire value. */
export function chatFailureCategoryLabel(t: TFunction, category: string): string {
	return t(`pages.chat.error.categoryLabels.${category}`, category);
}
