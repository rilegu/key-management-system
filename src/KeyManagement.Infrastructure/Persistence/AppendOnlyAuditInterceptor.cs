using KeyManagement.Domain.Auditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace KeyManagement.Infrastructure.Persistence;

/// <summary>
/// Refuses any attempt to amend or remove an audit record.
/// </summary>
/// <remarks>
/// <para>
/// The trail is append-only. <see cref="AuditEvent"/> exposes no way to change itself, so this
/// catches the other routes in: a raw <c>Update</c>, a <c>Remove</c>, or a future property
/// with a setter someone adds without thinking about it.
/// </para>
/// <para>
/// This is an application-layer control, not a storage guarantee. Anyone holding the database
/// file can still rewrite it; <c>docs/threat-model.md</c> records that as a known limitation
/// rather than claiming otherwise.
/// </para>
/// </remarks>
public sealed class AppendOnlyAuditInterceptor : SaveChangesInterceptor
{
    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Guard(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Guard(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static void Guard(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        foreach (var entry in context.ChangeTracker.Entries<AuditEvent>())
        {
            if (entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new InvalidOperationException(
                    $"Audit records are append-only; attempted to {entry.State} audit event {entry.Entity.Id}.");
            }
        }
    }
}
