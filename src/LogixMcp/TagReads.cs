// ── TagReadOutcome ────────────────────────────────────────────────────────────

/// <summary>The result of reading one tag as part of a group: its value, or the error a single read would throw.</summary>
/// <param name="Tag">The tag path as requested.</param>
/// <param name="Value">The value read, or <c>null</c> when the read failed.</param>
/// <param name="Error">Why the read failed, or <c>null</c> when it succeeded.</param>
public sealed record TagReadOutcome(string Tag, TagValue? Value, Exception? Error);

// ── TagReads ──────────────────────────────────────────────────────────────────

/// <summary>Reads a group of tags that describe one moment, such as one poll cycle or one test step's checks.</summary>
public static class TagReads
{
    /// <summary>
    /// Reads <paramref name="tags"/>. On a CIP session they are read together, in one CIP request
    /// (<see cref="LibPlcTagService.ReadManyAsync"/>), so the values come from nearly the same moment instead of
    /// one round trip apart; on any other backend they are read one at a time, in order.
    /// </summary>
    /// <param name="service">The session's tag service.</param>
    /// <param name="tags">The tag paths, in the order the results are wanted.</param>
    /// <param name="ct">Cancellation token; cancellation is thrown, not captured.</param>
    /// <returns>One outcome per requested tag, in the same order.</returns>
    public static async Task<IReadOnlyList<TagReadOutcome>> ReadTogetherAsync(
        ITagService service, IReadOnlyList<string> tags, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(tags);

        if (tags.Count > 1 && TagRouteResolver.Backend(service) is LibPlcTagService cip)
            return await cip.ReadManyAsync(tags, ct).ConfigureAwait(false);

        var outcomes = new List<TagReadOutcome>(tags.Count);
        foreach (var tag in tags)
        {
            try
            {
                outcomes.Add(new TagReadOutcome(tag, await service.ReadAsync(tag, ct).ConfigureAwait(false), null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                outcomes.Add(new TagReadOutcome(tag, null, ex));
            }
        }
        return outcomes;
    }
}
