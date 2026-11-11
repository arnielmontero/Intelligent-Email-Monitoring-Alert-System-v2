import { PAGE_GUIDES } from "./pageGuides";

const SEEN_KEY = "iemas-page-guides-seen";

/** Pages whose intro this browser has already dismissed. Stored per browser, so a new PC or browser shows them again. */
export function loadSeenGuides(): string[] {
  try {
    const raw = localStorage.getItem(SEEN_KEY);
    const parsed: unknown = raw ? JSON.parse(raw) : [];
    return Array.isArray(parsed) ? parsed.filter((p): p is string => typeof p === "string") : [];
  } catch {
    return [];
  }
}

export function saveSeenGuides(paths: string[]) {
  try {
    localStorage.setItem(SEEN_KEY, JSON.stringify(paths));
  } catch {
    // Storage unavailable (private window, blocked site data): the intro simply shows again next time.
  }
}

export function hasGuide(path: string) {
  return path in PAGE_GUIDES;
}
