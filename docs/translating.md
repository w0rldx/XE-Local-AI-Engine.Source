# Translating the app to a new language

The React management UI is fully internationalized with
[`i18next`](https://www.i18next.com/) + `react-i18next`. Every user-facing string is a
translation key resolved at render time, so adding a language is **data + three small
wiring edits** — no component changes.

Today the app ships `en` (English, the source/fallback) and `de` (German). This guide walks
through adding a third; the same steps apply to any locale.

All paths below are relative to `XE-Local-AI-Engine.Client.React/`.

## How i18n is wired

- **`src/locales/en.json`** — the **source of truth**. It is statically bundled and is the
  `fallbackLng`, so any key missing from another locale renders the English text instead of
  breaking. ~3110 leaf strings across 13 top-level sections (`common`, `chat`, `pages`,
  `components`, `voice`, …).
- **`src/locales/<code>.json`** — every other locale. These are **lazy-loaded**: each is code-split
  into its own chunk and fetched on demand the first time that language is selected, so users
  never download a language they won't render. This is why non-English locales must be
  registered in `src/i18n.ts` (step 2) — that map is how the loader finds the chunk.
- **`src/data/language/LanguageMenuData.ts`** — the list that populates the in-app language
  picker. A locale that isn't here is unreachable from the UI even if its JSON exists.
- The active language persists in `localStorage` under `i18nextLng`; the browser language
  detector picks the initial one, falling back to `en`.

## Steps to add a language

Example: adding Spanish (`es`).

### 1. Create the locale file

Copy `src/locales/en.json` to `src/locales/es.json` and translate **every value**, leaving
**every key unchanged**. Structure must stay byte-for-byte identical in shape — same keys, same
nesting, same order is nice-to-have but not required (parity is checked by key path, not order).

Rules that will bite you if ignored:

- **Never translate a key**, only its value. `"send": "Send"` → `"send": "Enviar"`.
- **Preserve interpolation placeholders verbatim.** `"pulling": "Pulling {{model}}"` →
  `"pulling": "Descargando {{model}}"`. The `{{model}}` token must survive untouched or the
  runtime value won't substitute.
- **Don't add HTML or escape characters.** React escapes text at render time and i18next
  escaping is deliberately off (`escapeValue: false`), so a literal `&` or `/` is fine and
  should stay literal — do not write `&amp;` or `&#x2F;`.
- Keep plural/format tokens (`{{count}}`, ICU-style braces) exactly as they appear in `en`.

### 2. Register the lazy chunk — `src/i18n.ts`

Add the locale to the `lazyLocales` map so the loader can fetch its chunk:

```ts
const lazyLocales: Record<string, () => Promise<{ default: unknown }>> = {
	de: () => import("./locales/de.json"),
	es: () => import("./locales/es.json"), // ← add
};
```

### 3. Add it to the language picker — `src/data/language/LanguageMenuData.ts`

```ts
export const languageData: ILanguageItem[] = [
	{ id: 1, icon: "🇬🇧", text: "English", value: "en" },
	{ id: 2, icon: "🇩🇪", text: "Deutsch", value: "de" },
	{ id: 3, icon: "🇪🇸", text: "Español", value: "es" }, // ← add
];
```

- `value` is the locale code and **must match** the `lazyLocales` key and the JSON filename.
- `text` is the language's **endonym** — its name in that language itself (`Español`, not
  `Spanish`).
- `icon` is an optional flag emoji.

That's the whole wiring. The picker, persistence, lazy fetch, and fallback are all automatic.

## Keeping locales in sync (key parity)

Structural parity — every locale having the **same set of key paths** as `en` — is enforced by
Vitest so a missing or orphaned key fails the suite instead of silently rendering the English
fallback:

- `src/I18n.test.ts` — full key-path parity against `en`.
- `src/features/app-update/I18nParity.test.ts` and
  `src/features/diagnostics/components/DiagnosticsI18n.test.ts` — section-scoped parity.

**These cover your new locale automatically — no test edit needed.** All three discover every
`src/locales/*.json` file at run time (via `src/test/Locales.ts`) and check each one against
`en`, so the moment you add `es.json` it is held to full parity, and any later key added to `en`
is forced into `es` too.

The fastest way to find gaps while translating: any key you leave out of `es.json` still
renders — as English — so a visual pass won't catch omissions. `pnpm run test` will name every
missing key.

## Validate

From `XE-Local-AI-Engine.Client.React/`:

```bash
pnpm run lint            # typecheck + lint (this is the real typecheck gate)
pnpm run test            # runs the i18n parity tests
pnpm run build           # confirms the new lazy chunk splits cleanly
```

All three must be green. See [`CONTRIBUTING.md`](../CONTRIBUTING.md#validating-your-change) for
the full gate list.

## Scope note

This covers the **React UI**, which is where all user-facing text lives. Backend strings, model
names, and AI-generated content are not part of the locale files and are out of scope for a
translation contribution.
