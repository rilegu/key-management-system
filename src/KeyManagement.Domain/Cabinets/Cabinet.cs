namespace KeyManagement.Domain.Cabinets;

/// <summary>
/// An electronic cabinet holding assets in slots.
/// </summary>
/// <remarks>
/// A cabinet reports and executes; it never decides. Custody is authorized by the server
/// before a cabinet is asked to do anything, so nothing here grants access.
/// </remarks>
public sealed class Cabinet
{
    private readonly List<Slot> _slots = [];

    private Cabinet()
    {
        Name = string.Empty;
        Site = string.Empty;
    }

    /// <summary>Enrols a cabinet that has not yet connected.</summary>
    /// <param name="name">Unique, human-readable name.</param>
    /// <param name="site">Where it physically is.</param>
    public Cabinet(string name, string site)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(site);

        Id = CabinetId.New();
        Name = name;
        Site = site;
        Status = CabinetStatus.NeverConnected;
    }

    /// <summary>Identifies this cabinet.</summary>
    public CabinetId Id { get; private set; }

    /// <summary>Unique across all cabinets.</summary>
    public string Name { get; private set; }

    /// <summary>Where it physically is.</summary>
    public string Site { get; private set; }

    /// <summary>Whether the server currently has a working link to it.</summary>
    public CabinetStatus Status { get; private set; }

    /// <summary>Last contact of any kind, UTC, or <see langword="null"/> if it has never connected.</summary>
    public DateTimeOffset? LastSeenAt { get; private set; }

    /// <summary>Firmware it reported at its last handshake.</summary>
    public string? FirmwareVersion { get; private set; }

    /// <summary>
    /// The highest event sequence number applied from this cabinet.
    /// </summary>
    /// <remarks>
    /// Events at or below this are discarded rather than applied, which is what makes a
    /// duplicated or reordered delivery harmless. It is also what a reconnecting cabinet is
    /// told so it can replay only the gap.
    /// </remarks>
    public long LastAppliedSequence { get; private set; }

    /// <summary>The slots it holds.</summary>
    public IReadOnlyCollection<Slot> Slots => _slots;

    /// <summary>Adds a slot.</summary>
    /// <param name="position">Position label, unique within this cabinet.</param>
    /// <returns>The new slot.</returns>
    public Slot AddSlot(string position)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(position);

        var slot = new Slot(Id, position);
        _slots.Add(slot);
        return slot;
    }

    /// <summary>Records a successful handshake.</summary>
    /// <param name="at">When it connected, UTC.</param>
    /// <param name="firmwareVersion">Firmware it reported.</param>
    public void MarkOnline(DateTimeOffset at, string? firmwareVersion)
    {
        Status = CabinetStatus.Online;
        LastSeenAt = at;
        FirmwareVersion = firmwareVersion ?? FirmwareVersion;
    }

    /// <summary>Records that the cabinet stopped heartbeating.</summary>
    /// <param name="at">When it was judged offline, UTC.</param>
    /// <remarks>
    /// Its slots become <see cref="SlotState.Unknown"/> rather than keeping their last value.
    /// A stale reading presented as current is the failure this avoids.
    /// </remarks>
    public void MarkOffline(DateTimeOffset at)
    {
        Status = CabinetStatus.Offline;
        LastSeenAt = at;

        foreach (var slot in _slots)
        {
            slot.MarkUnknown(at);
        }
    }

    /// <summary>Advances the applied sequence after an event is accepted.</summary>
    /// <param name="sequence">The sequence number of the event just applied.</param>
    /// <exception cref="ArgumentOutOfRangeException">The sequence has already been applied.</exception>
    public void AdvanceSequence(long sequence)
    {
        if (sequence <= LastAppliedSequence)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sequence),
                sequence,
                "Sequence numbers only move forward; a repeat should be discarded, not applied.");
        }

        LastAppliedSequence = sequence;
    }

    /// <summary>Whether an incoming event has already been applied.</summary>
    /// <param name="sequence">The event's sequence number.</param>
    /// <returns><see langword="true"/> when it should be discarded.</returns>
    public bool HasApplied(long sequence) => sequence <= LastAppliedSequence;
}
