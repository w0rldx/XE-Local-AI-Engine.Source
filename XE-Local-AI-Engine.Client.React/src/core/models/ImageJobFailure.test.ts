import i18next from "i18next";
import { beforeAll, describe, expect, it } from "vitest";

import { imageJobFailureMessage } from "@/core/models/ImageJobFailure";
import de from "@/locales/de.json";

const unloaded = "The image runtime unloaded the model while the generation was starting. Try again.";

describe("imageJobFailureMessage", () => {
	beforeAll(() => {
		i18next.addResourceBundle("de", "translation", de, true, true);
	});

	it("renders the model-unloaded reason in the operator's language", () => {
		expect(
			imageJobFailureMessage(i18next.getFixedT("de"), { failureReason: "ModelUnloadedWhileStarting", sanitizedError: unloaded }),
		).toBe("Die Bild-Laufzeit hat das Modell entladen, während die Generierung startete. Versuchen Sie es erneut.");
	});

	it("renders an older generic row with the generic text, and an unknown or missing reason with the stored text", () => {
		const german = i18next.getFixedT("de");

		expect(imageJobFailureMessage(german, { failureReason: "Generic", sanitizedError: "Image generation failed." })).toBe(
			"Die Bildgenerierung ist fehlgeschlagen.",
		);
		expect(imageJobFailureMessage(german, { failureReason: "AReasonThisBuildDoesNotKnow", sanitizedError: unloaded })).toBe(
			unloaded,
		);
		expect(imageJobFailureMessage(german, { failureReason: null, sanitizedError: unloaded })).toBe(unloaded);
	});
});
