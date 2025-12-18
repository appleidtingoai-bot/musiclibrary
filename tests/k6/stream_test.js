import http from 'k6/http';
import { check, sleep } from 'k6';
import { Trend } from 'k6/metrics';

export let options = {
  stages: [
    { duration: '1m', target: 500 },
    { duration: '3m', target: 500 },
    { duration: '5m', target: 2000 },
    { duration: '10m', target: 5000 },
    // ramp up further in separate runs
  ],
  thresholds: {
    http_req_duration: ['p(95)<1000'],
    'presign_success_rate': ['rate>0.99']
  }
};

const BASE = __ENV.BASE || 'https://cdn.tingoradio.ai';
const ENC = encodeURIComponent('music/afrobeats/49ec7f5d301544f18e6d6ca120c251a1_Ruger-Ft-Bnxn-Bae-Bae-(TrendyBeatz.com).mp3');
const ISSUE_COOKIE = `${BASE}/auth/issue-cookie`;
const MEDIA = `${BASE}/media/${ENC}`;

let presignTrend = new Trend('presign_latency');

export default function () {
  // Simulate issuing a cookie (in production you'd use a JWT)
  let r1 = http.post(ISSUE_COOKIE, null, { headers: { Authorization: 'Bearer DUMMY_JWT' } });
  check(r1, { 'issue cookie 200': (r) => r.status === 200 });

  // Attempt to fetch a small range to simulate playback
  let r2 = http.get(MEDIA, { headers: { Range: 'bytes=0-1023' } });
  check(r2, { 'media fetch ok': (r) => r.status === 206 || r.status === 200 });

  presignTrend.add(r2.timings.duration);
  sleep(1);
}
