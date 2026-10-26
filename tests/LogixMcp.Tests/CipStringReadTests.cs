using System.Text;

namespace LogixMcp.Tests;

/// <summary>
/// Reading STRING and user-defined string values over CIP: <see cref="CipTypeResolver"/> reports the
/// <c>DATA</c> capacity and <see cref="LibPlcTagService.DecodeValue"/> decodes the text (no network).
/// </summary>
public class CipStringReadTests
{
    private const string ProjectXml = """
        <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="38.1" TargetName="C" TargetType="Controller" ContainsContext="false">
          <Controller Name="C" MajorRev="38" MinorRev="1" ProcessorType="1756-L85E">
            <DataTypes>
              <DataType Name="Text20" Family="StringFamily" Class="User">
                <Members>
                  <Member Name="LEN" DataType="DINT" Dimension="0" Radix="Decimal" Hidden="false" ExternalAccess="Read/Write" />
                  <Member Name="DATA" DataType="SINT" Dimension="20" Radix="ASCII" Hidden="false" ExternalAccess="Read/Write" />
                </Members>
              </DataType>
              <DataType Name="Master_UDT" Family="NoFamily" Class="User">
                <Members>
                  <Member Name="Version" DataType="DINT" Dimension="0" Radix="Decimal" Hidden="false" ExternalAccess="Read Only" />
                  <Member Name="Cfg_Tag" DataType="STRING" Dimension="0" Radix="NullType" Hidden="false" ExternalAccess="Read/Write" />
                  <Member Name="Label" DataType="Text20" Dimension="0" Radix="NullType" Hidden="false" ExternalAccess="Read/Write" />
                </Members>
              </DataType>
            </DataTypes>
            <Modules />
            <AddOnInstructionDefinitions />
            <Tags>
              <Tag Name="Master" TagType="Base" DataType="Master_UDT" Constant="false" ExternalAccess="Read/Write">
                <Data Format="Decorated"><Structure DataType="Master_UDT" /></Data>
              </Tag>
              <Tag Name="Name" TagType="Base" DataType="STRING" Constant="false" ExternalAccess="Read/Write" />
              <Tag Name="Names" TagType="Base" DataType="STRING" Dimensions="2" Constant="false" ExternalAccess="Read/Write" />
            </Tags>
            <Programs />
          </Controller>
        </RSLogix5000Content>
        """;

    private static CipTypeResolver Resolver() =>
        new(L5Sharp.Core.L5X.Parse(ProjectXml), _ => throw new InvalidOperationException("tag list must not be read"));

    /// <summary>Little-endian byte-array buffer standing in for a libplctag tag.</summary>
    private sealed class BytesBuffer(byte[] bytes) : LibPlcTagService.ITagBuffer
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

    /// <summary>The native layout of a string value: LEN, then DATA[capacity], padded to a multiple of 4.</summary>
    private static BytesBuffer StringBuffer(int len, byte[] data, int capacity)
    {
        var bytes = new byte[(4 + capacity + 3) / 4 * 4];
        BitConverter.GetBytes(len).CopyTo(bytes, 0);
        data.CopyTo(bytes, 4);
        return new BytesBuffer(bytes);
    }

    private static BytesBuffer StringBuffer(string text, int capacity = 82) =>
        StringBuffer(text.Length, Encoding.Latin1.GetBytes(text), capacity);

    private static CipTypeResolution StringType(string name = "STRING", int capacity = 82) =>
        new(CipTypeKind.Structure, name, CipTypeSource.Project, ["LEN", "DATA"]) { StringCapacity = capacity };

    // ── The resolver reports the capacity ─────────────────────────────────────

    [Theory]
    [InlineData("Master.Cfg_Tag", "STRING", 82)]
    [InlineData("Master.Label",   "Text20", 20)]
    [InlineData("Name",           "STRING", 82)]
    [InlineData("Names[1]",       "STRING", 82)]
    public async Task StringTypes_CarryTheirCapacity(string path, string type, int capacity)
    {
        var r = await Resolver().ResolveAsync(path);

        Assert.Equal(CipTypeKind.Structure, r.Kind);
        Assert.Equal(type, r.TypeName);
        Assert.Equal(capacity, r.StringCapacity);
    }

    [Theory]
    [InlineData("Master")]
    [InlineData("Master.Version")]
    public async Task OtherTypes_HaveNoCapacity(string path)
    {
        var r = await Resolver().ResolveAsync(path);

        Assert.True(r.IsResolved, r.Reason);
        Assert.Null(r.StringCapacity);
    }

    // ── The decoder reads the text ────────────────────────────────────────────

    [Fact]
    public void String_DecodesItsText()
    {
        var value = LibPlcTagService.DecodeValue("Master.Cfg_Tag", StringType(), StringBuffer("GW_STUB"));

        Assert.Equal("GW_STUB", value.Value);
        Assert.Equal("STRING", value.DataType);
        Assert.Equal("Master.Cfg_Tag", value.TagName);
    }

    [Fact]
    public void EmptyString_IsEmpty()
    {
        Assert.Equal("", LibPlcTagService.DecodeValue("Name", StringType(), StringBuffer("")).Value);
    }

    [Fact]
    public void FullString_IsNotCut()
    {
        var full = new string('A', 82);
        Assert.Equal(full, LibPlcTagService.DecodeValue("Name", StringType(), StringBuffer(full)).Value);
    }

    [Fact]
    public void HighBytes_RoundTripAsLatin1()
    {
        var bytes = Enumerable.Range(0x80, 0x80).Select(b => (byte)b).Take(82).ToArray();

        var text = (string)LibPlcTagService.DecodeValue("Name", StringType(), StringBuffer(82, bytes, 82)).Value;

        Assert.Equal(bytes, text.Select(c => (byte)c).ToArray());
    }

    [Theory]
    [InlineData(83)]
    [InlineData(1000)]
    [InlineData(-1)]
    public void LenOutsideCapacity_IsRefused_NotCut(int len)
    {
        var ex = Assert.Throws<TagServiceException>(() =>
            LibPlcTagService.DecodeValue("Name", StringType(), StringBuffer(len, [], 82)));

        Assert.Equal(TagServiceErrorKind.StringLengthInvalid, ex.Error.Kind);
        Assert.Contains($"LEN is {len}", ex.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ShortReply_IsATypeMismatch()
    {
        var ex = Assert.Throws<TagServiceException>(() =>
            LibPlcTagService.DecodeValue("Name", StringType(), new BytesBuffer(new byte[40])));

        Assert.Equal(TagServiceErrorKind.TypeMismatch, ex.Error.Kind);
    }

    [Fact]
    public void UserStringType_UsesItsOwnCapacity()
    {
        var type = StringType("Text20", 20);

        var value = LibPlcTagService.DecodeValue("Master.Label", type, StringBuffer(new string('x', 20), 20));
        Assert.Equal(new string('x', 20), value.Value);
        Assert.Equal("Text20", value.DataType);

        var ex = Assert.Throws<TagServiceException>(() =>
            LibPlcTagService.DecodeValue("Master.Label", type, StringBuffer(21, [], 20)));
        Assert.Equal(TagServiceErrorKind.StringLengthInvalid, ex.Error.Kind);
    }

    [Fact]
    public void OtherStructures_AreStillRefused()
    {
        var udt = new CipTypeResolution(CipTypeKind.Structure, "Master_UDT", CipTypeSource.Project, ["Version"]);

        var ex = Assert.Throws<TagServiceException>(() =>
            LibPlcTagService.DecodeValue("Master", udt, new BytesBuffer(new byte[120])));

        Assert.Equal(TagServiceErrorKind.StructureReadUnsupported, ex.Error.Kind);
    }
}
