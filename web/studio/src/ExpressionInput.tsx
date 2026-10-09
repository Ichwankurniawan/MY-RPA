// Expression completion (ADR-0041, E-2): an expression field with a list of the names in scope and the expression
// functions. The Studio's own combobox (ARIA combobox pattern, no editor library: ADR-0032). The names come from the
// server's validation for the field's place in the workflow (fetched when the field gets focus); the browser only
// filters them while typing.

import { useLayoutEffect, useRef, useState, type KeyboardEvent } from 'react';
import { useStudio, useStudioState } from './context';
import { indexDocument } from './document';
import type { ExpressionFunction, ScopeName } from './types';
import { nodeJsonPath } from './workflowData';

const identifierChar = /[A-Za-z0-9_]/;
const identifierStart = /[A-Za-z_]/;

/** Whether `caret` in `text` is inside a string literal ('…' or "…", with backslash escapes). */
export function inString(text: string, caret: number): boolean {
  let quote: string | undefined;
  for (let i = 0; i < caret && i < text.length; i++) {
    const c = text[i];
    if (quote !== undefined) {
      if (c === '\\') {
        i++;
      } else if (c === quote) {
        quote = undefined;
      }
    } else if (c === "'" || c === '"') {
      quote = c;
    }
  }

  return quote !== undefined;
}

/**
 * The name at `caret`: where the word starts and ends and what is typed before the caret. Undefined inside a string,
 * in a number, or after a '.' (a member of a dictionary, not a name in scope).
 */
export function completionContext(text: string, caret: number): { start: number; end: number; prefix: string } | undefined {
  if (inString(text, caret)) {
    return undefined;
  }

  let start = caret;
  while (start > 0 && identifierChar.test(text[start - 1])) {
    start--;
  }

  let end = caret;
  while (end < text.length && identifierChar.test(text[end])) {
    end++;
  }

  const prefix = text.slice(start, caret);
  if ((prefix !== '' && !identifierStart.test(prefix[0])) || text[start - 1] === '.') {
    return undefined;
  }

  return { start, end, prefix };
}

/** The function whose call the caret is in: the innermost open '(' that follows a name. */
export function callAt(text: string, caret: number): string | undefined {
  let depth = 0;
  for (let i = Math.min(caret, text.length) - 1; i >= 0; i--) {
    if (inString(text, i)) {
      continue;
    }

    if (text[i] === ')') {
      depth++;
    } else if (text[i] === '(') {
      if (depth > 0) {
        depth--;
        continue;
      }

      let start = i;
      while (start > 0 && identifierChar.test(text[start - 1])) {
        start--;
      }

      return start < i ? text.slice(start, i) : undefined;
    }
  }

  return undefined;
}

/** One entry of the completion list. */
export interface CompletionItem {
  readonly kind: 'name' | 'function';
  readonly label: string;
  /** The kind and type of a name, or a function's signature. */
  readonly detail: string;
  /** What accepting it writes in place of the word: the name, or the function and its '('. */
  readonly insert: string;
}

/** The names, then the functions, that start with `prefix` (ignoring case); at most 50. */
export function completionItems(prefix: string, names: readonly ScopeName[], functions: readonly ExpressionFunction[]): CompletionItem[] {
  const lower = prefix.toLowerCase();
  const matches = (name: string) => name.toLowerCase().startsWith(lower);
  return [
    ...names.filter((n) => matches(n.name)).map((n): CompletionItem => ({ kind: 'name', label: n.name, detail: `${n.kind.toLowerCase()} · ${n.type}`, insert: n.name })),
    ...functions
      .filter((f) => matches(f.name))
      .map((f): CompletionItem => ({ kind: 'function', label: f.name, detail: f.signature, insert: `${f.name}(${f.maxArguments === 0 ? ')' : ''}` })),
  ].slice(0, 50);
}

interface Menu {
  readonly start: number;
  readonly end: number;
  readonly items: readonly CompletionItem[];
  readonly active: number;
}

/**
 * An expression field with completion. Typing a name opens the list (Ctrl+Space opens it anywhere); Up and Down move,
 * Enter or Tab accepts, Escape closes. Inside a function call its signature is shown under the field.
 */
export function ExpressionInput({
  id,
  nodeKey,
  suffix,
  value,
  disabled,
  invalid,
  describedBy,
  placeholder,
  label,
  onChange,
}: {
  id: string;
  /** The node the expression belongs to, and the rest of its JSON path (`.properties.message`), for the names in scope. */
  nodeKey: string;
  suffix: string;
  value: string;
  disabled: boolean;
  invalid?: boolean;
  describedBy?: string;
  placeholder?: string;
  /** The accessible name when there is no `<label>` for the field. */
  label?: string;
  onChange: (text: string) => void;
}) {
  const studio = useStudio();
  const functions = useStudioState((s) => s.expressionFunctions);
  const input = useRef<HTMLInputElement>(null);
  const caretAfter = useRef<number | undefined>(undefined);
  // Whether the user typed since the field got focus (the names may arrive after the first keystrokes).
  const typed = useRef(false);
  const [names, setNames] = useState<readonly ScopeName[]>([]);
  const [menu, setMenu] = useState<Menu | undefined>();
  const [caret, setCaret] = useState(-1);
  const listId = `${id}-completions`;

  // After accepting an entry the caret goes after the inserted text (once the new value is rendered).
  useLayoutEffect(() => {
    if (caretAfter.current !== undefined && input.current !== null) {
      input.current.setSelectionRange(caretAfter.current, caretAfter.current);
      caretAfter.current = undefined;
    }
  });

  const suggestFrom = (from: readonly ScopeName[], text: string, at: number, explicit: boolean) => {
    const context = completionContext(text, at);
    const items = context !== undefined && (explicit || context.prefix !== '') ? completionItems(context.prefix, from, functions) : [];
    setMenu(context !== undefined && items.length > 0 ? { start: context.start, end: context.end, items, active: 0 } : undefined);
  };

  const suggest = (text: string, at: number, explicit: boolean) => suggestFrom(names, text, at, explicit);

  const loadNames = () => {
    const document = studio.store.get().document;
    const entry = document ? indexDocument(document).byKey.get(nodeKey) : undefined;
    if (entry === undefined) {
      return;
    }

    void studio.namesInScope(nodeJsonPath(entry.path) + suffix).then((loaded) => {
      setNames(loaded);
      // Typed before the names arrived: suggest again with them (never just for getting focus).
      const field = input.current;
      if (typed.current && field !== null && field === field.ownerDocument.activeElement) {
        suggestFrom(loaded, field.value, field.selectionStart ?? field.value.length, false);
      }
    });
  };

  const accept = (item: CompletionItem) => {
    if (menu === undefined) {
      return;
    }

    caretAfter.current = menu.start + item.insert.length;
    setCaret(caretAfter.current);
    setMenu(undefined);
    onChange(value.slice(0, menu.start) + item.insert + value.slice(menu.end));
  };

  const onKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    if (event.key === ' ' && (event.ctrlKey || event.metaKey)) {
      event.preventDefault();
      suggest(value, event.currentTarget.selectionStart ?? value.length, true);
      return;
    }

    if (menu === undefined) {
      return;
    }

    const count = menu.items.length;
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      setMenu({ ...menu, active: (menu.active + (event.key === 'ArrowDown' ? 1 : count - 1)) % count });
    } else if (event.key === 'Enter' || event.key === 'Tab') {
      accept(menu.items[menu.active]);
    } else if (event.key === 'Escape') {
      setMenu(undefined);
    } else {
      return;
    }

    // The list owns these keys while it is open (not the tree, the dialog or the window's shortcuts).
    event.preventDefault();
    event.stopPropagation();
  };

  const call = caret >= 0 ? callAt(value, caret) : undefined;
  const signature = call !== undefined ? functions.find((f) => f.name === call) : undefined;
  const signatureId = `${id}-signature`;
  const described = [describedBy, signature ? signatureId : undefined].filter(Boolean).join(' ') || undefined;
  return (
    <div className="expression-input">
      <input
        ref={input}
        id={id}
        className="code"
        value={value}
        disabled={disabled}
        spellCheck={false}
        autoComplete="off"
        placeholder={placeholder}
        aria-label={label}
        aria-invalid={invalid}
        aria-describedby={described}
        role="combobox"
        aria-autocomplete="list"
        aria-expanded={menu !== undefined}
        aria-controls={menu !== undefined ? listId : undefined}
        aria-activedescendant={menu !== undefined ? `${listId}-${menu.active}` : undefined}
        onFocus={(event) => {
          typed.current = false;
          loadNames();
          setCaret(event.currentTarget.selectionStart ?? value.length);
        }}
        onBlur={() => {
          setMenu(undefined);
          setCaret(-1);
        }}
        onSelect={(event) => setCaret(event.currentTarget.selectionStart ?? value.length)}
        onKeyDown={onKeyDown}
        onChange={(event) => {
          const at = event.target.selectionStart ?? event.target.value.length;
          typed.current = true;
          setCaret(at);
          onChange(event.target.value);
          suggest(event.target.value, at, false);
        }}
      />
      {menu !== undefined && (
        <ul id={listId} role="listbox" className="completions" aria-label="Completions">
          {menu.items.map((item, i) => (
            <li
              key={`${item.kind}:${item.label}`}
              id={`${listId}-${i}`}
              role="option"
              aria-selected={i === menu.active}
              className={`completion completion-${item.kind}${i === menu.active ? ' active' : ''}`}
              // Mouse down, not click: the field keeps the focus, so the list does not close before the choice counts.
              onMouseDown={(event) => {
                event.preventDefault();
                accept(item);
              }}
            >
              <span className="completion-label">{item.label}</span> <span className="completion-detail">{item.detail}</span>
            </li>
          ))}
        </ul>
      )}
      {signature !== undefined && (
        <small id={signatureId} className="signature-hint">
          <code>{signature.signature}</code> — {signature.description}
        </small>
      )}
    </div>
  );
}
