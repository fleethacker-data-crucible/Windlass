// The shapes the Windlass server sends (docs/API.md, docs/UI.md). A value that was never measured is null, never an estimate.
export interface Figure {
  value: number | null;
  predicted: number | null;
  observed: number | null;
  measured: boolean;
  source: 'real-runs' | 'probe' | 'eval' | 'none' | string;
  samples: number | null;
}

export interface WindlassFacts {
  schema: string;
  architecture: string | null;
  quant: string | null;
  layers: number | null;
  fileBytes: number | null;
  contextLength: number | null;
  modelContextLength: number | null;
  guessAhead: boolean | null;
  speed: { secPerToken: Figure; secPerPromptToken: Figure; limit: 'disk' | 'cpu' | null };
  quality: { perplexity: number | null; tokens: number | null; measuredAt: string | null };
  fidelity: { score: number | null; correct: number | null; total: number | null; promptTokens: number | null; version: number | null; measuredAt: string | null };
  queue: { length: number | null; max: number | null };
}

export type ModelState = 'idle' | 'loading' | 'ready' | 'error';

export interface Status {
  schema: 'windlass.ui.status/1';
  version: string;
  state: ModelState;
  loadError: string | null;
  /** id and path are null while no model is loaded. canSwap is false for a stub server. */
  model: { id: string | null; path: string | null; contextLength: number | null };
  canSwap: boolean;
  busy: boolean;
  queued: number;
  queueMax: number;
  address: string;
  apiKeyRequired: boolean;
  batch: number;
  stub: boolean;
  startedAt: string;
  uptimeSeconds: number;
  autoStop: { enabled: boolean; ownsLife: boolean; armed: boolean };
  windlass: WindlassFacts;
}

export interface PlaybookDoc {
  schema: string;
  created: string | null;
  model: string;
  machine: { cpu: string; logicalCores: number; physicalCores: number; totalRamGB: number; freeRamGB: number; onBattery: boolean; hasBattery: boolean; gpus: string[] };
  measured: { readGBps: number; layerSecPhysical: number; layerSecLogical: number; layerSecBatch: number };
  observed: { runs: number; promptTokens: number; promptSeconds: number; tokens: number; seconds: number; guessed: number; accepted: number };
  quality: { perplexity: number; tokens: number; promptSecPerToken: number; measured: string | null };
  fidelity: { correct: number; total: number; promptTokens: number; score: number | null; version: number; measured: string | null };
  settings: { cacheGB: number; prefetch: boolean; contextLength: number; threads: number; speculate: boolean; predictedSecPerToken: number; limit: string | null };
  rules: string[];
}

export interface LogLine {
  seq: number;
  text: string;
}

export interface ChatMessage {
  role: 'system' | 'user' | 'assistant';
  content: string;
}

export interface ModelEntry {
  name: string;
  path: string;
  type: 'dir' | 'gguf';
  bytes: number | null;
  /** Recent models only: false when the file is gone. */
  exists?: boolean;
}

export interface ModelListing {
  schema: string;
  /** null at the top: the drives and the recent models. */
  dir: string | null;
  parent: string | null;
  entries: ModelEntry[];
  truncated: boolean;
  recent: ModelEntry[];
}
