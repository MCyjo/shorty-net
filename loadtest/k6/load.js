// Open-model load test: requests arrive at a fixed rate whether or not the server keeps up (constant-arrival-rate), which is how
// real traffic behaves. A closed model (a fixed number of looping users) slows down when the server slows down and hides it.
//
// SCENARIO=redirect  follow short links (skewed towards a few popular ones)
// SCENARIO=create    create links
// RATE (requests/second), DURATION_S, WARMUP_S, LINKS (how many links the redirect scenario draws from), NAME (result file)
import http from 'k6/http';
import { check } from 'k6';

const BASE = __ENV.BASE_URL || 'http://api:8080';
const SCENARIO = __ENV.SCENARIO || 'redirect';
const RATE = parseInt(__ENV.RATE || '1000', 10);
const DURATION_S = parseInt(__ENV.DURATION_S || '60', 10);
const WARMUP_S = parseInt(__ENV.WARMUP_S || '15', 10);
const LINKS = parseInt(__ENV.LINKS || '5000', 10);
const NAME = __ENV.NAME || `${SCENARIO}-${RATE}`;

const arrival = (startTime, duration, phase) => ({
  executor: 'constant-arrival-rate',
  rate: RATE,
  timeUnit: '1s',
  duration: `${duration}s`,
  startTime: `${startTime}s`,
  preAllocatedVUs: Math.max(50, Math.ceil(RATE / 20)),
  maxVUs: Math.max(500, RATE),
  exec: SCENARIO,
  tags: { phase },
});

export const options = {
  discardResponseBodies: true,
  summaryTrendStats: ['avg', 'min', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
  scenarios: {
    warmup: arrival(0, WARMUP_S, 'warmup'), // fills caches and JIT; not part of the results
    measure: arrival(WARMUP_S, DURATION_S, 'measure'),
  },
  // Always-true thresholds: they make k6 report these per-phase series in the summary. They do not judge the run.
  thresholds: {
    'http_req_duration{phase:measure}': ['max>=0'],
    'http_req_failed{phase:measure}': ['rate>=0'],
    'http_reqs{phase:measure}': ['count>=0'],
    'dropped_iterations{phase:measure}': ['count>=0'],
  },
};

export function setup() {
  if (SCENARIO !== 'redirect') return { codes: [] };
  const codes = [];
  for (let i = 0; i < LINKS; i += 100) {
    const batch = [];
    for (let j = i; j < Math.min(i + 100, LINKS); j++) {
      batch.push(['POST', `${BASE}/api/links`, JSON.stringify({ url: `https://example.com/seed/${j}` }),
        { headers: { 'Content-Type': 'application/json' }, tags: { name: 'seed' }, responseType: 'text' }]); // the global discardResponseBodies would drop the codes
    }
    for (const r of http.batch(batch)) {
      if (r.status !== 201) throw new Error(`seeding failed: ${r.status} ${r.body}`);
      codes.push(JSON.parse(r.body).code);
    }
  }
  return { codes };
}

// u^3 puts about 46% of the requests on the most popular 10% of links: skewed like real traffic, not a true Zipf distribution.
const pick = (codes) => codes[Math.floor(codes.length * Math.pow(Math.random(), 3))];

export function redirect(data) {
  const res = http.get(`${BASE}/${pick(data.codes)}`, { redirects: 0, tags: { name: 'redirect' } });
  check(res, { 'is 302': (r) => r.status === 302 });
}

export function create() {
  const res = http.post(`${BASE}/api/links`,
    JSON.stringify({ url: `https://example.com/load/${__VU}/${__ITER}/${Date.now()}` }),
    { headers: { 'Content-Type': 'application/json' }, tags: { name: 'create' } });
  check(res, { 'is 201': (r) => r.status === 201 });
}

export function handleSummary(data) {
  const m = (name, stat) => (data.metrics[name] ? data.metrics[name].values[stat] : NaN);
  const d = 'http_req_duration{phase:measure}';
  const sent = m('http_reqs{phase:measure}', 'count');
  const result = {
    name: NAME, scenario: SCENARIO, targetRate: RATE, measuredSeconds: DURATION_S,
    requests: sent,
    achievedRate: sent / DURATION_S,
    p50: m(d, 'med'), p90: m(d, 'p(90)'), p95: m(d, 'p(95)'), p99: m(d, 'p(99)'), max: m(d, 'max'),
    errorRate: m('http_req_failed{phase:measure}', 'rate'),
    droppedIterations: m('dropped_iterations{phase:measure}', 'count') || 0,
  };
  if (!(sent > 0)) return { stdout: `
FAILED no requests were made in the measured phase; the run proves nothing
` };
  const f = (x) => (Number.isFinite(x) ? x.toFixed(1) : 'n/a');
  const row = `| ${NAME} | ${RATE} | ${f(result.achievedRate)} | ${f(result.p50)} | ${f(result.p95)} | ${f(result.p99)} | ${f(result.max)} | ${(result.errorRate * 100).toFixed(2)} | ${result.droppedIterations} |`;
  return { [`/loadtest/results/${NAME}.json`]: JSON.stringify(result, null, 2), stdout: `\nRESULT ${row}\n` };
}
