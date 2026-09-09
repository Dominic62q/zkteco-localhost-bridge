import { useState } from "react";
import { api, type EnrolledUser } from "../api/client";

export default function UsersTable({ users, onChanged }: {
  users: EnrolledUser[];
  onChanged: () => void;
}) {
  const [deleting, setDeleting] = useState<string | null>(null);

  const remove = async (u: EnrolledUser) => {
    if (!window.confirm(`Delete ${u.fullName} and their fingerprint template? This cannot be undone.`)) return;
    setDeleting(u.id);
    try {
      const res = await api.deleteUser(u.id);
      if (!res.ok) throw new Error(`HTTP_${res.status}`);
      onChanged();
    } finally {
      setDeleting(null);
    }
  };

  if (users.length === 0) return <p>No enrolled users yet.</p>;

  return (
    <table style={{ borderCollapse: "collapse", width: "100%" }}>
      <thead>
        <tr>
          <th style={{ textAlign: "left", borderBottom: "1px solid #ccc" }}>Name</th>
          <th style={{ textAlign: "left", borderBottom: "1px solid #ccc" }}>External ID</th>
          <th style={{ textAlign: "left", borderBottom: "1px solid #ccc" }}>Enrolled</th>
          <th style={{ borderBottom: "1px solid #ccc" }}>Actions</th>
        </tr>
      </thead>
      <tbody>
        {users.map((u) => (
          <tr key={u.id}>
            <td>{u.fullName}</td>
            <td>{u.externalId ?? "—"}</td>
            <td>{new Date(u.createdAt).toLocaleString()}</td>
            <td>
              <button onClick={() => remove(u)} disabled={deleting === u.id}>
                {deleting === u.id ? "Deleting…" : "Delete"}
              </button>
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}
