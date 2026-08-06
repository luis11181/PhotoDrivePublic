"use client";

import { Languages, Moon, Sun } from "lucide-react";
import { usePreferences } from "@/lib/preferences";

export function PreferenceControls({
  className = "",
}: {
  className?: string;
}) {
  const { locale, setLocale, theme, setTheme, t } = usePreferences();
  const nextLocale = locale === "en" ? "es" : "en";
  const nextTheme = theme === "light" ? "dark" : "light";

  return (
    <div
      className={`preference-controls ${className}`}
      aria-label={`${t("preferences.language")} · ${theme}`}
    >
      <button
        className="preference-control"
        type="button"
        onClick={() => setLocale(nextLocale)}
        aria-label={
          nextLocale === "es"
            ? t("preferences.switchToSpanish")
            : t("preferences.switchToEnglish")
        }
        title={
          nextLocale === "es"
            ? t("preferences.switchToSpanish")
            : t("preferences.switchToEnglish")
        }
      >
        <Languages />
        <span>{locale.toUpperCase()}</span>
      </button>
      <button
        className="preference-control preference-theme"
        type="button"
        onClick={() => setTheme(nextTheme)}
        aria-label={
          nextTheme === "dark"
            ? t("preferences.darkTheme")
            : t("preferences.lightTheme")
        }
        title={
          nextTheme === "dark"
            ? t("preferences.darkTheme")
            : t("preferences.lightTheme")
        }
      >
        {theme === "light" ? <Moon /> : <Sun />}
      </button>
    </div>
  );
}
