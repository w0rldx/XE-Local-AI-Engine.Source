// Vitest setup: pin the ambient ICU locale so locale-formatted assertions are deterministic.
//
// Several display helpers deliberately format for the *viewer's* locale — `(8192).toLocaleString()`,
// `new Intl.NumberFormat(undefined, { notation: "compact" })`, `date.toLocaleDateString(undefined, …)`.
// That lets the machine's regional format leak into the expected strings, so a suite whose expectations
// are written in en-US hard-fails on a box that is not en-US, with no product defect behind it.
// Observed on a Windows packaging machine resolving to en-DE (English display language, German number
// format): `(8192).toLocaleString()` is "8.192", compact 1.5e6 is "1,5M" and a short date is
// "12 Mar 2025" — 8 failures across model-fit, model-management and usage-dashboard.
//
// An environment variable cannot fix this. Node reads LC_ALL/LANG for the ICU default on POSIX only; on
// Windows it takes the OS regional setting and ignores the environment — so the env-var route fails on
// exactly the box that needs it, the Windows packaging host. Redirecting the default at the API boundary
// behaves identically on every platform.
//
// Only an ABSENT locale argument is redirected. A call that names its locale explicitly is passed through
// untouched, so a test can still assert genuinely locale-specific behaviour.

const TEST_LOCALE = "en-US";

type IntlFormatConstructor = typeof Intl.NumberFormat | typeof Intl.DateTimeFormat;

function withDefaultLocale(args: unknown[]): unknown[] {
	return args.length === 0 || args[0] === undefined ? [TEST_LOCALE, ...args.slice(1)] : args;
}

// A Proxy keeps the original's statics (`supportedLocalesOf`), its prototype, and the
// callable-without-`new` duality intact — a hand-rolled subclass would drop at least one of them.
function pinConstructor<T extends IntlFormatConstructor>(original: T): T {
	return new Proxy(original, {
		apply: (target, thisArgument, args: unknown[]) =>
			Reflect.apply(target as CallableFunction, thisArgument, withDefaultLocale(args)),
		construct: (target, args: unknown[], newTarget) =>
			Reflect.construct(target as CallableFunction, withDefaultLocale(args), newTarget),
	});
}

Intl.NumberFormat = pinConstructor(Intl.NumberFormat);
Intl.DateTimeFormat = pinConstructor(Intl.DateTimeFormat);

// The `toLocale*` prototype methods do not route through the Intl constructors, so they need the same
// treatment. `??` deliberately leaves an explicit `null` alone — only a missing argument is defaulted.
const originalNumberToLocaleString = Number.prototype.toLocaleString;
Number.prototype.toLocaleString = function pinnedNumberToLocaleString(
	locales?: Intl.LocalesArgument,
	options?: Intl.NumberFormatOptions,
): string {
	return originalNumberToLocaleString.call(this, locales ?? TEST_LOCALE, options);
};

const originalDateToLocaleString = Date.prototype.toLocaleString;
Date.prototype.toLocaleString = function pinnedDateToLocaleString(
	locales?: Intl.LocalesArgument,
	options?: Intl.DateTimeFormatOptions,
): string {
	return originalDateToLocaleString.call(this, locales ?? TEST_LOCALE, options);
};

const originalDateToLocaleDateString = Date.prototype.toLocaleDateString;
Date.prototype.toLocaleDateString = function pinnedDateToLocaleDateString(
	locales?: Intl.LocalesArgument,
	options?: Intl.DateTimeFormatOptions,
): string {
	return originalDateToLocaleDateString.call(this, locales ?? TEST_LOCALE, options);
};

const originalDateToLocaleTimeString = Date.prototype.toLocaleTimeString;
Date.prototype.toLocaleTimeString = function pinnedDateToLocaleTimeString(
	locales?: Intl.LocalesArgument,
	options?: Intl.DateTimeFormatOptions,
): string {
	return originalDateToLocaleTimeString.call(this, locales ?? TEST_LOCALE, options);
};
