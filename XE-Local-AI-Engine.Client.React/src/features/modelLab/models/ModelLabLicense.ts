import type { TFunction } from "i18next";

// The facts the server builds its English confirmation from (LicenseGateService.DescribeCheckpoint and
// BuildModelLabText). The page shows them translated; the server's English text stays what the node records and hashes.
export interface LicenseFacts {
	readonly repoId: string;
	readonly license: string | null;
	readonly isGated: boolean;
	readonly metadataPresent: boolean;
}

export function licenseConfirmationLabel(t: TFunction, { repoId, license, isGated, metadataPresent }: LicenseFacts): string {
	let terms: string;
	if (!metadataPresent) {
		terms = t(
			"pages.modelLab.trace.license.noMetadata",
			"The base checkpoint '{{repoId}}' has no license metadata found for it.",
			{
				repoId,
			},
		);
	} else if (license) {
		terms = t("pages.modelLab.trace.license.declared", "The base checkpoint '{{repoId}}' declares the license '{{license}}'.", {
			repoId,
			license,
		});
	} else {
		terms = t("pages.modelLab.trace.license.undeclared", "The base checkpoint '{{repoId}}' declares no license.", { repoId });
	}
	const gated = isGated
		? [t("pages.modelLab.trace.license.gated", "The repository is gated and its terms were accepted on Hugging Face.")]
		: [];
	const confirm = t(
		"pages.modelLab.trace.license.confirm",
		"I confirm I am permitted to download these weights and to run experiments on them.",
	);
	return [terms, ...gated, confirm].join(" ");
}
