// Vitest setup: every locale is a lazy chunk (see src/i18n.ts), so wait for the `en` bundle before any test file runs.
// Without it a synchronous `t()` in the first test of a file would read the raw key instead of the shipped string.
import { i18nReady } from "@/i18n";

await i18nReady;
