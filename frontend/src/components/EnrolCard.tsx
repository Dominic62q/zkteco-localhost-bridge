import { useEffect, useRef, useState } from "react";
import { api, type EnrolSession } from "../api/client";

const TERMINAL = new Set(["completed", "failed", "cancelled", "timed_out"]);

function human(code: string | null | undefined): string {
  switch (code) {
    case "BRIDGE_BUSY": return "The reader is busy with another session. Wait for it to finish.";
    case "DEVICE_NOT_FOUND": return "No reader detected. Plug it in and try again.";
    case "DUPLICATE_FINGERPRINT": return "This finger is already registered — try a different finger.";
    case "SAME_FINGER_REQUIRED": return "Please use the same finger for all 3 scans.";
    case "CAPTURE_TIMEOUT": return "Timed out waiting. Press Enrol again when ready.";
    case "CAPTURE_FAILED": return "The scans didn't merge. Start again, pressing firmly each time.";
    case "DATABASE_ERROR": return "Couldn't save. Check the bridge window for details.";
    case "VALIDATION_ERROR": return "Please enter a full name.";
    case "SESSION_NOT_FOUND": return "Session expired. Start again.";
    default: return code ?? "";
  }
}

export default function EnrolCard({ busy, connected, onEnrolled }: { busy: boolean; connected: boolean; onEnrolled: () => void }) {
  const [name, setName] = useState("");
  const [extId, setExtId] = useState("");
  const [session, setSession] = useState<EnrolSession | null>(null);
  const [error, setError] = useState<string | null>(null);
  const timer = useRef<number | null>(null);

  const stopPoll = () => {
    if (timer.current !== null) window.clearInterval(timer.current);
    timer.current = null;
  };

  useEffect(() => stopPoll, []);

  const poll = (id: string) => {
    stopPoll();
    timer.current = window.setInterval(async () => {
      try {
        const s = await api.enrolGet(id);
        setSession(s);
        if (TERMINAL.has(s.status)) {
          stopPoll();
          if (s.status === "completed") onEnrolled();
        }
      } catch {
        setError("Lost contact with the bridge.");
        stopPoll();
      }
    }, 1000);
  };

  const start = async () => {
    setError(null);
    setSession(null);
    const { http, data } = await api.enrolStart(name, extId);
    if (http !== 200) {
      setError(human((data as unknown as { errorCode: string }).errorCode));
      return;
    }
    setSession(data);
    poll(data.sessionId);
  };

  const cancel = async () => {
    if (!session) return;
    stopPoll();
    const { data } = await api.enrolCancel(session.sessionId);
    setSession(data);
  };

  const active = session && !TERMINAL.has(session.status);
  const done = session?.status === "completed";

  return (
    <section style={{ border: "1px solid #ccc", borderRadius: 8, padding: "1rem", marginTop: "1rem" }}>
      <h2>Enrolment</h2>
      {!connected && <p role="alert">Reader disconnected — plug it in before enrolling.</p>}
      <div style={{ display: "flex", gap: "0.5rem", flexWrap: "wrap" }}>
        <label>
          Full name (required){" "}
          <input value={name} onChange={(e) => setName(e.target.value)} disabled={!!active} placeholder="Test User" />
        </label>
        <label>
          External ID (optional){" "}
          <input value={extId} onChange={(e) => setExtId(e.target.value)} disabled={!!active} placeholder="TEST-001" />
        </label>
      </div>
      <div style={{ marginTop: "0.5rem", display: "flex", gap: "0.5rem" }}>
        <button onClick={start} disabled={!!active || busy || !connected || !name.trim()}>
          Enrol fingerprint
        </button>
        {active && <button onClick={cancel}>Cancel</button>}
      </div>
      {busy && !active && <p>Reader is busy with another session…</p>}
      {session && (
        <div style={{ marginTop: "0.5rem" }}>
          <p><strong>{session.message}</strong></p>
          <p>{session.completedScans} of {session.requiredScans} scans
            {!TERMINAL.has(session.status) && <> · {session.secondsLeft}s left</>}</p>
          {session.errorCode && <p role="alert">{human(session.errorCode)}</p>}
          {done && <p>Enrolled as {name || session.userId}. Try a different finger next to confirm rejection (M4).</p>}
        </div>
      )}
      {error && <p role="alert">{error}</p>}
    </section>
  );
}
