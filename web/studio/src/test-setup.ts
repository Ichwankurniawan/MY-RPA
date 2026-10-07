// Runs before every test file: each test starts with empty local storage, so no crash-recovery draft (drafts.ts) leaks
// from one test into another.
import { beforeEach } from 'vitest';

beforeEach(() => {
  localStorage.clear();
});
