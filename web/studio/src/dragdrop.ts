// Drag-and-drop in the designer (W7, ADR-0033) by plain pointer hit-testing, as ADR-0021 decided (no DnD library):
// drag an activity card to move it, or a toolbox entry to insert a new activity. While dragging, nothing goes through
// React: one indicator element is positioned through the CSSOM (allowed by the CSP) and the drop is checked with the
// same rules as every other edit, so a refused place is shown as refused and never changes the document. One command
// (and one undo step) happens on drop. Keyboard equivalents: Cut/Paste, Move up/down, and the empty-slot zones.

import { indexDocument } from './document';
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

  const sourceAt = (target: EventTarget | null): Source | undefined => {
    if (!(target instanceof Element) || target.closest('input, textarea, select, .drop-zone')) {
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
    root.classList.toggle('drop-refused', next === undefined || next.refusal !== undefined);
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
    root.classList.remove('dragging', 'drop-refused');
  };

  const onDown = (event: PointerEvent) => {
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
      page.body.append(indicator);
    }

    event.preventDefault();
    show(dropCandidate(studio, page.elementFromPoint(event.clientX, event.clientY), event.clientY, press.source));
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
        if (source.kind === 'node') {
          studio.moveNodeTo(source.key, drop.target);
        } else {
          studio.insertActivity(source.type, drop.target);
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
