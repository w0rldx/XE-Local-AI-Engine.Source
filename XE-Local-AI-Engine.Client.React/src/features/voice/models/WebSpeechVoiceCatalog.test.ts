// @vitest-environment jsdom

import { act, renderHook } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import {
	groupOsVoicesByLanguage,
	listOsVoices,
	resolveOsVoiceLanguage,
	useWebSpeechVoices,
	type VoiceEnumerationSource,
} from "./WebSpeechVoiceCatalog";

// Fake OS voice catalog covering: two German voices (one on-device, one network) to assert the localService-first
// sort, one English on-device voice, and one duplicate (same voiceURI as "Anna") to assert dedupe.
const baseVoices = [
	{ name: "Google Deutsch", lang: "de-DE", voiceURI: "Google Deutsch", localService: false, default: false },
	{ name: "Anna", lang: "de-DE", voiceURI: "urn:moz-tts:anna", localService: true, default: false },
	{ name: "Anna (duplicate)", lang: "de-DE", voiceURI: "urn:moz-tts:anna", localService: true, default: false },
	{ name: "Daniel", lang: "en-GB", voiceURI: "urn:moz-tts:daniel", localService: true, default: false },
] as unknown as SpeechSynthesisVoice[];

function makeSource(callResults: (readonly SpeechSynthesisVoice[])[]): {
	source: VoiceEnumerationSource;
	fireVoicesChanged: () => void;
} {
	let call = 0;
	let listener: (() => void) | undefined;
	const source: VoiceEnumerationSource = {
		getVoices: () => [...(callResults[Math.min(call, callResults.length - 1)] ?? [])],
		addEventListener: (_type, cb) => {
			listener = cb;
		},
		removeEventListener: () => {
			listener = undefined;
		},
	};

	return {
		source,
		fireVoicesChanged: () => {
			call += 1;
			listener?.();
		},
	};
}

describe("listOsVoices", () => {
	it("dedupes voices sharing the same voiceURI", () => {
		const { source } = makeSource([baseVoices]);
		const entries = listOsVoices(source);
		expect(entries.filter((entry) => entry.id === "urn:moz-tts:anna")).toHaveLength(1);
	});

	it("normalizes region-tagged languages to their short primary subtag", () => {
		const { source } = makeSource([baseVoices]);
		const entries = listOsVoices(source);
		expect(entries.find((entry) => entry.name === "Anna")?.language).toBe("de");
		expect(entries.find((entry) => entry.name === "Daniel")?.language).toBe("en");
	});

	it("returns an empty list when no voice source is available", () => {
		expect(
			listOsVoices({ getVoices: () => [], addEventListener: () => undefined, removeEventListener: () => undefined }),
		).toEqual([]);
	});
});

describe("groupOsVoicesByLanguage", () => {
	it("groups by language and prefers on-device voices first within a group", () => {
		const { source } = makeSource([baseVoices]);
		const grouped = groupOsVoicesByLanguage(listOsVoices(source));

		const deGroup = grouped.get("de");
		expect(deGroup?.map((entry) => entry.name)).toEqual(["Anna", "Google Deutsch"]);

		const enGroup = grouped.get("en");
		expect(enGroup?.map((entry) => entry.name)).toEqual(["Daniel"]);
	});
});

describe("resolveOsVoiceLanguage", () => {
	it("resolves the language of a known OS voice by id", () => {
		const { source } = makeSource([baseVoices]);
		expect(resolveOsVoiceLanguage("urn:moz-tts:anna", source)).toBe("de");
	});

	it("returns undefined for a logical manifest voice id that maps to no OS voice", () => {
		const { source } = makeSource([baseVoices]);
		expect(resolveOsVoiceLanguage("de_web_default", source)).toBeUndefined();
	});
});

describe("useWebSpeechVoices", () => {
	it("re-reads the catalog when voiceschanged fires (Chrome's async population)", () => {
		const { source, fireVoicesChanged } = makeSource([[], baseVoices]);
		const { result } = renderHook(() => useWebSpeechVoices(source));

		expect(result.current.size).toBe(0);

		act(() => {
			fireVoicesChanged();
		});

		expect(result.current.get("de")?.map((entry) => entry.name)).toEqual(["Anna", "Google Deutsch"]);
		expect(result.current.get("en")?.map((entry) => entry.name)).toEqual(["Daniel"]);
	});
});
