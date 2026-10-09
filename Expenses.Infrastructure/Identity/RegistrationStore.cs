using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Expenses.Application.Identity;
using Expenses.Domain.Entity;
using Expenses.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Expenses.Infrastructure.Identity;

public sealed class RegistrationStore(ExpensesDbContext db) : IRegistrationStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<RegistrationReservation> ReserveAsync(string email, Guid key, Guid correlationId,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var fingerprint = SHA256.HashData(Encoding.UTF8.GetBytes(email));
        var idempotencyId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO identity.idempotency_records
              (id, scope_key, operation, idempotency_key, request_hash, status, expires_at)
            VALUES ({idempotencyId}, 'anonymous-registration', 'create_user', {key.ToString("D")},
                    {fingerprint}, 'in_progress', {now.AddHours(24)})
            ON CONFLICT (scope_key, operation, idempotency_key) DO NOTHING
            """, cancellationToken);
        if (inserted == 0)
        {
            var existing = await db.Database.SqlQuery<IdempotencySnapshot>($"""
                SELECT id AS "Id", request_hash AS "Hash", status AS "Status", expires_at AS "ExpiresAt",
                       response_payload::text AS "Payload"
                FROM identity.idempotency_records
                WHERE scope_key = 'anonymous-registration' AND operation = 'create_user'
                  AND idempotency_key = {key.ToString("D")}
                """).SingleAsync(cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(existing.Hash, fingerprint))
                throw new RegistrationException(RegistrationFailure.KeyReused);
            var prior = await db.Registrations.AsNoTracking().SingleAsync(x => x.IdempotencyRecordId == existing.Id, cancellationToken);
            if (prior.Stage == "reconciliation_required" ||
                existing.Status == "in_progress" && prior.CreatedAt.AddSeconds(5) <= now)
                throw new RegistrationException(RegistrationFailure.ReconciliationRequired);
            if (existing.Status == "in_progress") throw new RegistrationException(RegistrationFailure.InProgress);
            if (existing.ExpiresAt <= now) throw new RegistrationException(RegistrationFailure.KeyExpired);
            if (existing.Status == "failed")
                throw new RegistrationException(JsonSerializer.Deserialize<StoredFailure>(existing.Payload!, Json)!.Failure);
            var user = JsonSerializer.Deserialize<RegisteredUser>(existing.Payload!, Json)
                ?? throw new RegistrationException(RegistrationFailure.Unexpected);
            return new RegistrationReservation(prior.Id, new RegistrationResult(user, true));
        }

        if (await db.Users.AnyAsync(x => x.NormalizedEmail == email, cancellationToken))
            throw new RegistrationException(RegistrationFailure.UserExists);
        var reserved = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO identity.registration_operations
                (id, created_at, updated_at, normalized_email, idempotency_record_id, correlation_id, stage)
            VALUES ({operationId}, {now}, {now}, {email}, {idempotencyId}, {correlationId}, 'reserved')
            ON CONFLICT (normalized_email)
              WHERE stage IN ('reserved','principal_created','password_ready','reconciliation_required') DO NOTHING
            """, cancellationToken);
        if (reserved == 0) throw new RegistrationException(RegistrationFailure.UserExists);
        await transaction.CommitAsync(cancellationToken);
        return new RegistrationReservation(operationId);
    }

    public async Task RecordPrincipalAsync(Guid operationId, ExternalIdentity principal, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var count = await db.Registrations.Where(x => x.Id == operationId && x.Stage == "reserved")
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.Stage, "principal_created")
                .SetProperty(x => x.CognitoSub, principal.Subject).SetProperty(x => x.CognitoUsername, principal.Username)
                .SetProperty(x => x.UpdatedAt, now).SetProperty(x => x.Version, x => x.Version + 1), cancellationToken);
        RequireTransition(count);
    }

    public async Task RecordPasswordAsync(Guid operationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var count = await db.Registrations.Where(x => x.Id == operationId && x.Stage == "principal_created")
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.Stage, "password_ready")
                .SetProperty(x => x.UpdatedAt, now).SetProperty(x => x.Version, x => x.Version + 1), cancellationToken);
        RequireTransition(count);
    }

    public async Task<RegisteredUser> CompleteAsync(Guid operationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var operation = await LockAsync(operationId, cancellationToken);
            if (operation.Stage != "password_ready") throw new RegistrationException(RegistrationFailure.ReconciliationRequired);
            var user = new User
            {
                Id = Guid.NewGuid(), Email = operation.NormalizedEmail, CognitoSub = operation.CognitoSub!, CreatedAt = now
            };
            db.Users.Add(user);
            await db.SaveChangesAsync(cancellationToken);
            var result = new RegisteredUser(user.Id, user.Email, user.CreatedAt.UtcDateTime);
            var payload = JsonSerializer.Serialize(result, Json);
            operation.Stage = "completed";
            operation.UserId = user.Id;
            operation.UpdatedAt = now;
            operation.Version++;
            await db.SaveChangesAsync(cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE identity.idempotency_records SET status = 'succeeded', completed_at = {now},
                    response_status = 201, response_payload = {payload}::jsonb, actor_user_id = {user.Id}
                WHERE id = {operation.IdempotencyRecordId} AND status = 'in_progress'
                """, cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO identity.audit_logs (actor_user_id, action, entity_type, entity_id, correlation_id)
                VALUES ({user.Id}, 'user_created', 'user', {user.Id}, {operation.CorrelationId})
                """, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        finally { db.ChangeTracker.Clear(); }
    }

    public async Task<RegisteredUser?> ReadCompletionAsync(Guid operationId, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        return await (from operation in db.Registrations.AsNoTracking()
                      join user in db.Users.AsNoTracking() on operation.UserId equals user.Id
                      where operation.Id == operationId && operation.Stage == "completed"
                      select new RegisteredUser(user.Id, user.Email, user.CreatedAt.UtcDateTime))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task RecordFailureAsync(Guid operationId, RegistrationFailure failure, bool uncertain,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var operation = await LockAsync(operationId, cancellationToken);
            if (operation.Stage == "completed") return;
            operation.Stage = uncertain ? "reconciliation_required" : "failed";
            operation.UpdatedAt = now;
            operation.Version++;
            await db.SaveChangesAsync(cancellationToken);
            if (uncertain)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO identity.identity_reconciliations(operation_key, cognito_sub, reason_code, correlation_id)
                    VALUES ({operationId.ToString("D")}, {operation.CognitoSub}, 'registration_outcome_unknown', {operation.CorrelationId})
                    ON CONFLICT (operation_key) DO NOTHING
                    """, cancellationToken);
            }
            else
            {
                var payload = JsonSerializer.Serialize(new StoredFailure(failure), Json);
                var status = failure switch
                {
                    RegistrationFailure.UserExists => 409,
                    RegistrationFailure.PasswordPolicy => 422,
                    RegistrationFailure.Unavailable => 503,
                    _ => 500
                };
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE identity.idempotency_records SET status = 'failed', completed_at = {now},
                        response_status = {status}, response_payload = {payload}::jsonb
                    WHERE id = {operation.IdempotencyRecordId} AND status = 'in_progress'
                    """, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        finally { db.ChangeTracker.Clear(); }
    }

    private async Task<RegistrationOperation> LockAsync(Guid id, CancellationToken cancellationToken) =>
        (await db.Registrations.FromSqlInterpolated($"SELECT * FROM identity.registration_operations WHERE id = {id} FOR UPDATE")
            .ToListAsync(cancellationToken)).Single();

    private static void RequireTransition(int count)
    {
        if (count != 1) throw new RegistrationException(RegistrationFailure.ReconciliationRequired);
    }

    private sealed record StoredFailure(RegistrationFailure Failure);

    public sealed class IdempotencySnapshot
    {
        public Guid Id { get; set; }
        public byte[] Hash { get; set; } = [];
        public string Status { get; set; } = "";
        public DateTimeOffset ExpiresAt { get; set; }
        public string? Payload { get; set; }
    }
}
