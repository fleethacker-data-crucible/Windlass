// Number formatting. A missing value is always "—" (Fleet UI section 7: no invented numbers).
export const DASH = '—';

export function formatDuration(seconds: number | null | undefined): string {
  if (seconds == null || !Number.isFinite(seconds) || seconds < 0) return DASH;
  if (seconds < 60) return `${seconds < 10 ? seconds.toFixed(1) : Math.round(seconds)} s`;
  const total = Math.round(seconds);
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  if (h > 0) return m > 0 ? `${h} h ${m} min` : `${h} h`;
  return s > 0 ? `${m} min ${s} s` : `${m} min`;
}

/** Decimal gigabytes, as the playbook and the README count them. */
export function formatBytes(n: number | null | undefined): string {
  if (n == null || !Number.isFinite(n) || n < 0) return DASH;
  if (n < 1e6) return `${Math.round(n / 1e3)} KB`;
  if (n < 1e9) return `${(n / 1e6).toFixed(n < 1e8 ? 1 : 0)} MB`;
  return `${(n / 1e9).toFixed(n < 1e10 ? 2 : 1)} GB`;
}

export function formatNumber(n: number | null | undefined, digits = 0): string {
  if (n == null || !Number.isFinite(n)) return DASH;
  return n.toLocaleString('en-US', { minimumFractionDigits: digits, maximumFractionDigits: digits });
}

export function formatSecPerToken(v: number | null | undefined): string {
  if (v == null || !Number.isFinite(v) || v <= 0) return DASH;
  return `${v < 10 ? v.toFixed(2) : v.toFixed(1)} s/token`;
}

export function formatTokensPerSecond(secPerToken: number | null | undefined): string {
  if (secPerToken == null || !Number.isFinite(secPerToken) || secPerToken <= 0) return DASH;
  const v = 1 / secPerToken;
  return `${v < 1 ? v.toFixed(2) : v.toFixed(1)} tokens/s`;
}

export function formatPercent(fraction: number | null | undefined): string {
  if (fraction == null || !Number.isFinite(fraction)) return DASH;
  return `${Math.round(fraction * 100)}%`;
}

export function formatTime(iso: string | null | undefined): string {
  if (!iso) return DASH;
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return DASH;
  return d.toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
}

/** The worst case for a reply: every token takes the measured seconds per token. Null when it was never measured. */
export function worstCaseSeconds(maxTokens: number, secPerToken: number | null | undefined): number | null {
  if (secPerToken == null || !Number.isFinite(secPerToken) || secPerToken <= 0 || !Number.isFinite(maxTokens) || maxTokens < 1) return null;
  return maxTokens * secPerToken;
}
