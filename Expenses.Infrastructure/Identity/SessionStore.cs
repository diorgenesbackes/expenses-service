using Expenses.Application.Identity;
using Expenses.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Expenses.Infrastructure.Identity;

public sealed class SessionStore(ExpensesDbContext db) : ISessionStore
{
    public async Task BeginLoginAsync(byte[] emailHash, Guid owner, DateTimeOffset now, CancellationToken token)
    {
        await using var tx = await db.Database.BeginTransactionAsync(token);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO identity.login_guards(email_hash) VALUES ({emailHash}) ON CONFLICT DO NOTHING", token);
        var guard = await LockGuardAsync(emailHash, token);
        var blocked = await db.Database.SqlQuery<DateTimeOffset>($"""
            SELECT blocked_until AS "Value" FROM identity.login_blocks
            WHERE key_kind = 'email' AND key_hash = {emailHash} AND blocked_until > {now}
            """).ToListAsync(token);
        if (blocked.Count > 0) throw new SessionException(SessionFailure.Locked, retryAfter: Retry(blocked[0], now));
        if (guard.LeaseUntil > now) throw new SessionException(SessionFailure.Busy, retryAfter: Retry(guard.LeaseUntil.Value, now));
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE identity.login_guards SET owner = {owner}, lease_until = {now.AddSeconds(10)} WHERE email_hash = {emailHash}
            """, token);
        await tx.CommitAsync(token);
    }

    public async Task<SessionUser?> FinishLoginAsync(byte[] emailHash, Guid owner, string? subject, Guid sessionId,
        byte[] accessHash, byte[] refreshHash, DateTimeOffset accessExpiresAt, DateTimeOffset now,
        Guid correlationId, CancellationToken token)
    {
        await using var tx = await db.Database.BeginTransactionAsync(token);
        var guard = await LockGuardAsync(emailHash, token);
        if (guard.Owner != owner || guard.LeaseUntil <= now) throw new SessionException(SessionFailure.Busy, retryAfter: 1);
        SessionUser? user = null;
        var remaining = 10;
        if (subject is null)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO identity.login_attempts(email_hash, correlation_id, created_at) VALUES ({emailHash}, {correlationId}, {now})
                """, token);
            var count = await db.Database.SqlQuery<long>($"""
                SELECT count(*) AS "Value" FROM identity.login_attempts WHERE email_hash = {emailHash} AND created_at > {now.AddMinutes(-15)}
                """).SingleAsync(token);
            remaining = Math.Max(0, 10 - (int)count);
            if (remaining == 0)
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO identity.login_blocks(key_kind,key_hash,created_at,updated_at,blocked_until)
                    VALUES ('email', {emailHash}, {now}, {now}, {now.AddMinutes(15)})
                    ON CONFLICT (key_kind,key_hash) DO UPDATE SET created_at = EXCLUDED.created_at,
                        updated_at = EXCLUDED.updated_at, blocked_until = EXCLUDED.blocked_until
                    """, token);
        }
        else
        {
            user = await db.Users.AsNoTracking().Where(x => x.CognitoSub == subject)
                .Select(x => new SessionUser(x.Id, x.Email)).SingleOrDefaultAsync(token);
            if (user is not null)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM identity.login_attempts WHERE email_hash = {emailHash}", token);
                await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM identity.login_blocks WHERE key_kind = 'email' AND key_hash = {emailHash}", token);
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO identity.sessions(id,user_id,created_at,updated_at,expires_at,access_expires_at,access_hash,refresh_hash,state)
                    VALUES ({sessionId}, {user.Id}, {now}, {now}, {now.AddDays(7)}, {accessExpiresAt}, {accessHash}, {refreshHash}, 'active')
                    """, token);
                await AuditAsync(user.Id, sessionId, "login_success", correlationId, token);
            }
            else
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO identity.identity_reconciliations(operation_key,cognito_sub,reason_code,correlation_id)
                    VALUES ({sessionId.ToString()}, {subject}, 'login_profile_missing', {correlationId})
                    ON CONFLICT DO NOTHING
                    """, token);
        }
        await ReleaseLoginAsync(emailHash, owner, token);
        await tx.CommitAsync(token);
        // Throw only after persisting the failed attempt and releasing the lease.
        if (subject is null)
            throw remaining == 0 ? new SessionException(SessionFailure.Locked, retryAfter: 900)
                : new SessionException(SessionFailure.InvalidCredentials, remaining <= 3 ? remaining : null);
        return user;
    }

    public Task ReleaseLoginAsync(byte[] emailHash, Guid owner, CancellationToken token) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE identity.login_guards SET owner = NULL, lease_until = NULL WHERE email_hash = {emailHash} AND owner = {owner}
            """, token);

    public async Task<StoredSession?> ReadAsync(Guid id, CancellationToken token)
    {
        var row = await db.Database.SqlQuery<SessionRow>($"""
            SELECT s.id AS "Id", s.user_id AS "UserId", u.email AS "Email", u.cognito_sub AS "Subject",
                   s.access_hash AS "AccessHash", s.refresh_hash AS "RefreshHash", s.access_expires_at AS "AccessExpiresAt",
                   s.expires_at AS "ExpiresAt", s.state AS "State", s.updated_at AS "UpdatedAt"
            FROM identity.sessions s JOIN identity.users u ON u.id = s.user_id WHERE s.id = {id}
            """).SingleOrDefaultAsync(token);
        return row is null ? null : new StoredSession(row.Id, new SessionUser(row.UserId, row.Email), row.Subject,
            row.AccessHash, row.RefreshHash, row.AccessExpiresAt, row.ExpiresAt, row.State, row.UpdatedAt);
    }

    public async Task<bool> BeginRefreshAsync(Guid id, byte[] refreshHash, Guid owner, DateTimeOffset now, CancellationToken token) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE identity.sessions SET state = 'refreshing', refresh_owner = {owner}, updated_at = {now}
            WHERE id = {id} AND state = 'active' AND refresh_hash = {refreshHash} AND expires_at > {now}
            """, token) == 1;

    public async Task<bool> CompleteRefreshAsync(Guid id, Guid owner, byte[] accessHash, byte[] refreshHash,
        DateTimeOffset accessExpiresAt, DateTimeOffset now, CancellationToken token) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE identity.sessions SET state = 'active', refresh_owner = NULL, access_hash = {accessHash},
                refresh_hash = {refreshHash}, access_expires_at = {accessExpiresAt}, updated_at = {now}
            WHERE id = {id} AND state = 'refreshing' AND refresh_owner = {owner} AND expires_at > {now}
            """, token) == 1;

    public async Task RevokeAsync(Guid id, DateTimeOffset now, Guid correlationId, CancellationToken token)
    {
        await using var tx = await db.Database.BeginTransactionAsync(token);
        var ids = await db.Database.SqlQuery<Guid>($"""
            UPDATE identity.sessions SET state = 'revoked', revoked_at = {now}, updated_at = {now}, refresh_owner = NULL
            WHERE id = {id} AND state <> 'revoked' RETURNING user_id AS "Value"
            """).ToListAsync(token);
        if (ids.Count > 0) await AuditAsync(ids[0], id, "session_revoked", correlationId, token);
        await tx.CommitAsync(token);
    }

    private Task AuditAsync(Guid userId, Guid sessionId, string action, Guid correlationId, CancellationToken token) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO identity.audit_logs(actor_user_id,action,entity_type,entity_id,correlation_id)
            VALUES ({userId}, {action}, 'session', {sessionId}, {correlationId})
            """, token);
    private async Task<GuardRow> LockGuardAsync(byte[] emailHash, CancellationToken token) =>
        (await db.Database.SqlQuery<GuardRow>($"""
            SELECT owner AS "Owner", lease_until AS "LeaseUntil" FROM identity.login_guards WHERE email_hash = {emailHash} FOR UPDATE
            """).ToListAsync(token)).Single();
    private static int Retry(DateTimeOffset until, DateTimeOffset now) => Math.Max(1, (int)Math.Ceiling((until - now).TotalSeconds));
    public sealed class GuardRow { public Guid? Owner { get; set; } public DateTimeOffset? LeaseUntil { get; set; } }
    public sealed class SessionRow
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public string Email { get; set; } = "";
        public string Subject { get; set; } = "";
        public byte[] AccessHash { get; set; } = [];
        public byte[] RefreshHash { get; set; } = [];
        public DateTimeOffset AccessExpiresAt { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public string State { get; set; } = "";
        public DateTimeOffset UpdatedAt { get; set; }
    }
}
