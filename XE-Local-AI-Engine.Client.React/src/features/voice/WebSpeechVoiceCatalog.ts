// Catalog of every OS/browser speech voice, grouped by language, so the voice picker can offer ANY installed
// voice — not just the manifest's Kokoro (English) + logical web-speech entries. `WebSpeechProvider.pickVoice`
// already resolves a `voiceId` by `voiceURI`/`name` and falls back to a language-prefix match, so exposing the
// full OS catalog here is enough to unlock every language the OS already supports — no backend change needed.
//
// Chrome (and some other browsers) populate `speechSynthesis.getVoices()` asynchronously: the first call right
// after page load returns `[]`, and the real list arrives via the `voiceschanged` event. `useWebSpeechVoices`
// re-reads the catalog whenever that event fires so the picker fills in once voices are ready.

import { useEffect, useMemo, useState } from "react";

/** Minimal speech-engine surface needed to enumerate voices and observe population; the real `speechSynthesis` and
 * test fakes both satisfy it. */
export interface VoiceEnumerationSource {
	getVoices(): SpeechSynthesisVoice[];
	addEventListener(type: "voiceschanged", listener: () => void): void;
	removeEventListener(type: "voiceschanged", listener: () => void): void;
}

/** One selectable OS voice. `language` is the normalized short IETF code (e.g. "de" from "de-DE"). */
export interface OsVoiceEntry {
	/** Stable identity for `voiceId` — the voice's `voiceURI`, falling back to a name+lang composite when a browser
	 * reports an empty `voiceURI`. Matches by this or by `name` in `WebSpeechProvider.pickVoice`. */
	readonly id: string;
	readonly name: string;
	readonly language: string;
	readonly localService: boolean;
}

/** OS voices grouped by normalized language code; each group is sorted on-device (`localService`) first. */
export type OsVoiceCatalog = ReadonlyMap<string, readonly OsVoiceEntry[]>;

function resolveSource(injected?: VoiceEnumerationSource): VoiceEnumerationSource | undefined {
	return injected ?? (globalThis.speechSynthesis as VoiceEnumerationSource | undefined);
}

/** Normalizes an IETF tag ("de-DE", "pt_BR") to its short primary subtag ("de", "pt"), lowercased. */
function normalizeLanguage(lang: string): string {
	return lang.split(/[-_]/)[0]?.toLowerCase() ?? "";
}

function toEntryId(voice: SpeechSynthesisVoice): string {
	return voice.voiceURI.length > 0 ? voice.voiceURI : `${voice.name}::${voice.lang}`;
}

function toEntry(voice: SpeechSynthesisVoice): OsVoiceEntry {
	return {
		id: toEntryId(voice),
		name: voice.name,
		language: normalizeLanguage(voice.lang),
		localService: voice.localService,
	};
}

/** Flat, deduped (by id) list of every OS voice the engine currently reports. Empty when unsupported / not yet
 * populated. */
export function listOsVoices(source?: VoiceEnumerationSource): readonly OsVoiceEntry[] {
	const engine = resolveSource(source);
	if (!engine) {
		return [];
	}

	const seen = new Set<string>();
	const entries: OsVoiceEntry[] = [];
	for (const voice of engine.getVoices()) {
		const entry = toEntry(voice);
		if (!entry.language || seen.has(entry.id)) {
			continue;
		}

		seen.add(entry.id);
		entries.push(entry);
	}

	return entries;
}

/** Groups a flat voice list by language, preferring on-device (`localService`) voices first within each group so an
 * offline-capable pick is the natural default. */
export function groupOsVoicesByLanguage(entries: readonly OsVoiceEntry[]): OsVoiceCatalog {
	const grouped = new Map<string, OsVoiceEntry[]>();
	for (const entry of entries) {
		const bucket = grouped.get(entry.language);
		if (bucket) {
			bucket.push(entry);
		} else {
			grouped.set(entry.language, [entry]);
		}
	}

	for (const bucket of grouped.values()) {
		bucket.sort((a, b) => Number(b.localService) - Number(a.localService) || a.name.localeCompare(b.name));
	}

	return grouped;
}

/** Resolves the normalized language of a selected OS voice by its `voiceId` (`voiceURI`/name+lang composite), or
 * `undefined` when it does not match any currently-reported OS voice (e.g. a manifest voice id). */
export function resolveOsVoiceLanguage(voiceId: string, source?: VoiceEnumerationSource): string | undefined {
	return listOsVoices(source).find((entry) => entry.id === voiceId)?.language;
}

/**
 * Live-updating OS voice catalog grouped by language. Reads once on mount, then re-reads on every `voiceschanged`
 * event to pick up Chrome's async population (and any user-installed voice packs added later in the session).
 * `source` is injectable for tests; production callers omit it and get `globalThis.speechSynthesis`.
 */
export function useWebSpeechVoices(source?: VoiceEnumerationSource): OsVoiceCatalog {
	const [entries, setEntries] = useState<readonly OsVoiceEntry[]>(() => listOsVoices(source));

	useEffect(() => {
		const engine = resolveSource(source);
		if (!engine) {
			return;
		}

		const refresh = (): void => setEntries(listOsVoices(engine));
		refresh();
		engine.addEventListener("voiceschanged", refresh);
		return () => engine.removeEventListener("voiceschanged", refresh);
	}, [source]);

	return useMemo(() => groupOsVoicesByLanguage(entries), [entries]);
}
