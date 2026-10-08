// Drag-and-drop in the designer (W7, ADR-0033) by plain pointer hit-testing, as ADR-0021 decided (no DnD library):
// drag an activity card to move it, or a toolbox entry to insert a new activity. While dragging, nothing goes through
// React: one indicator element is positioned through the CSSOM (allowed by the CSP) and the drop is checked with the
// same rules as every other edit, so a refused place is shown as refused and never changes the document. One command
// (and one undo step) happens on drop. Keyboard equivalents: Cut/Paste, Move up/down, and the empty-slot zones.

import { indexDocument } from './document';
import { stepSize, type Point } from './graph';
import type { Target } from './placement';
import type { Studio } from './studio';

/** Pixels the pointer must travel before a press becomes a drag (a shorter press stays a click). */
const threshold = 5;

type Source = { readonly kind: 'node'; readonly key: string } | { readonly kind: 'activity'; readonly type: string };

/** Where a drop at a point would go, and why not when it is refused. */
export interface DropCandidate {
  readonly target?: Target;
  readonly refusal?: string;
  /** The element the indicator outlines (a zone) or the card edge it marks (before/after). */
  readonly element: HTMLElement;
  readonly edge: 'before' | 'after' | 'inside';
}

/**
 * The drop place under a point: an empty-slot or empty-list zone (`data-drop-parent`), or the gap before or after a
 * card in a list (upper or lower half of the card). Cards in slots and the root have no gaps; zones fill those.
 */
export function dropCandidate(studio: Studio, element: Element | null, clientY: number, source: Source): DropCandidate | undefined {
  const document = studio.store.get().document;
  if (document === undefined || !(element instanceof HTMLElement)) {
    return undefined;
  }

  const index = indexDocument(document);
  const zone = element.closest<HTMLElement>('[data-drop-parent]');
  if (zone) {
    const parent = index.byKey.get(zone.dataset.dropParent!);
    if (!parent) {
      return undefined;
    }

    const position = zone.dataset.dropSlot !== undefined ? { slot: zone.dataset.dropSlot } : { index: Number(zone.dataset.dropIndex ?? 0) };
    return check(studio, { parentPath: parent.path, position }, zone, 'inside', source);
  }

  const item = element.closest<HTMLElement>('[role="treeitem"][data-key]');
  const card = item?.querySelector<HTMLElement>(':scope > .node');
  const entry = item ? index.byKey.get(item.dataset.key!) : undefined;
  const last = entry?.path.at(-1);
  if (!card || !entry || last === undefined || !('children' in last)) {
    return undefined;
  }

  const rect = card.getBoundingClientRect();
  const after = clientY > rect.top + rect.height / 2;
  return check(studio, { parentPath: entry.path.slice(0, -1), position: { index: last.children + (after ? 1 : 0) } }, card, after ? 'after' : 'before', source);
}

function check(studio: Studio, target: Target, element: HTMLElement, edge: DropCandidate['edge'], source: Source): DropCandidate {
  const refusal = source.kind === 'node' ? studio.moveRefusalTo(source.key, target) : studio.placeRefusalFor(target);
  return { target, refusal, element, edge };
}

/** Where a step dropped at the pointer goes on a canvas: centred on the pointer, in canvas units (the designer may be zoomed). */
function canvasPoint(canvas: HTMLElement, event: PointerEvent, zoom: number): Point {
  const surface = canvas.querySelector<HTMLElement>('.flow-surface') ?? canvas;
  const rect = surface.getBoundingClientRect();
  return { x: Math.max(0, (event.clientX - rect.left) / zoom - stepSize.width / 2), y: Math.max(0, (event.clientY - rect.top) / zoom - stepSize.height / 2) };
}

/** Installs drag-and-drop on the Studio's root element; returns the uninstaller. */
export function installDragAndDrop(root: HTMLElement, studio: Studio): () => void {
  const page = root.ownerDocument;
  let press: { source: Source; x: number; y: number; pointerId: number } | undefined;
  let dragging = false;
  let candidate: DropCandidate | undefined;
  let suppressClick = false;
  const indicator = page.createElement('div');
  indicator.className = 'drop-indicator';
  indicator.setAttribute('aria-hidden', 'true');
  // While dragging, one transparent overlay over the page carries the cursor (grabbing / not-allowed) and keeps text from
  // being selected. Changing the cursor on the page root instead (an inherited property) restyles every element, which
  // on a 3,000-node workflow is the slowest part of a drag (UX-3). Hit-testing looks through the overlay.
  const overlay = page.createElement('div');
  overlay.className = 'drag-overlay';
  overlay.setAttribute('aria-hidden', 'true');
  const under = (x: number, y: number) => page.elementsFromPoint(x, y).find((element) => element !== overlay && element !== indicator) ?? null;

  const sourceAt = (target: EventTarget | null): Source | undefined => {
    // A card's own controls and its inline editors (UX-3) never start a drag; a flowchart canvas moves its own steps (G-2).
    if (!(target instanceof Element) || target.closest('input, textarea, select, .drop-zone, .inline-properties, .node button, [role="menu"], .flow-canvas')) {
      return undefined;
    }

    const insert = target.closest<HTMLButtonElement>('button.insert[data-activity]');
    if (insert) {
      return insert.disabled ? undefined : { kind: 'activity', type: insert.dataset.activity! };
    }

    const item = target.closest<HTMLElement>('.designer [role="treeitem"][data-key]');
    return item && target.closest('.node') ? { kind: 'node', key: item.dataset.key! } : undefined;
  };

  const show = (next: DropCandidate | undefined) => {
    candidate = next;
    const refused = next === undefined || next.refusal !== undefined;
    root.classList.toggle('drop-refused', refused);
    overlay.classList.toggle('refused', refused);
    if (next === undefined || next.refusal !== undefined) {
      indicator.hidden = true;
      return;
    }

    const rect = next.element.getBoundingClientRect();
    indicator.hidden = false;
    indicator.classList.toggle('zone', next.edge === 'inside');
    indicator.style.left = `${rect.left}px`;
    indicator.style.width = `${rect.width}px`;
    indicator.style.top = `${next.edge === 'after' ? rect.bottom : rect.top}px`;
    indicator.style.height = next.edge === 'inside' ? `${rect.height}px` : '';
  };

  const end = () => {
    press = undefined;
    dragging = false;
    candidate = undefined;
    indicator.remove();
    overlay.remove();
    overlay.classList.remove('refused');
    root.classList.remove('dragging', 'drop-refused');
  };

  const onDown = (event: PointerEvent) => {
    // A drag released where no click follows (for example over another element) must not swallow the next real click.
    suppressClick = false;
    const source = event.button === 0 ? sourceAt(event.target) : undefined;
    press = source ? { source, x: event.clientX, y: event.clientY, pointerId: event.pointerId } : undefined;
  };

  const onMove = (event: PointerEvent) => {
    if (!press || event.pointerId !== press.pointerId) {
      return;
    }

    if (!dragging) {
      if (Math.hypot(event.clientX - press.x, event.clientY - press.y) < threshold) {
        return;
      }

      dragging = true;
      root.classList.add('dragging');
      indicator.hidden = true;
      page.body.append(overlay, indicator);
    }

    event.preventDefault();
    show(dropCandidate(studio, under(event.clientX, event.clientY), event.clientY, press.source));
  };

  const onUp = (event: PointerEvent) => {
    if (!press || event.pointerId !== press.pointerId) {
      return;
    }

    if (dragging) {
      suppressClick = true; // the click that follows a drag is not a click on what was dragged
      const { source } = press;
      const drop = candidate;
      if (drop?.target !== undefined && drop.refusal === undefined) {
        // On a flowchart canvas the new step goes where it was dropped (G-2).
        const at = drop.element.dataset.dropCanvas !== undefined ? canvasPoint(drop.element, event, studio.store.get().zoom) : undefined;
        if (source.kind === 'node') {
          studio.moveNodeTo(source.key, drop.target, at);
        } else {
          studio.insertActivity(source.type, drop.target, at);
        }
      } else {
        studio.notify(drop?.refusal ? `Not dropped: ${drop.refusal}` : 'Not dropped: drop on a gap between activities or on an empty slot.');
      }
    }

    end();
  };

  const onClick = (event: MouseEvent) => {
    if (suppressClick) {
      suppressClick = false;
      event.stopPropagation();
      event.preventDefault();
    }
  };

  const onKey = (event: KeyboardEvent) => {
    if (dragging && event.key === 'Escape') {
      studio.notify('Drag cancelled.');
      end();
    }
  };

  root.addEventListener('pointerdown', onDown);
  page.addEventListener('pointermove', onMove);
  page.addEventListener('pointerup', onUp);
  page.addEventListener('pointercancel', end);
  root.addEventListener('click', onClick, true);
  page.addEventListener('keydown', onKey);
  return () => {
    end();
    root.removeEventListener('pointerdown', onDown);
    page.removeEventListener('pointermove', onMove);
    page.removeEventListener('pointerup', onUp);
    page.removeEventListener('pointercancel', end);
    root.removeEventListener('click', onClick, true);
    page.removeEventListener('keydown', onKey);
  };
}
