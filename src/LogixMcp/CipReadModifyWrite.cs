using System.Buffers.Binary;
using System.Globalization;
using System.Net.Sockets;
using System.Text;

// ── CipBitWrite ───────────────────────────────────────────────────────────────

/// <summary>One bit to set or clear in a controller tag with the CIP Read-Modify-Write Tag service.</summary>
/// <param name="TagPath">
/// The integer or BOOL-array word that holds the bit, e.g. <c>Bench_Dint</c> or
/// <c>Program:P_Sim.SimKillFB[0]</c> (a BOOL array's word index, not its bit index).
/// </param>
/// <param name="MaskSize">The size of that integer or word in bytes: 1, 2, 4 or 8.</param>
/// <param name="Bit">The bit within it, 0 to <c>MaskSize * 8 - 1</c>.</param>
/// <param name="Value"><c>true</c> to set the bit, <c>false</c> to clear it.</param>
public sealed record CipBitWrite(string TagPath, int MaskSize, int Bit, bool Value);

// ── CipReadModifyWrite ────────────────────────────────────────────────────────

/// <summary>
/// Writes one bit of a controller tag with the CIP Read-Modify-Write Tag service (0x4E). The request carries an OR
/// mask and an AND mask, and the controller applies them to the tag in one step, so only the masked bit changes:
/// a bit the running logic changes in the same integer is never overwritten, unlike a read of the whole integer
/// followed by a write of it.
///
/// <para>
/// Used for a bit of an integer (<c>Tag.5</c>) and for an element of a BOOL array, which CIP addresses by the
/// 32-bit word that holds it. The request goes over this server's own EtherNet/IP session (RegisterSession, then
/// SendRRData with an Unconnected Send along the session's CIP route), not through libplctag.
/// </para>
/// </summary>
public static class CipReadModifyWrite
{
    /// <summary>The CIP Read-Modify-Write Tag service code.</summary>
    public const byte Service = 0x4E;

    private const byte SymbolSegment  = 0x91;
    private const byte GeneralSuccess = 0x00;

    /// <summary>The default time allowed for the whole exchange.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    // ── Request ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Encodes <paramref name="tagPath"/> as a CIP request path: one ANSI extended symbol segment per name
    /// (<c>Program:Name</c> is a single name), padded to an even length, and one element segment per array index.
    /// </summary>
    /// <param name="tagPath">A tag path such as <c>Program:P_Sim.SimKillFB[0]</c> or <c>Master.Cnt.Word</c>.</param>
    /// <returns>The path bytes (always an even number).</returns>
    /// <exception cref="ArgumentException">The path is empty, malformed or has a non-numeric index.</exception>
    public static byte[] EncodeSymbolicPath(string tagPath)
    {
        if (string.IsNullOrWhiteSpace(tagPath))
            throw new ArgumentException("The tag path is empty.", nameof(tagPath));

        var text  = tagPath.Trim();
        var names = new List<string>();

        // "Program:Name" is one symbol; the tag and its members follow it.
        if (text.StartsWith("Program:", StringComparison.OrdinalIgnoreCase))
        {
            var dot = text.IndexOf('.');
            if (dot <= "Program:".Length)
                throw new ArgumentException($"'{tagPath}' has a program prefix but no tag name.", nameof(tagPath));
            names.Add(text[..dot]);
            text = text[(dot + 1)..];
        }

        // Split on '.' outside brackets.
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '[') depth++;
            else if (text[i] == ']') depth--;
            else if (text[i] == '.' && depth == 0) { names.Add(text[start..i]); start = i + 1; }
        }
        names.Add(text[start..]);
        if (depth != 0)
            throw new ArgumentException($"'{tagPath}' has unbalanced brackets.", nameof(tagPath));

        var path = new List<byte>();
        foreach (var part in names)
        {
            var bracket = part.IndexOf('[');
            var name    = bracket < 0 ? part : part[..bracket];
            if (name.Length == 0 || name.Length > 255 || name.Any(c => c > 0x7F))
                throw new ArgumentException($"'{tagPath}' has an invalid name '{name}'.", nameof(tagPath));

            path.Add(SymbolSegment);
            path.Add((byte)name.Length);
            path.AddRange(Encoding.ASCII.GetBytes(name));
            if (name.Length % 2 == 1) path.Add(0x00);

            if (bracket < 0) continue;
            if (!part.EndsWith(']'))
                throw new ArgumentException($"'{tagPath}' has text after an index.", nameof(tagPath));
            foreach (var raw in part[(bracket + 1)..^1].Split(','))
            {
                if (!uint.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                    throw new ArgumentException($"'{tagPath}' has a non-numeric index '{raw.Trim()}'.", nameof(tagPath));
                AddElementSegment(path, index);
            }
        }
        return [.. path];
    }

    // The smallest element segment that holds the index: 8-bit, 16-bit or 32-bit (each padded to whole words).
    private static void AddElementSegment(List<byte> path, uint index)
    {
        if (index <= 0xFF)
        {
            path.Add(0x28);
            path.Add((byte)index);
        }
        else if (index <= 0xFFFF)
        {
            path.Add(0x29);
            path.Add(0x00);
            path.Add((byte)index);
            path.Add((byte)(index >> 8));
        }
        else
        {
            path.Add(0x2A);
            path.Add(0x00);
            Span<byte> le = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(le, index);
            path.AddRange(le.ToArray());
        }
    }

    /// <summary>
    /// Builds the Read-Modify-Write request: the service, the tag's path, the mask size, the OR mask (the bit to
    /// set) and the AND mask (the bit to clear), each mask little-endian and <see cref="CipBitWrite.MaskSize"/> long.
    /// </summary>
    /// <param name="write">The bit to write.</param>
    /// <returns>The CIP request, ready to wrap in an Unconnected Send.</returns>
    /// <exception cref="ArgumentException">An unsupported mask size, a bit outside it, or a malformed path.</exception>
    public static byte[] BuildRequest(CipBitWrite write)
    {
        ArgumentNullException.ThrowIfNull(write);
        if (write.MaskSize is not (1 or 2 or 4 or 8))
            throw new ArgumentException($"Mask size {write.MaskSize} is not 1, 2, 4 or 8 bytes.", nameof(write));
        if (write.Bit < 0 || write.Bit >= write.MaskSize * 8)
            throw new ArgumentException($"Bit {write.Bit} is outside a {write.MaskSize}-byte mask.", nameof(write));

        var path = EncodeSymbolicPath(write.TagPath);

        // Setting: OR in the bit and keep every bit (AND all ones). Clearing: OR nothing and AND out the bit.
        var orMask  = new byte[write.MaskSize];
        var andMask = Enumerable.Repeat((byte)0xFF, write.MaskSize).ToArray();
        if (write.Value) orMask[write.Bit / 8]  |= (byte)(1 << (write.Bit % 8));
        else             andMask[write.Bit / 8] &= (byte)~(1 << (write.Bit % 8));

        var request = new List<byte> { Service, (byte)(path.Length / 2) };
        request.AddRange(path);
        request.Add((byte)write.MaskSize);
        request.Add(0x00);
        request.AddRange(orMask);
        request.AddRange(andMask);
        return [.. request];
    }

    // ── Reply ─────────────────────────────────────────────────────────────────

    /// <summary>The service, general status and first extended status word of a CIP reply.</summary>
    /// <param name="ReplyService">The reply service code (the request's service with bit 7 set).</param>
    /// <param name="GeneralStatus">The CIP general status; 0 is success.</param>
    /// <param name="ExtendedStatus">The first extended status word, or <c>null</c> when there is none.</param>
    public sealed record CipReplyStatus(byte ReplyService, byte GeneralStatus, ushort? ExtendedStatus);

    /// <summary>Reads the status fields of a CIP reply (the unconnected data item of a SendRRData reply).</summary>
    /// <param name="cip">The CIP reply bytes.</param>
    /// <returns>The status, or <c>null</c> when the reply is too short to hold one.</returns>
    public static CipReplyStatus? ParseReplyStatus(ReadOnlySpan<byte> cip)
    {
        if (cip.Length < 4) return null;
        ushort? extended = cip[3] > 0 && cip.Length >= 6 ? BinaryPrimitives.ReadUInt16LittleEndian(cip[4..]) : null;
        return new CipReplyStatus(cip[0], cip[2], extended);
    }

    /// <summary>
    /// The error for a refused or failed Read-Modify-Write, or <c>null</c> when the reply reports success.
    /// </summary>
    /// <param name="tagName">The path the caller wrote (the bit or element), for the message.</param>
    /// <param name="status">The parsed reply status, or <c>null</c> for an unreadable reply.</param>
    /// <returns>A typed error, or <c>null</c> on success.</returns>
    public static TagServiceException? ErrorFor(string tagName, CipReplyStatus? status)
    {
        if (status is null)
            return new TagServiceException(new TagServiceError(TagServiceErrorKind.ProtocolError,
                $"Cannot write '{tagName}': the controller's reply to Read-Modify-Write was unreadable. " +
                "The write may or may not have been applied; read the tag to check.", tagName));

        if (status.GeneralStatus == GeneralSuccess && status.ReplyService == (Service | 0x80))
            return null;

        var code = status.ExtendedStatus is { } ext
            ? $"CIP status 0x{status.GeneralStatus:X2}/0x{ext:X4}"
            : $"CIP status 0x{status.GeneralStatus:X2}";
        var (kind, why) = status.GeneralStatus switch
        {
            0x0F => (TagServiceErrorKind.WriteDenied,
                     "the controller refused it (privilege violation, ErrorNotAllowed): the tag's External Access is " +
                     "not Read/Write"),
            0x04 or 0x05 => (TagServiceErrorKind.TagNotFound, "the controller does not have that tag"),
            0x08 => (TagServiceErrorKind.ProtocolError, "the controller does not support Read-Modify-Write"),
            0x01 when status.ExtendedStatus == 0x0204 => (TagServiceErrorKind.ConnectionFailed,
                     "the request timed out on its way to the controller"),
            _ => (TagServiceErrorKind.ProtocolError, "the controller refused the Read-Modify-Write request"),
        };
        return new TagServiceException(new TagServiceError(kind,
            $"Cannot write '{tagName}': {why} ({code}). Nothing was written.", tagName));
    }

    // ── Exchange ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Sends <paramref name="write"/> to the controller at <paramref name="gateway"/> along <paramref name="path"/>
    /// and checks the reply.
    /// </summary>
    /// <param name="gateway">The controller's IP address or host name.</param>
    /// <param name="path">The CIP route as given to <c>logix_connect</c> (e.g. <c>1,0</c>); empty sends it unrouted.</param>
    /// <param name="tagName">The path the caller wrote, for messages.</param>
    /// <param name="write">The bit to write.</param>
    /// <param name="timeout">Time allowed for the whole exchange; <see cref="DefaultTimeout"/> when null.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="TagServiceException">The route cannot be used, the controller cannot be reached, or it
    /// refused the write.</exception>
    public static async Task WriteBitAsync(string gateway, string? path, string tagName, CipBitWrite write,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        // An empty path sends the request to the gateway itself, unrouted.
        byte[] route = string.IsNullOrWhiteSpace(path)
            ? []
            : ControllerNameReader.ParseRoute(path) ?? throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.ProtocolError,
                $"Cannot write '{tagName}': the CIP path '{path}' is not a list of port and slot numbers, which a " +
                "bit write needs. Nothing was written.", tagName));

        var request = ControllerNameReader.BuildRoutedRequest(route, BuildRequest(write));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? DefaultTimeout);
        byte[] reply;
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(gateway, EmulatorIdentity.DefaultPort, cts.Token).ConfigureAwait(false);
            var stream = client.GetStream();

            await stream.WriteAsync(ControllerFaultReader.BuildRegisterSession(), cts.Token).ConfigureAwait(false);
            var registered = await ControllerFaultReader.ReadFrameAsync(stream, cts.Token).ConfigureAwait(false);
            if (registered.Length < 12 || BinaryPrimitives.ReadUInt32LittleEndian(registered.AsSpan(8)) != 0)
                throw new TagServiceException(new TagServiceError(TagServiceErrorKind.ConnectionFailed,
                    $"Cannot write '{tagName}': {gateway} refused the EtherNet/IP session. Nothing was written.", tagName));
            var handle = BinaryPrimitives.ReadUInt32LittleEndian(registered.AsSpan(4));

            await stream.WriteAsync(ControllerFaultReader.BuildSendRRData(handle, request), cts.Token).ConfigureAwait(false);
            reply = await ControllerFaultReader.ReadFrameAsync(stream, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TagServiceException(new TagServiceError(TagServiceErrorKind.ConnectionFailed,
                $"Cannot write '{tagName}': no reply from {gateway} within {(timeout ?? DefaultTimeout).TotalSeconds:0} s. " +
                "The write may or may not have been applied; read the tag to check.", tagName));
        }
        catch (Exception ex) when (ex is SocketException or IOException or EndOfStreamException or ObjectDisposedException)
        {
            throw new TagServiceException(new TagServiceError(TagServiceErrorKind.ConnectionFailed,
                $"Cannot write '{tagName}': the connection to {gateway} failed ({ex.Message}). Nothing was written.",
                tagName), ex);
        }

        CipReplyStatus? status;
        try
        {
            status = ParseReplyStatus(ControllerFaultReader.ExtractCipItem(reply));
        }
        catch (FaultRecordException)
        {
            status = null;
        }

        if (ErrorFor(tagName, status) is { } error)
            throw error;
    }
}
