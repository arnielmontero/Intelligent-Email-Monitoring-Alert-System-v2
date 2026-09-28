export type ThemePreference = "system" | "light" | "dark";

const STORAGE_KEY = "iemas-theme";
const media = () => window.matchMedia("(prefers-color-scheme: light)");

export function getThemePreference(): ThemePreference {
  try {
    const stored = localStorage.getItem(STORAGE_KEY);
    return stored === "light" || stored === "dark" ? stored : "system";
  } catch {
    return "system";
  }
}

export function applyTheme(preference: ThemePreference) {
  const resolved = preference === "system" ? (media().matches ? "light" : "dark") : preference;
  document.documentElement.dataset.theme = resolved;
}

export function setThemePreference(preference: ThemePreference) {
  try {
    if (preference === "system") localStorage.removeItem(STORAGE_KEY);
    else localStorage.setItem(STORAGE_KEY, preference);
  } catch {
    // Storage unavailable (private mode): the choice still applies for this page load.
  }
  applyTheme(preference);
}

/** Applies the saved choice before the first render and follows OS changes while on "System". */
export function initTheme() {
  applyTheme(getThemePreference());
  media().addEventListener("change", () => {
    if (getThemePreference() === "system") applyTheme("system");
  });
}
