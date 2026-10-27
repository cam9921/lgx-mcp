using L5Sharp.Core;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Reading a group of tags together: on a CIP session every read is started before any is awaited (so libplctag
/// can pack them into one CIP request), each value is decoded exactly as a single read decodes it, and each tag
/// keeps its own error. No network: the reads go to the service's buffer seam.
/// </summary>
public class ReadTogetherTests
{
    private const string ProjectXml = """
        <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="38.1" TargetName="C" TargetType="Controller" ContainsContext="false">
          <Controller Name="C" MajorRev="38" MinorRev="1" ProcessorType="1756-L85E">
            <DataTypes />
            <Modules />
            <AddOnInstructionDefinitions />
            <Tags>
              <Tag Name="Speed" TagType="Base" DataType="REAL" Constant="false" ExternalAccess="Read/Write" />
              <Tag Name="Count" TagType="Base" DataType="DINT" Constant="false" ExternalAccess="Read/Write" />
              <Tag Name="Word" TagType="Base" DataType="DINT" Constant="false" ExternalAccess="Read/Write" />
              <Tag Name="Alive" TagType="Base" DataType="BOOL" Dimensions="64" Constant="false" ExternalAccess="Read/Write" />
              <Tag Name="Name" TagType="Base" DataType="STRING" Constant="false" ExternalAccess="Read/Write" />
            </Tags>
            <Programs />
          </Controller>
        </RSLogix5000Content>
        """;

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

    private static byte[] StringBytes(string text)
    {
        var bytes = new byte[88];
        BitConverter.GetBytes(text.Length).CopyTo(bytes, 0);
        System.Text.Encoding.ASCII.GetBytes(text).CopyTo(bytes, 4);
        return bytes;
    }

    /// <summary>
    /// A CIP service whose reads all wait until <paramref name="expected"/> of them have started, so a test can only
    /// pass when the reads are issued together. Records every handle read.
    /// </summary>
    private static async Task<(LibPlcTagService Svc, List<string> Read)> Service(
        Dictionary<string, Func<LibPlcTagService.ITagBuffer>> replies, int expected)
    {
        var read    = new List<string>();
        var started = 0;
        var allIn   = new TaskCompletionSource();
        var svc = new LibPlcTagService
        {
            TypeResolver = new CipTypeResolver(L5X.Parse(ProjectXml),
                _ => throw new InvalidOperationException("tag list must not be read")),
            ParentBufferReader = async (name, _) =>
            {
                lock (read) read.Add(name);
                if (Interlocked.Increment(ref started) == expected) allIn.SetResult();
                await allIn.Task.WaitAsync(TimeSpan.FromSeconds(5));
                return replies[name]();
            },
        };
        await svc.ConnectAsync("198.51.100.1", "1,0");
        return (svc, read);
    }

    [Fact]
    public async Task Cip_StartsEveryRead_BeforeAwaitingAny_AndDecodesEach()
    {
        var replies = new Dictionary<string, Func<LibPlcTagService.ITagBuffer>>
        {
            ["Speed"]    = () => new BytesBuffer(BitConverter.GetBytes(1.5f)),
            ["Count"]    = () => new BytesBuffer(BitConverter.GetBytes(42)),
            ["Word"]     = () => new BytesBuffer(0x20, 0, 0, 0),
            ["Alive[1]"] = () => new BytesBuffer(0x01, 0, 0, 0x80),
            ["Name"]     = () => new BytesBuffer(StringBytes("GW_STUB")),
        };
        var (svc, read) = await Service(replies, expected: 5);

        string[] tags = ["Speed", "Count", "Word.5", "Alive[32]", "Alive[63]", "Name"];
        var outcomes = await TagReads.ReadTogetherAsync(new GatedTagService(svc, new WriteGatePolicy([], [])), tags);

        Assert.Equal(tags, outcomes.Select(o => o.Tag));
        Assert.All(outcomes, o => Assert.Null(o.Error));
        Assert.Equal(1.5f, outcomes[0].Value!.Value);
        Assert.Equal(42, outcomes[1].Value!.Value);
        Assert.Equal(true, outcomes[2].Value!.Value);  // bit 5 of Word
        Assert.Equal(true, outcomes[3].Value!.Value);  // Alive[32]: word 1, bit 0
        Assert.Equal(true, outcomes[4].Value!.Value);  // Alive[63]: word 1, bit 31
        Assert.Equal("GW_STUB", outcomes[5].Value!.Value);
        Assert.Equal(5, read.Count);                   // two elements of one BOOL word share one read
    }

    [Fact]
    public async Task Cip_EachTagKeepsItsOwnError()
    {
        var replies = new Dictionary<string, Func<LibPlcTagService.ITagBuffer>>
        {
            ["Speed"] = () => new BytesBuffer(BitConverter.GetBytes(2.5f)),
            ["Count"] = () => throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.TagNotFound, "not found", "Count")),
        };
        var (svc, _) = await Service(replies, expected: 2);

        var outcomes = await svc.ReadManyAsync(["Speed", "Count", "Word.40"]);

        Assert.Equal(2.5f, outcomes[0].Value!.Value);
        Assert.Equal(TagServiceErrorKind.TagNotFound, Assert.IsType<TagServiceException>(outcomes[1].Error).Error.Kind);
        Assert.Equal(TagServiceErrorKind.BitOutOfRange, Assert.IsType<TagServiceException>(outcomes[2].Error).Error.Kind);
    }

    [Fact]
    public async Task OtherBackends_AreReadOneAtATime_InOrder()
    {
        var store = new VirtualTagService(new Dictionary<string, (object, string?)>
        {
            ["A"] = (1, "DINT"),
            ["B"] = (2, "DINT"),
        });

        var outcomes = await TagReads.ReadTogetherAsync(store, ["B", "A", "Missing"]);

        Assert.Equal(2, outcomes[0].Value!.Value);
        Assert.Equal(1, outcomes[1].Value!.Value);
        Assert.NotNull(outcomes[2].Error);
    }
}
