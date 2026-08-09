// Inline SVG flags, deliberately not emoji: Windows ships no flag font, so 🇵🇱 renders as
// the letters "PL" in a box on the desktop browser. These are drawn once, cost ~200 bytes
// each, and look the same on every device in the house.
//
// Simplified on purpose — at 20 px wide the point is instant recognition, not heraldry.

import type { ReactNode } from 'react'
import type { Strings } from '../i18n'

const FLAGS: Record<string, ReactNode> = {
  PL: (
    <>
      <rect width="20" height="7.5" fill="#fff" />
      <rect y="7.5" width="20" height="7.5" fill="#dc143c" />
    </>
  ),
  US: (
    <>
      <rect width="20" height="15" fill="#fff" />
      {/* 13 stripes; the 7 red ones are enough to read as the flag */}
      {[0, 2, 4, 6, 8, 10, 12].map((row) => (
        <rect key={row} y={(row * 15) / 13} width="20" height={15 / 13} fill="#b22234" />
      ))}
      <rect width="8.6" height={(7 * 15) / 13} fill="#3c3b6e" />
      {[
        [1.6, 1.3], [4.2, 1.3], [6.8, 1.3],
        [2.9, 3.1], [5.5, 3.1],
        [1.6, 4.9], [4.2, 4.9], [6.8, 4.9],
      ].map(([cx, cy]) => (
        <circle key={`${cx}-${cy}`} cx={cx} cy={cy} r="0.62" fill="#fff" />
      ))}
    </>
  ),
  GB: (
    <>
      <rect width="20" height="15" fill="#012169" />
      <path d="M0 0 L20 15 M20 0 L0 15" stroke="#fff" strokeWidth="3" />
      <path d="M0 0 L20 15 M20 0 L0 15" stroke="#c8102e" strokeWidth="1.6" />
      <path d="M10 0 V15 M0 7.5 H20" stroke="#fff" strokeWidth="5" />
      <path d="M10 0 V15 M0 7.5 H20" stroke="#c8102e" strokeWidth="3" />
    </>
  ),
};

/** "en-US" → "US". Falls back to the language when a locale carries no region. */
function regionOf(locale: string): string {
  const parts = locale.split(/[-_]/);
  return (parts[1] ?? parts[0] ?? '').toUpperCase();
}

/**
 * The language name for a locale. The flag already carries the country, so this names the
 * language; an unknown locale shows its own code rather than disappearing.
 */
export function localeLabel(locale: string, strings: Strings): string {
  switch (locale.toLowerCase()) {
    case 'en-us':
      return strings.localeEnUs;
    case 'en-gb':
      return strings.localeEnGb;
    case 'pl-pl':
      return strings.localePlPl;
    default:
      return locale;
  }
}

/** "64 MB" — download sizes are shown before anything is fetched, never after. */
export function formatBytes(bytes: number): string {
  if (bytes >= 1024 * 1024 * 1024) return `${(bytes / (1024 * 1024 * 1024)).toFixed(1)} GB`;
  if (bytes >= 1024 * 1024) return `${Math.round(bytes / (1024 * 1024))} MB`;
  if (bytes >= 1024) return `${Math.round(bytes / 1024)} KB`;
  return `${bytes} B`;
}

/**
 * The flag for a locale. Unknown regions fall back to the region letters rather than
 * rendering nothing, so a hand-added locale still shows something meaningful.
 */
export default function Flag({ locale, className = '' }: { locale: string; className?: string }) {
  const region = regionOf(locale);
  const art = FLAGS[region];

  if (art === undefined) {
    return (
      <span
        aria-hidden
        className={`inline-flex h-[15px] w-5 shrink-0 items-center justify-center rounded-[2px] border border-stone-300 bg-stone-100 text-[8px] font-semibold text-stone-500 ${className}`}
      >
        {region.slice(0, 2)}
      </span>
    );
  }

  return (
    <svg
      viewBox="0 0 20 15"
      className={`inline-block h-[15px] w-5 shrink-0 rounded-[2px] ${className}`}
      aria-hidden
    >
      {/* Clip so the simplified art keeps the rounded corners. */}
      <defs>
        <clipPath id={`flag-clip-${region}`}>
          <rect width="20" height="15" rx="2" />
        </clipPath>
      </defs>
      <g clipPath={`url(#flag-clip-${region})`}>{art}</g>
      <rect width="20" height="15" rx="2" fill="none" stroke="rgba(0,0,0,0.15)" strokeWidth="1" />
    </svg>
  );
}
