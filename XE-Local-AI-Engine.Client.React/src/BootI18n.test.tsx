// @vitest-environment jsdom
//
// The `en` bundle is a lazy chunk. A failed fetch used to resolve `i18nReady` with no bundle, so Main.tsx painted every
// string as its raw key. Each test re-runs src/i18n.ts in a fresh module graph with an `en` chunk that fails, and boots
// through `renderWhenI18nReady` as Main.tsx does.

import { fireEvent, screen } from "@testing-library/react";
import i18next from "i18next";
import { act } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, describe, expect, it, vi } from "vitest";

import { renderWhenI18nReady } from "@/BootI18n";
import en from "@/locales/en.json";

// The key PreloadErrorReload stamps after an automatic reload; while it is fresh, the automatic path will not reload.
const AUTOMATIC_RELOAD_STAMP = "xe-preload-error-reload-at";

function Translated() {
	return <p>{i18next.t("basicFormatOption")}</p>;
}

async function bootWithFailingEn() {
	vi.doMock("@/locales/en.json", () => {
		throw new Error("chunk failed");
	});
	i18next.removeResourceBundle("en", "translation");
	vi.resetModules();
	return import("@/i18n");
}

let root: Root | undefined;
let container: HTMLDivElement | undefined;

afterEach(async () => {
	await act(async () => root?.unmount());
	container?.remove();
	sessionStorage.clear();
	vi.unstubAllGlobals();
	vi.doUnmock("@/locales/en.json");
	vi.resetModules();
	i18next.addResourceBundle("en", "translation", en, true, true);
	await i18next.changeLanguage("en");
});

async function boot(ready: Promise<void>) {
	container = document.createElement("div");
	document.body.append(container);
	const mounted = createRoot(container);
	root = mounted;
	await act(() => renderWhenI18nReady(mounted, ready, <Translated />));
}

describe("boot with a failing en chunk", () => {
	it("rejects i18nReady and shows the untranslated retry screen instead of raw keys", async () => {
		const { i18nReady } = await bootWithFailingEn();

		await expect(i18nReady).rejects.toThrow("English translation bundle");
		await boot(i18nReady);

		expect(screen.getByRole("alert").textContent).toContain("The interface could not load");
		expect(screen.queryByText("basicFormatOption")).toBeNull();
	});

	// Most browsers cache a failed module import, so a second import() would fail without a request; Retry reloads.
	it("reloads the document on Retry even when the automatic reload guard is fresh", async () => {
		const reload = vi.fn();
		vi.stubGlobal("location", { ...window.location, reload });
		sessionStorage.setItem(AUTOMATIC_RELOAD_STAMP, String(Date.now()));
		const { i18nReady } = await bootWithFailingEn();
		await boot(i18nReady);

		fireEvent.click(screen.getByRole("button", { name: "Retry" }));

		expect(reload).toHaveBeenCalledTimes(1);
		expect(sessionStorage.getItem(AUTOMATIC_RELOAD_STAMP)).toBeNull();
	});
});
