import { PAGE_GUIDES } from "./pageGuides";

interface PageIntroProps {
  path: string;
  pageTitle: string;
  /** True when no intro has ever been dismissed in this browser: adds a one-line welcome. */
  firstEver: boolean;
  onDismiss: () => void;
}

/** The short "how this page works" card shown at the top of a page on the first visit. */
export default function PageIntro({ path, pageTitle, firstEver, onDismiss }: PageIntroProps) {
  const guide = PAGE_GUIDES[path];
  if (!guide) return null;
  return (
    <section className="page-intro" aria-label={`About ${pageTitle}`}>
      <button type="button" className="page-intro-close" onClick={onDismiss} aria-label="Close this guide" data-tip="Hide this guide. Bring it back any time with Page guide in the top bar.">
        ×
      </button>
      {firstEver && (
        <p style={{ fontWeight: 600 }}>
          Welcome! Each page shows a short guide like this the first time you open it.
        </p>
      )}
      <h2>About {pageTitle}</h2>
      <p>{guide.intro}</p>
      {guide.steps.length > 0 && (
        <ul>
          {guide.steps.map((step) => <li key={step}>{step}</li>)}
        </ul>
      )}
      {guide.tip && <p style={{ color: "var(--color-text-muted)" }}>{guide.tip}</p>}
      <div className="page-intro-actions">
        <button type="button" className="btn-primary" onClick={onDismiss}>Got it</button>
        <span>
          Hover or tab to any <span className="info-tip" aria-hidden="true" style={{ cursor: "default" }}>?</span> or button for a short explanation.
        </span>
      </div>
    </section>
  );
}
