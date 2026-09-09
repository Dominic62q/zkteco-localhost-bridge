import { useEffect, useRef, useState } from "react";
import { api, type EnrolledUser, type VerifySession } from "../api/client";

const TERMINAL = new Set(["completed", "failed", "cancelled", "timed_out"]);

function human(code: string | null | undefined): string {
  switch (code) {
    case "BRIDGE_BUSY": return "The reader is busy with another session.";
    case "DEVICE_NOT_FOUND": return "No reader detected.";
    case "USER_NOT_FOUND": return "That person no longer exists. Refreshing list…";
    case "NO_TEMPLATE": return "No fingerprint stored for this person.";
    case "CAPTURE_TIMEOUT": return "Timed out waiting. Try again when ready.";
    case "VALIDATION_ERROR": return "Pick a person first.";
    default: return code ?? "";
  }
}

export default function VerifyCard({ users, busy, connected, onUsers }: {
  users: EnrolledUser[];
  busy: boolean;
  connected: boolean;
  onUsers: () => void;
}) {
  const [userId, setUserId] = useState("");
  const [session, setSession] = useState<VerifySession | null>(null);
  const [error, setError] = useState<string | null>(null);
  const timer = useRef<number | null>(null);

  useEffect(() => {
    if (!userId && users.length > 0) setUserId(users[0].id);
    if (userId && !users.some((u) => u.id === userId)) {
      setUserId(users.length > 0 ? users[0].id : "");
    }
  }, [users, userId]);

  useEffect(() => () => {
    if (timer.current !== null) window.clearInterval(timer.current);
  }, []);

  const stopPoll = () => {
    if (timer.current !== null) window.clearInterval(timer.current);
    timer.current = null;
  };

  const poll = (id: string) => {
    stopPoll();
    timer.current = window.setInterval(async () => {
      try {
        const s = await api.verifyGet(id);
        setSession(s);
        if (TERMINAL.has(s.status)) stopPoll();
      } catch {
        setError("Lost contact with the bridge.");
        stopPoll();
      }
    }, 1000);
  };

  const start = async () => {
    setError(null);
    setSession(null);
    if (!userId) {
      setError(human("VALIDATION_ERROR"));
      return;
    }
    const { http, data } = await api.verifyStart(userId);
    if (http !== 200) {
      setError(human((data as unknown as { errorCode: string }).errorCode));
      if ((data as unknown as { errorCode: string }).errorCode === "USER_NOT_FOUND") onUsers();
      return;
    }
    setSession(data);
    poll(data.sessionId);
  };

  const cancel = async () => {
    if (!session) return;
    stopPoll();
    const { data } = await api.verifyCancel(session.sessionId);
    setSession(data);
  };

  const active = session && !TERMINAL.has(session.status);
  const name = users.find((u) => u.id === (session?.userId ?? userId))?.fullName ?? "Selected person";

  return (
    <section style={{ border: "1px solid #ccc", borderRadius: 8, padding: "1rem", marginTop: "1rem" }}>
      <h2>Verification</h2>
      {!connected && <p role="alert">Reader disconnected — plug it in before verifying.</p>}
      <div style={{ display: "flex", gap: "0.5rem" }}>
        <label>
          Person{" "}
          <select value={userId} onChange={(e) => setUserId(e.target.value)} disabled={!!active}>
            {users.map((u) => (
              <option key={u.id} value={u.id}>{u.fullName}{u.externalId ? ` (${u.externalId})` : ""}</option>
            ))}
          </select>
        </label>
        <button onClick={start} disabled={!!active || busy || !connected || users.length === 0}>
          Verify
        </button>
        {active && <button onClick={cancel}>Cancel</button>}
      </div>
      {busy && !active && <p>Reader is busy with another session…</p>}
      {session && (
        <div style={{ marginTop: "0.5rem" }}>
          <p>{name}: <strong>{session.message}</strong></p>
          {session.status === "completed" && session.matched === true && (
            <p role="status" style={{ fontSize: "1.2rem" }}>Match — same finger.</p>
          )}
          {session.status === "completed" && session.matched === false && (
            <p role="status" style={{ fontSize: "1.2rem" }}>No match — different finger.</p>
          )}
          {session.status === "completed" && session.score !== null && (
            <p style={{ fontSize: "0.8rem", color: "#555" }}>Score {session.score} (diagnostic info only)</p>
          )}
          {!TERMINAL.has(session.status) && <p>{session.secondsLeft}s left</p>}
          {session.errorCode && <p role="alert">{human(session.errorCode)}</p>}
        </div>
      )}
      {error && <p role="alert">{error}</p>}
    </section>
  );
}
