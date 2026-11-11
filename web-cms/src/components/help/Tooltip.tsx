import { useEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";

/**
 * One tooltip for the whole CMS. Any element with a `data-tip="..."` attribute shows a readable bubble
 * on mouse hover or keyboard focus, so a page adds help to a button or field with a single attribute.
 * Mounted once in AppShell.
 */
export function TooltipLayer() {
  const [tip, setTip] = useState<{ text: string; rect: DOMRect } | null>(null);
  const current = useRef<Element | null>(null);
  // Whether the tooltip was opened by the mouse or by keyboard focus: moving the mouse away only closes a mouse one.
  const source = useRef<"mouse" | "focus" | null>(null);
  const timer = useRef<number | undefined>(undefined);

  useEffect(() => {
    const find = (target: EventTarget | null) =>
      target instanceof Element ? target.closest("[data-tip]") : null;

    const show = (el: Element, delay: number, from: "mouse" | "focus") => {
      const text = el.getAttribute("data-tip");
      if (!text) return;
      current.current = el;
      source.current = from;
      window.clearTimeout(timer.current);
      timer.current = window.setTimeout(() => {
        if (current.current === el && el.isConnected) setTip({ text, rect: el.getBoundingClientRect() });
      }, delay);
    };
    const hide = () => {
      current.current = null;
      source.current = null;
      window.clearTimeout(timer.current);
      setTip(null);
    };

    const onOver = (e: MouseEvent) => {
      const el = find(e.target);
      if (el === current.current) return;
      if (el) show(el, 250, "mouse");
      else if (source.current !== "focus") hide();
    };
    const onFocus = (e: FocusEvent) => {
      const el = find(e.target);
      if (el) show(el, 0, "focus");
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") hide();
    };

    document.addEventListener("mouseover", onOver);
    document.addEventListener("focusin", onFocus);
    document.addEventListener("focusout", hide);
    document.addEventListener("keydown", onKey);
    window.addEventListener("scroll", hide, true);
    return () => {
      document.removeEventListener("mouseover", onOver);
      document.removeEventListener("focusin", onFocus);
      document.removeEventListener("focusout", hide);
      document.removeEventListener("keydown", onKey);
      window.removeEventListener("scroll", hide, true);
      window.clearTimeout(timer.current);
    };
  }, []);

  if (!tip) return null;

  const width = Math.min(300, window.innerWidth - 24);
  const left = Math.max(12, Math.min(tip.rect.left + tip.rect.width / 2 - width / 2, window.innerWidth - width - 12));
  // Prefer below the element; flip above when it would run off the bottom of the window.
  const below = tip.rect.bottom + 8 + 120 < window.innerHeight;
  const style: React.CSSProperties = below
    ? { left, top: tip.rect.bottom + 8, width }
    : { left, bottom: window.innerHeight - tip.rect.top + 8, width };

  return createPortal(
    <div className="tip-bubble" role="tooltip" style={style}>
      {tip.text}
    </div>,
    document.body,
  );
}

/** A small "?" next to a label or card title. Hover or tab to it to read `text`. */
export function InfoTip({ text, label = "More information" }: { text: string; label?: string }) {
  return (
    <span className="info-tip" tabIndex={0} role="img" aria-label={`${label}: ${text}`} data-tip={text}
      onClick={(e) => { e.preventDefault(); e.stopPropagation(); }}>
      ?
    </span>
  );
}

/** A field label with a "?" explaining the field. */
export function TipLabel({ children, tip }: { children: React.ReactNode; tip: string }) {
  return (
    <span className="tip-label">
      {children}
      <InfoTip text={tip} />
    </span>
  );
}
