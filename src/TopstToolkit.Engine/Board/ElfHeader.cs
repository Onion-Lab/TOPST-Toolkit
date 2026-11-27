using System.Buffers.Binary;

namespace TopstToolkit.Engine.Board;

/// <summary>The ELF header fields needed to confirm that a built file is an AArch64 Linux shared library.</summary>
public sealed record ElfHeader(bool Is64Bit, bool IsLittleEndian, ushort Type, ushort Machine)
{
    /// <summary>ET_DYN: shared object.</summary>
    public const ushort TypeSharedObject = 3;
    /// <summary>EM_AARCH64.</summary>
    public const ushort MachineAArch64 = 183;

    public bool IsAArch64SharedObject => Is64Bit && IsLittleEndian && Type == TypeSharedObject && Machine == MachineAArch64;

    /// <summary>Null when the file is missing, too short or not ELF.</summary>
    public static ElfHeader? TryRead(string path)
    {
        if (!File.Exists(path))
            return null;
        var h = new byte[20];
        using var stream = File.OpenRead(path);
        if (stream.ReadAtLeast(h, h.Length, throwOnEndOfStream: false) < h.Length)
            return null;
        return Parse(h);
    }

    /// <summary>Parses the first 20 bytes of a file. Null when they are not an ELF header.</summary>
    public static ElfHeader? Parse(ReadOnlySpan<byte> h)
    {
        if (h.Length < 20 || h[0] != 0x7F || h[1] != (byte)'E' || h[2] != (byte)'L' || h[3] != (byte)'F')
            return null;
        var little = h[5] == 1;
        return new ElfHeader(h[4] == 2, little, U16(h.Slice(16, 2), little), U16(h.Slice(18, 2), little));
    }

    private static ushort U16(ReadOnlySpan<byte> bytes, bool little) =>
        little ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt16BigEndian(bytes);
}
