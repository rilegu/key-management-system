using System;
using System.Globalization;
using KeyManagement.Contracts;

namespace KeyManagement.Desktop.ViewModels;

/// <summary>
/// One line in the activity feed or the activity trail.
/// </summary>
public sealed class ActivityRowViewModel
{
    /// <summary>Creates a row from an audit record.</summary>
    /// <param name="record">The record as the server reported it.</param>
    public ActivityRowViewModel(AuditEventSummary record)
    {
        ArgumentNullException.ThrowIfNull(record);

        Id = record.Id;
        What = Vocabulary.ActivityWord(record.Type);
        Detail = record.Summary;
        OccurredAt = record.OccurredAt;
        CorrelationId = record.CorrelationId;
        IsRefusal = Vocabulary.IsRefusal(record.Type);
    }

    /// <summary>Identifies the record.</summary>
    public Guid Id { get; }

    /// <summary>What happened, in the words used on screen.</summary>
    public string What { get; }

    /// <summary>The one-line summary the server wrote.</summary>
    public string Detail { get; }

    /// <summary>When it happened.</summary>
    public DateTimeOffset OccurredAt { get; }

    /// <summary>Gathers every record produced by one command.</summary>
    public Guid CorrelationId { get; }

    /// <summary>Whether this was a refusal or a fault, which the list marks.</summary>
    public bool IsRefusal { get; }

    /// <summary>Time of day, local, for the feed.</summary>
    public string Time =>
        OccurredAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);

    /// <summary>Date and time, local, for the trail.</summary>
    public string When =>
        OccurredAt.ToLocalTime().ToString("dd MMM yyyy  HH:mm:ss", CultureInfo.CurrentCulture);

    /// <summary>Short form of the correlation id, enough to match records by eye.</summary>
    public string Correlation => CorrelationId.ToString("N", CultureInfo.InvariantCulture)[..8];
}
