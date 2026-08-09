/**
 * Language runtime: the React context plus a module-level mirror of the active
 * language for non-React callers (api/client.ts throws translated messages).
 *
 * Kept separate from LanguageProvider.tsx so this file stays JSX-free and the
 * provider file only exports a component.
 */
import { createContext, useContext } from 'react'
import { en } from './en'
import type { Strings } from './en'
import { pl } from './pl'

export type Lang = 'en' | 'pl'

export const LANGUAGES: readonly Lang[] = ['en', 'pl']

export const dictionaries: Record<Lang, Strings> = { en, pl }

/** localStorage key holding the user's explicit choice. */
export const LANG_STORAGE_KEY = 'storymode.lang'

/**
 * Default is English, deliberately: navigator.language is ignored so the app
 * never surprises anyone with a language they did not pick.
 */
export const DEFAULT_LANG: Lang = 'en'

function isLang(value: unknown): value is Lang {
  return value === 'en' || value === 'pl'
}

/** The persisted choice, or the default when nothing valid is stored. */
export function readStoredLang(): Lang {
  try {
    const stored = window.localStorage.getItem(LANG_STORAGE_KEY)
    return isLang(stored) ? stored : DEFAULT_LANG
  } catch {
    // Private mode / storage disabled — fall back to the default.
    return DEFAULT_LANG
  }
}

export function writeStoredLang(lang: Lang): void {
  try {
    window.localStorage.setItem(LANG_STORAGE_KEY, lang)
  } catch {
    // Best effort only: the choice still applies for this session.
  }
}

// --- Module-level mirror for code that cannot use hooks ------------------

let currentLang: Lang = readStoredLang()

/** Called by LanguageProvider whenever the active language changes. */
export function setCurrentLang(lang: Lang): void {
  currentLang = lang
}

/**
 * Active dictionary for non-React modules. Resolve it at the moment the string
 * is needed (inside the throw/handler), never at module load.
 */
export function getStrings(): Strings {
  return dictionaries[currentLang]
}

// --- React context -------------------------------------------------------

export interface LanguageContextValue {
  lang: Lang
  setLang: (lang: Lang) => void
  t: Strings
}

export const LanguageContext = createContext<LanguageContextValue>({
  lang: DEFAULT_LANG,
  setLang: () => undefined,
  t: dictionaries[DEFAULT_LANG],
})

/** The active dictionary. */
export function useStrings(): Strings {
  return useContext(LanguageContext).t
}

/** The active language plus the setter behind the picker in Settings. */
export function useLanguage(): { lang: Lang; setLang: (lang: Lang) => void } {
  const { lang, setLang } = useContext(LanguageContext)
  return { lang, setLang }
}

/** Fills {name} placeholders in a dictionary value. */
export function format(template: string, values: Record<string, string | number>): string {
  return template.replace(/\{(\w+)\}/g, (match, key: string) =>
    key in values ? String(values[key]) : match,
  )
}
