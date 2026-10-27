using L5Sharp.Core;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// BOOL array elements and bit writes over CIP: an element is read through the 32-bit word that holds it, and a
/// bit (a BOOL array element, or <c>Tag.N</c> of an integer) is written with the CIP Read-Modify-Write service, so
/// the controller changes only that bit. No network: the word reads and the bit writes go to test seams.
/// </summary>
public class BoolArrayAndBitWriteTests
{
    private const string ProjectXml = """
        <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="38.1" TargetName="C" TargetType="Controller" ContainsContext="false">
          <Controller Name="C" MajorRev="38" MinorRev="1" ProcessorType="1756-L85E">
            <DataTypes>
              <DataType Name="Bits_UDT" Family="NoFamily" Class="User">
                <Members>
                  <Member Name="Flags" DataType="BOOL" Dimension="64" Radix="Binary" Hidden="false" ExternalAccess="Read/Write" />
                  <Member Name="Count" DataType="DINT" Dimension="0" Radix="Decimal" Hidden="false" ExternalAccess="Read/Write" />
                </Members>
              </DataType>
            </DataTypes>
            <Modules />
            <AddOnInstructionDefinitions />
            <Tags>
              <Tag Name="Alive" TagType="Base" DataType="BOOL" Dimensions="64" Constant="false" ExternalAccess="Read/Write" />
              <Tag Name="Words" TagType="Base" DataType="DINT" Dimensions="4" Constant="false" ExternalAccess="Read/Write" />
              <Tag Name="Run" TagType="Base" DataType="BOOL" Constant="false" ExternalAccess="Read/Write" />
              <Tag Name="Holder" TagType="Base" DataType="Bits_UDT" Constant="false" ExternalAccess="Read/Write">
                <Data Format="Decorated"><Structure DataType="Bits_UDT" /></Data>
              </Tag>
            </Tags>
            <Programs>
              <Program Name="P_Sim" TestEdits="false" MainRoutineName="Main" Disabled="false" UseAsFolder="false">
                <Tags>
                  <Tag Name="SimKillFB" TagType="Base" DataType="BOOL" Dimensions="32" Constant="false" ExternalAccess="Read/Write" />
                </Tags>
                <Routines />
              </Program>
            </Programs>
          </Controller>
        </RSLogix5000Content>
        """;

    private static CipTypeResolver Resolver() =>
        new(L5X.Parse(ProjectXml), _ => throw new InvalidOperationException("tag list must not be read"));

    /// <summary>Little-endian byte-array buffer standing in for a libplctag tag.</summary>
    private sealed class BytesBuffer(params byte[] bytes) : LibPlcTagService.ITagBuffer
    {
        public int GetSize() => bytes.Length;
        public bool GetBit(int o) => (bytes[o / 8] & (1 << (o % 8))) != 0;
        public sbyte GetInt8(int o) => (sbyte)bytes[o];
        public byte GetUInt8(int o) => bytes[o];
        public short GetInt16(int o) => BitConverter.ToInt16(bytes, o);
        public ushort GetUInt16(int o) => BitConverter.ToUInt16(bytes, o);
        public int GetInt32(int o) => BitConverter.ToInt32(bytes, o);
        public uint GetUInt32(int o) => BitConverter.ToUInt32(bytes, o);
        public long GetInt64(int o) => BitConverter.ToInt64(bytes, o);
        public ulong GetUInt64(int o) => BitConverter.ToUInt64(bytes, o);
        public float GetFloat32(int o) => BitConverter.ToSingle(bytes, o);
        public double GetFloat64(int o) => BitConverter.ToDouble(bytes, o);
    }

    /// <summary>A CIP service whose word reads return <paramref name="word"/> and whose bit writes are recorded.</summary>
    private static async Task<(LibPlcTagService Svc, List<string> Read, List<CipBitWrite> Written)> Service(byte[] word)
    {
        var read    = new List<string>();
        var written = new List<CipBitWrite>();
        var svc = new LibPlcTagService
        {
            TypeResolver = Resolver(),
            ParentBufferReader = (name, _) =>
            {
                read.Add(name);
                return Task.FromResult<LibPlcTagService.ITagBuffer>(new BytesBuffer(word));
            },
            BitWriter = (write, _) =>
            {
                written.Add(write);
                return Task.CompletedTask;
            },
        };
        await svc.ConnectAsync("198.51.100.1", "1,0");
        return (svc, read, written);
    }

    // ── The resolver marks BOOL array elements ────────────────────────────────

    [Theory]
    [InlineData("Alive[5]")]
    [InlineData("Alive[63]")]
    [InlineData("Program:P_Sim.SimKillFB[3]")]
    [InlineData("Holder.Flags[40]")]
    public async Task BoolArrayElements_AreMarked(string path)
    {
        var r = await Resolver().ResolveAsync(path);

        Assert.Equal("BOOL", r.TypeName);
        Assert.True(r.IsBoolArrayElement, r.Reason);
    }

    [Theory]
    [InlineData("Run")]
    [InlineData("Words[2]")]
    [InlineData("Holder.Count")]
    public async Task OtherPaths_AreNotMarked(string path)
    {
        var r = await Resolver().ResolveAsync(path);

        Assert.True(r.IsResolved, r.Reason);
        Assert.False(r.IsBoolArrayElement);
    }

    // ── Reading an element reads its word ─────────────────────────────────────

    [Theory]
    [InlineData("Alive[5]",                   "Alive[0]",                   new byte[] { 0x20, 0, 0, 0 }, true)]
    [InlineData("Alive[4]",                   "Alive[0]",                   new byte[] { 0x20, 0, 0, 0 }, false)]
    [InlineData("Alive[37]",                  "Alive[1]",                   new byte[] { 0x20, 0, 0, 0 }, true)]
    [InlineData("Alive[63]",                  "Alive[1]",                   new byte[] { 0, 0, 0, 0x80 }, true)]
    [InlineData("Program:P_Sim.SimKillFB[3]", "Program:P_Sim.SimKillFB[0]", new byte[] { 0x08, 0, 0, 0 }, true)]
    [InlineData("Holder.Flags[40]",           "Holder.Flags[1]",            new byte[] { 0, 0x01, 0, 0 }, true)]
    public async Task ReadElement_ReadsItsWord_AndReturnsTheBit(string path, string word, byte[] bytes, bool expected)
    {
        var (svc, read, _) = await Service(bytes);

        var v = await svc.ReadAsync(path);

        Assert.Equal(expected, v.Value);
        Assert.Equal("BOOL", v.DataType);
        Assert.Equal(path, v.TagName);
        Assert.Equal([word], read);
    }

    [Fact]
    public void ElementWord_OfTheWrongWidth_IsATypeMismatch()
    {
        var ex = Assert.Throws<TagServiceException>(() =>
            LibPlcTagService.DecodeBoolArrayElement("Alive[3]", 3, new BytesBuffer(0x08)));

        Assert.Equal(TagServiceErrorKind.TypeMismatch, ex.Error.Kind);
    }

    // ── Writing an element or a bit sends one Read-Modify-Write ───────────────

    [Theory]
    [InlineData("Alive[5]",                    true,  "Alive[0]",                   5)]
    [InlineData("Alive[37]",                   false, "Alive[1]",                   5)]
    [InlineData("Program:P_Sim.SimKillFB[31]", true,  "Program:P_Sim.SimKillFB[0]", 31)]
    [InlineData("Holder.Flags[40]",            false, "Holder.Flags[1]",            8)]
    public async Task WriteElement_SendsOneBitOfItsWord(string path, bool value, string word, int bit)
    {
        var (svc, read, written) = await Service(new byte[4]);

        await svc.WriteAsync(path, value);

        Assert.Equal([new CipBitWrite(word, 4, bit, value)], written);
        Assert.Empty(read); // the word is never read and written back from here
    }

    // A BOOL takes true or false only, as for any BOOL write: no silent coercion from numbers.
    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(2)]
    public async Task WriteElement_OfANumber_IsRefused_AndNothingIsSent(int value)
    {
        var (svc, _, written) = await Service(new byte[4]);

        await Assert.ThrowsAsync<TagServiceException>(() => svc.WriteAsync("Alive[5]", value));

        Assert.Empty(written);
    }

    // ── The Read-Modify-Write request ─────────────────────────────────────────

    [Fact]
    public void SymbolicPath_ProgramScopeAndElement()
    {
        var path = CipReadModifyWrite.EncodeSymbolicPath("Program:P_Sim.SimKillFB[0]");

        // "Program:P_Sim" is 13 characters (odd, so one pad byte); "SimKillFB" is 9 (odd, one pad byte).
        byte[] expected =
        [
            0x91, 13, .. "Program:P_Sim"u8.ToArray(), 0x00,
            0x91, 9,  .. "SimKillFB"u8.ToArray(), 0x00,
            0x28, 0x00,
        ];
        Assert.Equal(expected, path);
    }

    [Fact]
    public void SymbolicPath_MembersAndWideIndices()
    {
        byte[] member = [0x91, 6, .. "Holder"u8.ToArray(), 0x91, 5, .. "Flags"u8.ToArray(), 0x00, 0x28, 0x01];
        byte[] index16 = [0x91, 2, .. "Ab"u8.ToArray(), 0x29, 0x00, 0x2C, 0x01];
        byte[] index32 = [0x91, 2, .. "Ab"u8.ToArray(), 0x2A, 0x00, 0x00, 0x00, 0x01, 0x00];

        Assert.Equal(member, CipReadModifyWrite.EncodeSymbolicPath("Holder.Flags[1]"));
        Assert.Equal(index16, CipReadModifyWrite.EncodeSymbolicPath("Ab[300]"));
        Assert.Equal(index32, CipReadModifyWrite.EncodeSymbolicPath("Ab[65536]"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Program:.Tag")]
    [InlineData("Tag[x]")]
    [InlineData("Tag[1")]
    public void SymbolicPath_Malformed_IsRefused(string path) =>
        Assert.Throws<ArgumentException>(() => CipReadModifyWrite.EncodeSymbolicPath(path));

    [Fact]
    public void Request_SetsOneBit_WithAnOrMask()
    {
        var request = CipReadModifyWrite.BuildRequest(new CipBitWrite("Ab", 4, 5, true));

        // Service, path size (2 words), path, mask size 4, OR mask (bit 5), AND mask (all ones).
        byte[] expected = [0x4E, 2, 0x91, 2, (byte)'A', (byte)'b', 4, 0, 0x20, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF];
        Assert.Equal(expected, request);
    }

    [Fact]
    public void Request_ClearsOneBit_WithAnAndMask()
    {
        var request = CipReadModifyWrite.BuildRequest(new CipBitWrite("Ab", 2, 9, false));

        // Mask size 2, OR mask nothing, AND mask with bit 9 (byte 1, bit 1) cleared.
        byte[] expected = [0x4E, 2, 0x91, 2, (byte)'A', (byte)'b', 2, 0, 0, 0, 0xFF, 0xFD];
        Assert.Equal(expected, request);
    }

    [Theory]
    [InlineData(3, 0)]
    [InlineData(4, 32)]
    [InlineData(1, -1)]
    public void Request_BadMaskOrBit_IsRefused(int maskSize, int bit) =>
        Assert.Throws<ArgumentException>(() => CipReadModifyWrite.BuildRequest(new CipBitWrite("Ab", maskSize, bit, true)));

    // ── The reply ─────────────────────────────────────────────────────────────

    [Fact]
    public void Reply_Success_IsNoError()
    {
        var status = CipReadModifyWrite.ParseReplyStatus([0xCE, 0x00, 0x00, 0x00]);
        Assert.Null(CipReadModifyWrite.ErrorFor("Ab.5", status));
    }

    [Theory]
    [InlineData(new byte[] { 0xCE, 0, 0x0F, 0 }, TagServiceErrorKind.WriteDenied)]
    [InlineData(new byte[] { 0xCE, 0, 0x05, 0 }, TagServiceErrorKind.TagNotFound)]
    [InlineData(new byte[] { 0xCE, 0, 0x04, 0 }, TagServiceErrorKind.TagNotFound)]
    [InlineData(new byte[] { 0xCE, 0, 0x08, 0 }, TagServiceErrorKind.ProtocolError)]
    [InlineData(new byte[] { 0xD2, 0, 0x01, 1, 0x04, 0x02 }, TagServiceErrorKind.ConnectionFailed)]
    [InlineData(new byte[] { 0xCE, 0, 0x13, 0 }, TagServiceErrorKind.ProtocolError)]
    public void Reply_Refusals_AreTyped(byte[] reply, TagServiceErrorKind kind)
    {
        var error = CipReadModifyWrite.ErrorFor("Ab.5", CipReadModifyWrite.ParseReplyStatus(reply));

        Assert.NotNull(error);
        Assert.Equal(kind, error.Error.Kind);
        Assert.Contains("Ab.5", error.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reply_Unreadable_SaysTheWriteMayHaveHappened()
    {
        var error = CipReadModifyWrite.ErrorFor("Ab.5", CipReadModifyWrite.ParseReplyStatus([0xCE]));

        Assert.NotNull(error);
        Assert.Contains("may or may not", error.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RoutedRequest_WrapsTheRequestInAnUnconnectedSend()
    {
        var inner  = CipReadModifyWrite.BuildRequest(new CipBitWrite("Ab", 4, 0, true));
        var routed = ControllerNameReader.BuildRoutedRequest([0x01, 0x00], inner);

        // Unconnected Send, the embedded request and its length, then the route: 1 word, reserved, port 1 slot 0.
        byte[] routeTail = [0x01, 0x00, 0x01, 0x00];
        Assert.Equal(0x52, routed[0]);
        Assert.Equal(inner.Length, routed[8] | (routed[9] << 8));
        Assert.Equal(inner, routed[10..(10 + inner.Length)]);
        Assert.Equal(routeTail, routed[^4..]);
    }
}
