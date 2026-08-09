/**
 * UI copy lives in two flat dictionaries with identical key sets: en.ts defines
 * the shape (`Strings = typeof en`), pl.ts must match it or the build fails.
 *
 * Components read the active dictionary with `useStrings()`; modules that
 * cannot use hooks (api/client.ts) call `getStrings()` instead.
 */
export { en } from './en'
export type { Strings } from './en'
export { pl } from './pl'
export {
  DEFAULT_LANG,
  LANGUAGES,
  LANG_STORAGE_KEY,
  LanguageContext,
  dictionaries,
  format,
  getStrings,
  useLanguage,
  useStrings,
} from './context'
export type { Lang, LanguageContextValue } from './context'
export { LanguageProvider } from './LanguageProvider'
