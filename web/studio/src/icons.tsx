// The MyRPA icon set (UX-1, studio-ux-plan.md): drawn for this project on a 24 × 24 grid, 1.75 px round strokes in
// `currentColor`, so icons follow the text colour and both themes. Attributes only (no style attribute: CSP). Icons are
// decorative (`aria-hidden`): the command next to them carries the accessible name.

import type { ReactNode } from 'react';

const paths = {
  // The MyRPA mark: a rounded tile holding three connected steps (a tiny workflow).
  logo: (
    <>
      <rect x="2.5" y="2.5" width="19" height="19" rx="5.5" />
      <path d="M8 8.5h8M8 8.5l4 7.5M16 8.5l-4 7.5" />
      <circle cx="8" cy="8.5" r="1.9" fill="currentColor" />
      <circle cx="16" cy="8.5" r="1.9" fill="currentColor" />
      <circle cx="12" cy="16" r="1.9" fill="currentColor" />
    </>
  ),
  'file-new': (
    <>
      <path d="M13.5 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8.5z" />
      <path d="M13.5 3v5.5H19M12 11.5v6M9 14.5h6" />
    </>
  ),
  'folder-open': (
    <>
      <path d="M3.5 18.5V6a1.5 1.5 0 0 1 1.5-1.5h4.2l2 2.2H18a1.5 1.5 0 0 1 1.5 1.5v1.6" />
      <path d="M3.5 18.5l2.6-7.2A1.5 1.5 0 0 1 7.5 10.3h13a1 1 0 0 1 .94 1.34L19 18.5z" />
    </>
  ),
  save: (
    <>
      <path d="M5 3.5h11.2l3.3 3.3V19a1.5 1.5 0 0 1-1.5 1.5H5A1.5 1.5 0 0 1 3.5 19V5A1.5 1.5 0 0 1 5 3.5z" />
      <path d="M7.5 3.5v4.5h7V3.5M7 20.5v-6.5h10v6.5" />
    </>
  ),
  'save-as': (
    <>
      <path d="M12 20.5H5A1.5 1.5 0 0 1 3.5 19V5A1.5 1.5 0 0 1 5 3.5h11.2l3.3 3.3V11" />
      <path d="M7.5 3.5v4.5h7V3.5M7 20.5V14h5" />
      <path d="M15.5 20.5l.5-2.5 4.2-4.2a1.4 1.4 0 0 1 2 2L18 20z" />
    </>
  ),
  undo: <path d="M9 14.5L4 9.5l5-5M4 9.5h10.5a5.5 5.5 0 0 1 0 11H11" />,
  redo: <path d="M15 14.5l5-5-5-5M20 9.5H9.5a5.5 5.5 0 0 0 0 11H13" />,
  cut: (
    <>
      <circle cx="6.5" cy="17.5" r="2.8" />
      <circle cx="17.5" cy="17.5" r="2.8" />
      <path d="M8.6 15.6L18.5 3.5M15.4 15.6L5.5 3.5" />
    </>
  ),
  copy: (
    <>
      <rect x="8.5" y="8.5" width="12" height="12" rx="2" />
      <path d="M15.5 8.5V5.5a2 2 0 0 0-2-2h-8a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h3" />
    </>
  ),
  paste: (
    <>
      <path d="M9 4.5H6.5a2 2 0 0 0-2 2V19a2 2 0 0 0 2 2h11a2 2 0 0 0 2-2V6.5a2 2 0 0 0-2-2H15" />
      <rect x="9" y="2.5" width="6" height="4" rx="1.2" />
      <path d="M8.5 12h7M8.5 15.5h5" />
    </>
  ),
  delete: <path d="M4 6.5h16M9.5 6.5V4h5v2.5M6 6.5l1 13.5h10l1-13.5M10 10.5v6M14 10.5v6" />,
  'move-up': <path d="M12 19.5v-15M6 10.5l6-6 6 6" />,
  'move-down': <path d="M12 4.5v15M6 13.5l6 6 6-6" />,
  validate: (
    <>
      <path d="M12 3l7.5 3v5.5c0 4.5-3.2 8.2-7.5 9.5-4.3-1.3-7.5-5-7.5-9.5V6z" />
      <path d="M8.5 12.2l2.5 2.5 4.5-5" />
    </>
  ),
  run: <path d="M7.5 4.8v14.4a.8.8 0 0 0 1.2.7l11.3-7.2a.8.8 0 0 0 0-1.4L8.7 4.1a.8.8 0 0 0-1.2.7z" />,
  stop: <rect x="5.5" y="5.5" width="13" height="13" rx="2.5" />,
  'zoom-in': (
    <>
      <circle cx="10.5" cy="10.5" r="6.5" />
      <path d="M15.3 15.3L20.5 20.5M10.5 7.8v5.4M7.8 10.5h5.4" />
    </>
  ),
  'zoom-out': (
    <>
      <circle cx="10.5" cy="10.5" r="6.5" />
      <path d="M15.3 15.3L20.5 20.5M7.8 10.5h5.4" />
    </>
  ),
  'zoom-fit': <path d="M4 9V5.5A1.5 1.5 0 0 1 5.5 4H9M15 4h3.5A1.5 1.5 0 0 1 20 5.5V9M20 15v3.5a1.5 1.5 0 0 1-1.5 1.5H15M9 20H5.5A1.5 1.5 0 0 1 4 18.5V15" />,
  theme: (
    <>
      <circle cx="12" cy="12" r="8.5" />
      <path d="M12 3.5a8.5 8.5 0 0 1 0 17z" fill="currentColor" />
    </>
  ),
  'panel-left': (
    <>
      <rect x="3.5" y="4.5" width="17" height="15" rx="2" />
      <path d="M9 4.5v15" />
    </>
  ),
  'panel-right': (
    <>
      <rect x="3.5" y="4.5" width="17" height="15" rx="2" />
      <path d="M15 4.5v15" />
    </>
  ),
  'panel-bottom': (
    <>
      <rect x="3.5" y="4.5" width="17" height="15" rx="2" />
      <path d="M3.5 14h17" />
    </>
  ),
  file: (
    <>
      <path d="M13.5 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8.5z" />
      <path d="M13.5 3v5.5H19" />
    </>
  ),
  folder: <path d="M3.5 18V6.5A1.5 1.5 0 0 1 5 5h4.2l2 2.2H19a1.5 1.5 0 0 1 1.5 1.5V18a1.5 1.5 0 0 1-1.5 1.5H5A1.5 1.5 0 0 1 3.5 18z" />,
  search: (
    <>
      <circle cx="10.5" cy="10.5" r="6.5" />
      <path d="M15.3 15.3L20.5 20.5" />
    </>
  ),
  star: <path d="M12 3.8l2.5 5.1 5.6.8-4.05 3.95.96 5.6L12 16.6l-5 2.65.96-5.6L3.9 9.7l5.6-.8z" />,
  recent: (
    <>
      <circle cx="12" cy="12" r="8.5" />
      <path d="M12 7.5V12l3 2" />
    </>
  ),
  activity: (
    <>
      <rect x="3.5" y="6.5" width="17" height="11" rx="2.5" />
      <path d="M7.5 12h9" />
    </>
  ),
  plugin: <path d="M9 3.5v4M15 3.5v4M6.5 7.5h11V11a5.5 5.5 0 0 1-11 0zM12 16.5v4" />,
  problems: (
    <>
      <path d="M12 3.5l9 16H3z" />
      <path d="M12 10v4.5M12 17.2v.3" />
    </>
  ),
  check: (
    <>
      <circle cx="12" cy="12" r="8.5" />
      <path d="M8.3 12.3l2.5 2.5 4.9-5.3" />
    </>
  ),
  connected: (
    <>
      <path d="M5 12a7 7 0 0 1 14 0" />
      <path d="M8.2 12a3.8 3.8 0 0 1 7.6 0" />
      <circle cx="12" cy="15.5" r="1.6" fill="currentColor" />
    </>
  ),
  close: <path d="M6.5 6.5l11 11M17.5 6.5l-11 11" />,
  // UX-3: activity kinds on designer cards, and the designer's own controls.
  sequence: (
    <>
      <rect x="6" y="3.5" width="12" height="4.5" rx="1.5" />
      <rect x="6" y="16" width="12" height="4.5" rx="1.5" />
      <path d="M12 8v8M9.5 13.5L12 16l2.5-2.5" />
    </>
  ),
  branch: <path d="M12 3.5v5M12 8.5l-6 5.5v6.5M12 8.5l6 5.5v6.5M4 18.5l2 2 2-2M16 18.5l2 2 2-2" />,
  loop: <path d="M17 8.5A6.5 6.5 0 1 0 18.5 12M17 3.5v5h-5" />,
  data: (
    <>
      <rect x="3.5" y="5.5" width="17" height="13" rx="2.5" />
      <path d="M8 10.5h8M8 14h8" />
    </>
  ),
  log: (
    <>
      <path d="M6 3.5h9.5l3 3V20a.5.5 0 0 1-.5.5H6a.5.5 0 0 1-.5-.5V4a.5.5 0 0 1 .5-.5z" />
      <path d="M8.5 9h7M8.5 12.5h7M8.5 16h4.5" />
    </>
  ),
  invoke: (
    <>
      <rect x="3.5" y="3.5" width="12" height="12" rx="2" />
      <path d="M12 12l8.5 8.5M20.5 14.5v6h-6" />
    </>
  ),
  browser: (
    <>
      <rect x="3" y="4.5" width="18" height="15" rx="2" />
      <path d="M3 9h18M6 6.8h.01M8.5 6.8h.01" />
    </>
  ),
  catch: (
    <>
      <path d="M12 3l7.5 3v5.5c0 4.5-3.2 8.2-7.5 9.5-4.3-1.3-7.5-5-7.5-9.5V6z" />
      <path d="M12 8.5v4.5M12 15.8v.2" />
    </>
  ),
  more: (
    <>
      <circle cx="12" cy="5.5" r="1.4" fill="currentColor" />
      <circle cx="12" cy="12" r="1.4" fill="currentColor" />
      <circle cx="12" cy="18.5" r="1.4" fill="currentColor" />
    </>
  ),
  'chevron-down': <path d="M6.5 9.5l5.5 5.5 5.5-5.5" />,
  'chevron-right': <path d="M9.5 6.5l5.5 5.5-5.5 5.5" />,
  'expand-all': <path d="M7 4.5l5 5 5-5M7 14.5l5 5 5-5" />,
  'collapse-all': <path d="M7 9.5l5-5 5 5M7 19.5l5-5 5 5" />,
} satisfies Record<string, ReactNode>;

/** Every icon of the set, by name. */
export type IconName = keyof typeof paths;

/** All icon names (for the icon gallery test and documentation). */
export const iconNames = Object.keys(paths) as IconName[];

/** One icon of the MyRPA set; decorative unless `label` is given (then it is an image with that name). */
export function Icon({ name, size = 18, label }: { name: IconName; size?: number; label?: string }) {
  return (
    <svg
      className={`icon icon-${name}`}
      viewBox="0 0 24 24"
      width={size}
      height={size}
      fill="none"
      stroke="currentColor"
      strokeWidth={1.75}
      strokeLinecap="round"
      strokeLinejoin="round"
      focusable="false"
      {...(label === undefined ? { 'aria-hidden': true } : { role: 'img', 'aria-label': label })}
    >
      {paths[name]}
    </svg>
  );
}
