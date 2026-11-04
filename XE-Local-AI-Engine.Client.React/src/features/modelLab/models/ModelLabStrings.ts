import i18next from "i18next";

// Model Lab's strings ship with its route, not with every session's locale chunk. Named so the bundle budget counts them as the route's.
const bundles = {
	de: () => import("@/features/modelLab/locales/model-lab-de.json"),
	en: () => import("@/features/modelLab/locales/model-lab-en.json"),
};

const loads = new Map<string, Promise<void>>();
const failed = new Set<string>();

// A stable promise per language, so a suspending page can ask on every render. It never rejects, and a failed one stays
// settled for the visit: the page then reads the calls' English defaults, instead of suspending on a new import each time.
export function loadModelLabStrings(language: string | undefined): Promise<void> {
	const code = language?.split("-")[0] === "de" ? "de" : "en";
	const pending =
		loads.get(code) ??
		bundles[code]().then(
			(bundle) => {
				i18next.addResourceBundle(code, "translation", bundle.default, true, true);
			},
			() => {
				failed.add(code);
			},
		);
	loads.set(code, pending);
	return pending;
}

// A new visit may try again.
export function forgetFailedModelLabStrings(): void {
	for (const code of failed) {
		loads.delete(code);
	}
	failed.clear();
}
