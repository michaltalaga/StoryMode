import { useCallback, useEffect, useMemo, useState } from 'react'
import type { ReactNode } from 'react'
import type { Lang } from './context'
import {
  LanguageContext,
  dictionaries,
  readStoredLang,
  setCurrentLang,
  writeStoredLang,
} from './context'

/**
 * Holds the active language for the whole app. Mount above the router so every
 * route re-renders when the language changes — switching never needs a reload.
 */
export function LanguageProvider({ children }: { children: ReactNode }) {
  const [lang, setLangState] = useState<Lang>(readStoredLang)

  const setLang = useCallback((next: Lang) => {
    setLangState(next)
    writeStoredLang(next)
  }, [])

  useEffect(() => {
    // Keep <html lang>, the tab title and the non-React accessor in step with
    // the context (index.html ships the neutral "Story Mode" until React boots).
    setCurrentLang(lang)
    document.documentElement.lang = lang
    document.title = dictionaries[lang].appTitle
  }, [lang])

  const value = useMemo(() => ({ lang, setLang, t: dictionaries[lang] }), [lang, setLang])

  return <LanguageContext.Provider value={value}>{children}</LanguageContext.Provider>
}
