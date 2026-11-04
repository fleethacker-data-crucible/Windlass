import { describe, expect, it } from 'vitest';
import { SseParser } from './sse';

describe('SseParser', () => {
  it('reads comments, data and the end marker', () => {
    const p = new SseParser();
    const ev = p.push(': accepted\n\ndata: {"a":1}\n\n: keepalive\n\ndata: [DONE]\n\n');
    expect(ev).toEqual([
      { type: 'comment', text: 'accepted' },
      { type: 'data', data: '{"a":1}' },
      { type: 'comment', text: 'keepalive' },
      { type: 'data', data: '[DONE]' }
    ]);
  });
  it('waits for an event that arrives in pieces, and accepts CRLF', () => {
    const p = new SseParser();
    expect(p.push('data: {"x"')).toEqual([]);
    expect(p.push(':2}\r\n')).toEqual([]);
    expect(p.push('\r\ndata: next\r\n\r\n')).toEqual([
      { type: 'data', data: '{"x":2}' },
      { type: 'data', data: 'next' }
    ]);
  });
  it('keeps multi-line data together and ignores other fields', () => {
    expect(new SseParser().push('event: x\ndata: a\ndata: b\n\n')).toEqual([{ type: 'data', data: 'a\nb' }]);
  });
});
