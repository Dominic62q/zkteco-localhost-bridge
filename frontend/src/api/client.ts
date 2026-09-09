export interface Health {
  bridge: string;
  sdk: string;
  sdkRet: number;
  database: string;
  deviceDetected: boolean;
  deviceName: string | null;
  deviceCount: number;
}

export interface ReaderStatus {
  connected: boolean;
  model: string | null;
  deviceCount: number;
  busy: boolean;
  lastError: string | null;
}

export interface EnrolSession {
  sessionId: string;
  operation: "enrol";
  status: string;
  requiredScans: number;
  completedScans: number;
  message: string | null;
  errorCode: string | null;
  userId: string | null;
  expiresAt: string;
  secondsLeft: number;
}

const base = (import.meta.env.VITE_BRIDGE_URL as string | undefined) ?? "http://127.0.0.1:5050";
const token = (import.meta.env.VITE_BRIDGE_TOKEN as string | undefined) ?? "";
const headers: Record<string, string> = { "Content-Type": "application/json" };
if (token) headers["X-Bridge-Token"] = token;

async function post<T>(path: string, body?: unknown): Promise<{ http: number; data: T }> {
  const res = await fetch(`${base}${path}`, {
    method: "POST",
    headers,
    body: body ? JSON.stringify(body) : undefined,
  });
  return { http: res.status, data: (await res.json()) as T };
}


export interface EnrolledUser {
  id: string;
  fullName: string;
  externalId: string | null;
  createdAt: string;
}

export interface VerifySession {
  sessionId: string;
  operation: "verify";
  status: string;
  matched: boolean | null;
  score: number | null;
  userId: string;
  message: string | null;
  errorCode: string | null;
  expiresAt: string;
  secondsLeft: number;
}

async function get<T>(path: string): Promise<T> {
  const res = await fetch(`${base}${path}`, { headers });
  if (!res.ok) throw new Error(`BRIDGE_HTTP_${res.status}`);
  return (await res.json()) as T;
}

export const api = {
  health: () => get<Health>("/api/health"),
  status: () => get<ReaderStatus>("/api/fingerprint/status"),
  enrolStart: (fullName: string, externalId?: string) =>
    post<EnrolSession>("/api/fingerprint/enrol/start", { fullName, externalId: externalId || undefined }),
  enrolGet: (id: string) => get<EnrolSession>(`/api/fingerprint/enrol/${id}`),
  enrolCancel: (id: string) => post<EnrolSession>(`/api/fingerprint/enrol/${id}/cancel`),
  users: () => get<EnrolledUser[]>("/api/users"),
  deleteUser: (id: string) => fetch(`${base}/api/users/${id}`, { method: "DELETE", headers }),
  verifyStart: (userId: string) =>
    post<VerifySession>("/api/fingerprint/verify/start", { userId }),
  verifyGet: (id: string) => get<VerifySession>(`/api/fingerprint/verify/${id}`),
  verifyCancel: (id: string) => post<VerifySession>(`/api/fingerprint/verify/${id}/cancel`),
};

export const BRIDGE_URL = base;
