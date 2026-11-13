import { describe, expect, it } from 'vitest';
import { scrubStoredApiKey } from './prefs.svelte';

describe('API key storage', () => {
  it('removes a leftover session key and does not read or write it', () => {
    const ops: string[] = [];
    const storage = {
      removeItem(key: string) {
        ops.push('remove ' + key);
      },
      getItem(): string {
        throw new Error('read');
      },
      setItem(): void {
        throw new Error('write');
      }
    };
    scrubStoredApiKey(storage);
    expect(ops).toEqual(['remove windlass-api-key']);
  });

  it('does nothing when storage is missing', () => {
    expect(() => scrubStoredApiKey(null)).not.toThrow();
  });
});
