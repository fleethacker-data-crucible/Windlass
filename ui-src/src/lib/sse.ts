// Incremental parser for the server-sent events that /v1/chat/completions streams (docs/API.md "Slow but alive"):
// `: accepted` and `: keepalive` comments, `data: {json}` events and the final `data: [DONE]`.
export type SseEvent = { type: 'comment'; text: string } | { type: 'data'; data: string };

export class SseParser {
  private buffer = '';

  push(chunk: string): SseEvent[] {
    this.buffer += chunk.replace(/\r\n/g, '\n');
    const out: SseEvent[] = [];
    let i: number;
    while ((i = this.buffer.indexOf('\n\n')) >= 0) {
      const block = this.buffer.slice(0, i);
      this.buffer = this.buffer.slice(i + 2);
      const data: string[] = [];
      for (const line of block.split('\n')) {
        if (line.startsWith(':')) out.push({ type: 'comment', text: line.slice(1).trim() });
        else if (line.startsWith('data:')) data.push(line.slice(5).replace(/^ /, ''));
      }
      if (data.length > 0) out.push({ type: 'data', data: data.join('\n') });
    }
    return out;
  }
}
