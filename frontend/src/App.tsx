import { useCallback, useEffect, useState } from "react";
import { api, BRIDGE_URL, type EnrolledUser, type Health, type ReaderStatus } from "./api/client";
import EnrolCard from "./components/EnrolCard";
import UsersTable from "./components/UsersTable";
import VerifyCard from "./components/VerifyCard";
const POLL_MS = 2500;

function messageFor(code: string | null | undefined): string {
  switch (code) {
    case "SDK_NOT_FOUND":
      return "Fingerprint SDK not found. Check ZK_SDK_PATH and reinstall the driver.";
    case null:
    case undefined:
      return "";
    default:
      return code;
  }
}

export default function App() {
  const [health, setHealth] = useState<Health | null>(null);
  const [status, setStatus] = useState<ReaderStatus | null>(null);
  const [users, setUsers] = useState<EnrolledUser[]>([]);
  const [bridgeDown, setBridgeDown] = useState(false);

  const refreshUsers = useCallback(async () => {
    try {
      setUsers(await api.users());
    } catch {
      /* bridge down: status card already reports it */
    }
  }, []);
  useEffect(() => {
    let alive = true;
    const tick = async () => {
      try {
        const [h, s] = await Promise.all([api.health(), api.status()]);
        if (!alive) return;
        setHealth(h);
        setStatus(s);
        setBridgeDown(false);
      } catch {
        if (alive) setBridgeDown(true);
      }
    };
    void tick();
    void refreshUsers();
    const t = setInterval(tick, POLL_MS);
    const u = setInterval(refreshUsers, 5000);
    return () => {
      alive = false;
      clearInterval(t);
      clearInterval(u);
    };
  }, [refreshUsers]);

  return (
    <main style={{ fontFamily: "system-ui, sans-serif", maxWidth: 640, margin: "2rem auto", padding: "0 1rem" }}>
      <header>
        <h1>ZKTeco Fingerprint Test</h1>
        <p>
          Bridge: <code>{BRIDGE_URL}</code> —{" "}
          {bridgeDown ? "not reachable (start the bridge: scripts/start-dev.ps1)" : `sdk=${health?.sdk} db=${health?.database}`}
        </p>
        <p style={{ fontSize: "0.85rem" }}>
          Notice: this test app collects fingerprint templates locally for hardware validation. Use the enrolled-users
          table (M4) delete action to remove your biometric data.
        </p>
      </header>

      <section style={{ border: "1px solid #ccc", borderRadius: 8, padding: "1rem", marginTop: "1rem" }}>
        <h2>Device status</h2>
        {bridgeDown ? (
          <p>Bridge not running. Start it with <code>cd bridge; dotnet run</code>.</p>
        ) : status ? (
          <>
            <p>{status.connected ? "Connected" : "Disconnected"}</p>
            <ul>
              <li>Model: {status.model ?? "—"}</li>
              <li>Device count: {status.deviceCount}</li>
              <li>Busy: {status.busy ? "yes" : "no"}</li>
            </ul>
            {status.lastError && <p role="alert">{messageFor(status.lastError)}</p>}
          </>
        ) : (
          <p>Loading…</p>
        )}
        <button onClick={() => window.location.reload()}>Refresh</button>
      </section>

      <EnrolCard busy={!!status?.busy} connected={!!status?.connected} onEnrolled={refreshUsers} />

      <VerifyCard users={users} busy={!!status?.busy} connected={!!status?.connected} onUsers={refreshUsers} />

      <section style={{ border: "1px solid #ccc", borderRadius: 8, padding: "1rem", marginTop: "1rem" }}>
        <h2>Enrolled users</h2>
        <UsersTable users={users} onChanged={refreshUsers} />
      </section>
    </main>
  );
}
