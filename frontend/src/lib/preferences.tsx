"use client";

import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
} from "react";
import {
  translations,
  type Locale,
  type TranslationKey,
} from "@/lib/translations";

export type Theme = "light" | "dark";
export type PhotoGridDensity = "comfortable" | "standard" | "compact";

type TranslationValues = Record<string, string | number>;

type PreferencesContextValue = {
  locale: Locale;
  setLocale: (locale: Locale) => void;
  theme: Theme;
  setTheme: (theme: Theme) => void;
  photoGridDensity: PhotoGridDensity;
  setPhotoGridDensity: (density: PhotoGridDensity) => void;
  showTimelineEvents: boolean;
  setShowTimelineEvents: (show: boolean) => void;
  t: (key: TranslationKey, values?: TranslationValues) => string;
};

const PreferencesContext = createContext<PreferencesContextValue | null>(null);
const localeStorageKey = "haven.locale";
const themeStorageKey = "haven.theme";
const photoGridDensityStorageKey = "haven.photoGridDensity";
const showTimelineEventsStorageKey = "haven.showTimelineEvents";

export function PreferencesProvider({ children }: { children: ReactNode }) {
  const [locale, setLocaleState] = useState<Locale>("en");
  const [theme, setThemeState] = useState<Theme>("light");
  const [photoGridDensity, setPhotoGridDensityState] =
    useState<PhotoGridDensity>("standard");
  const [showTimelineEvents, setShowTimelineEventsState] = useState(true);

  useEffect(() => {
    const initialization = window.setTimeout(() => {
      const savedLocale = window.localStorage.getItem(localeStorageKey);
      const browserLocale = window.navigator.language.toLowerCase();
      const initialLocale: Locale =
        savedLocale === "es" || savedLocale === "en"
          ? savedLocale
          : browserLocale.startsWith("es")
            ? "es"
            : "en";
      const documentTheme = document.documentElement.dataset.theme;
      const initialTheme: Theme = documentTheme === "dark" ? "dark" : "light";
      const savedPhotoGridDensity = window.localStorage.getItem(
        photoGridDensityStorageKey,
      );
      const initialPhotoGridDensity: PhotoGridDensity =
        savedPhotoGridDensity === "comfortable" ||
        savedPhotoGridDensity === "compact"
          ? savedPhotoGridDensity
          : "standard";
      setLocaleState(initialLocale);
      setThemeState(initialTheme);
      setPhotoGridDensityState(initialPhotoGridDensity);
      setShowTimelineEventsState(
        window.localStorage.getItem(showTimelineEventsStorageKey) !== "false",
      );
      document.documentElement.lang = initialLocale;
    }, 0);
    return () => window.clearTimeout(initialization);
  }, []);

  const setLocale = useCallback((nextLocale: Locale) => {
    setLocaleState(nextLocale);
    window.localStorage.setItem(localeStorageKey, nextLocale);
    document.documentElement.lang = nextLocale;
  }, []);

  const setTheme = useCallback((nextTheme: Theme) => {
    setThemeState(nextTheme);
    window.localStorage.setItem(themeStorageKey, nextTheme);
    document.documentElement.dataset.theme = nextTheme;
    document.documentElement.style.colorScheme = nextTheme;
  }, []);

  const setPhotoGridDensity = useCallback((density: PhotoGridDensity) => {
    setPhotoGridDensityState(density);
    window.localStorage.setItem(photoGridDensityStorageKey, density);
  }, []);

  const setShowTimelineEvents = useCallback((show: boolean) => {
    setShowTimelineEventsState(show);
    window.localStorage.setItem(showTimelineEventsStorageKey, String(show));
  }, []);

  const t = useCallback(
    (key: TranslationKey, values?: TranslationValues) => {
      const template = translations[locale][key];
      if (!values) return template;
      return template.replace(/\{(\w+)\}/g, (match, name: string) =>
        values[name] === undefined ? match : String(values[name]),
      );
    },
    [locale],
  );

  const value = useMemo(
    () => ({
      locale,
      setLocale,
      theme,
      setTheme,
      photoGridDensity,
      setPhotoGridDensity,
      showTimelineEvents,
      setShowTimelineEvents,
      t,
    }),
    [
      locale,
      photoGridDensity,
      setLocale,
      setPhotoGridDensity,
      setShowTimelineEvents,
      setTheme,
      showTimelineEvents,
      t,
      theme,
    ],
  );

  return (
    <PreferencesContext.Provider value={value}>
      {children}
    </PreferencesContext.Provider>
  );
}

export function usePreferences() {
  const context = useContext(PreferencesContext);
  if (!context)
    throw new Error("usePreferences must be used within PreferencesProvider.");
  return context;
}
