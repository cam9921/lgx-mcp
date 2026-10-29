using libplctag;

namespace LogixMcp.Tests;

/// <summary>
/// Copying a read tag's bytes. libplctag.NET 1.0.0's <c>Tag.GetUInt8</c> throws a bare
/// <see cref="LibPlcTagException"/> whenever the byte it reads is 0xFF, whatever the tag's status, and its other typed
/// getters do the same on their own sentinel values. A value holding such a byte must still read, and only a
/// status that is not Ok is an error. No network: the getter is a stand-in that behaves as libplctag's does.
/// </summary>
public class CipByteCopyTests
{
    // Behaves as libplctag.NET 1.0.0's Tag.GetUInt8: a bare exception on 0xFF, with no status check.
    private static Func<int, byte> LibPlcTagGetUInt8(byte[] wire) => offset =>
        wire[offset] == byte.MaxValue ? throw new LibPlcTagException() : wire[offset];

    [Theory]
    [InlineData(-5)]
    [InlineData(-1)]
    [InlineData(999)]
    [InlineData(999_999_999)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(-256)]
    [InlineData(16_711_680)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void Dint_EveryValue_CopiesExactly(int value)
    {
        var wire = BitConverter.GetBytes(value);

        var bytes = LibPlcTagService.CopyBytes(wire.Length, LibPlcTagGetUInt8(wire), () => Status.Ok);

        Assert.Equal(value, BitConverter.ToInt32(bytes));
    }

    [Theory]
    [InlineData((short)-1)]
    [InlineData((short)-5)]
    [InlineData((short)255)]
    [InlineData(short.MinValue)]
    [InlineData(short.MaxValue)]
    public void Int_EveryValue_CopiesExactly(short value)
    {
        var wire = BitConverter.GetBytes(value);

        var bytes = LibPlcTagService.CopyBytes(wire.Length, LibPlcTagGetUInt8(wire), () => Status.Ok);

        Assert.Equal(value, BitConverter.ToInt16(bytes));
    }

    [Theory]
    [InlineData((sbyte)-1)]
    [InlineData((sbyte)-5)]
    [InlineData(sbyte.MinValue)]
    [InlineData(sbyte.MaxValue)]
    public void Sint_EveryValue_CopiesExactly(sbyte value)
    {
        var wire = new[] { unchecked((byte)value) };

        var bytes = LibPlcTagService.CopyBytes(wire.Length, LibPlcTagGetUInt8(wire), () => Status.Ok);

        Assert.Equal(value, unchecked((sbyte)bytes[0]));
    }

    [Fact]
    public void ThrowWithStatusNotOk_IsAnError()
    {
        var wire = new byte[] { 0x01, 0xFF, 0x00, 0x00 };

        Assert.Throws<LibPlcTagException>(() =>
            LibPlcTagService.CopyBytes(wire.Length, LibPlcTagGetUInt8(wire), () => Status.ErrorOutOfBounds));
    }

    [Fact]
    public void MapException_NamesOperationTagStatusAndCode()
    {
        var ex = LibPlcTagService.MapException(
            new LibPlcTagException(Status.ErrorBadParam), Status.ErrorBadParam, "Line.Count", "read");

        Assert.Equal("libplctag read of 'Line.Count' failed with status ErrorBadParam (-7)", ex.Error.Message);
        Assert.Equal("Line.Count", ex.Error.TagName);
    }

    [Fact]
    public void WrapReadFailure_BareLibPlcTagException_StillNamesTagAndOperation()
    {
        var ex = LibPlcTagService.WrapReadFailure(new LibPlcTagException(), "Line.Count", "grouped read");

        Assert.Contains("grouped read", ex.Error.Message);
        Assert.Contains("'Line.Count'", ex.Error.Message);
        Assert.DoesNotContain("Exception of type", ex.Error.Message);
    }
}
