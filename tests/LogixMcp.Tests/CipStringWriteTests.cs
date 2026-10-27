namespace LogixMcp.Tests;

/// <summary>
/// Writing a STRING or user-defined string type over CIP as text: the value's text, the checks that refuse text
/// that does not fit, the bytes put in the buffer, and the read-back comparison. No network.
/// </summary>
public class CipStringWriteTests
{
    /// <summary>A write buffer over a byte array of the size the controller returned.</summary>
    private sealed class RecordingBuffer(int size) : LibPlcTagService.ITagWriteBuffer
    {
        public byte[] Bytes { get; } = Enumerable.Repeat((byte)0xAA, size).ToArray(); // stale bytes, to see them cleared
        public int GetSize() => Bytes.Length;
        public void SetBit(int offset, bool value) => throw new InvalidOperationException();
        public void SetInt8(int offset, sbyte value) => Bytes[offset] = (byte)value;
        public void SetUInt8(int offset, byte value) => Bytes[offset] = value;
        public void SetInt16(int offset, short value) => BitConverter.GetBytes(value).CopyTo(Bytes, offset);
        public void SetUInt16(int offset, ushort value) => BitConverter.GetBytes(value).CopyTo(Bytes, offset);
        public void SetInt32(int offset, int value) => BitConverter.GetBytes(value).CopyTo(Bytes, offset);
        public void SetUInt32(int offset, uint value) => BitConverter.GetBytes(value).CopyTo(Bytes, offset);
        public void SetInt64(int offset, long value) => BitConverter.GetBytes(value).CopyTo(Bytes, offset);
        public void SetUInt64(int offset, ulong value) => BitConverter.GetBytes(value).CopyTo(Bytes, offset);
        public void SetFloat32(int offset, float value) => BitConverter.GetBytes(value).CopyTo(Bytes, offset);
        public void SetFloat64(int offset, double value) => BitConverter.GetBytes(value).CopyTo(Bytes, offset);
    }

    [Fact]
    public void ApplyString_SetsLenAndData_AndClearsTheRest()
    {
        var buffer = new RecordingBuffer(88);

        LibPlcTagService.ApplyString(buffer, "Devices[5].Cfg_Tag", "STRING", 82, "SPARE");

        Assert.Equal(5, BitConverter.ToInt32(buffer.Bytes, 0));
        Assert.Equal("SPARE"u8.ToArray(), buffer.Bytes[4..9]);
        Assert.All(buffer.Bytes[9..86], b => Assert.Equal(0, b)); // the rest of DATA cleared
    }

    [Fact]
    public void ApplyString_UserStringType_UsesItsCapacity()
    {
        var buffer = new RecordingBuffer(24);

        LibPlcTagService.ApplyString(buffer, "Label", "Text20", 20, new string('x', 20));

        Assert.Equal(20, BitConverter.ToInt32(buffer.Bytes, 0));
        Assert.All(buffer.Bytes[4..24], b => Assert.Equal((byte)'x', b));
    }

    [Fact]
    public void ApplyString_WrongBufferSize_IsATypeMismatch_AndNothingIsSet()
    {
        var buffer = new RecordingBuffer(40);

        var ex = Assert.Throws<TagServiceException>(() =>
            LibPlcTagService.ApplyString(buffer, "Name", "STRING", 82, "SPARE"));

        Assert.Equal(TagServiceErrorKind.TypeMismatch, ex.Error.Kind);
        Assert.All(buffer.Bytes, b => Assert.Equal(0xAA, b));
    }

    [Theory]
    [InlineData("SPARE", "SPARE")]
    [InlineData(42, "42")]
    [InlineData(3.5, "3.5")]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    public void TextOf_GivesTheValuesText(object value, string text) =>
        Assert.Equal(text, LibPlcTagService.TextOf(value));

    [Fact]
    public void Text_LongerThanTheCapacity_IsRefused()
    {
        var ex = Assert.Throws<TagServiceException>(() =>
            LibPlcTagService.StringTextForWrite("Label", new string('x', 21), "Text20", 20));

        Assert.Equal(TagServiceErrorKind.InvalidValue, ex.Error.Kind);
        Assert.Contains("21 characters", ex.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_WithACharacterOutsideOneByte_IsRefused()
    {
        var ex = Assert.Throws<TagServiceException>(() =>
            LibPlcTagService.StringTextForWrite("Name", "Café ☕", "STRING", 82));

        Assert.Equal(TagServiceErrorKind.InvalidValue, ex.Error.Kind);
        Assert.Contains("U+2615", ex.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_UpToTheCapacity_AndLatin1_IsAccepted() =>
        Assert.Equal("Café", LibPlcTagService.StringTextForWrite("Name", "Café", "STRING", 82));

    [Theory]
    [InlineData("SPARE", "SPARE", true)]
    [InlineData("SPARE", "SPAR", false)]
    [InlineData(42, "42", true)]
    [InlineData(true, "true", true)]
    public void ReadBack_ComparesAsText(object written, string observed, bool match) =>
        Assert.Equal(match, WriteTagHandler.ValuesMatch(written, observed, "STRING"));
}
