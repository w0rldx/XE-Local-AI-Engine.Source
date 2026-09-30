namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;

using System.Runtime.InteropServices;

/// <summary>The owner, group and mode of one filesystem object, read through <c>statx(2)</c>.</summary>
/// <remarks>
///     Everything the isolation layer decides about trust — is this binary root-owned, is this directory writable by anyone but us, is
///     this component a symlink — is decided from these three numbers, so they are read once and passed around as a value rather than
///     re-statted per question.
/// </remarks>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct SandboxUnixFileFacts(uint UserId, uint GroupId, uint Mode)
{
    // S_IFMT and the file-type constants. Spelled out rather than taken from a framework enum because
    // UnixFileMode carries only the permission bits and cannot answer "is this a symlink".
    private const uint FileTypeMask = 0xF000;
    private const uint DirectoryType = 0x4000;
    private const uint RegularFileType = 0x8000;
    private const uint SymbolicLinkType = 0xA000;

    private const uint StickyBit = 0x200;
    private const uint GroupAndWorldWrite = 0b000_010_010;
    private const uint AnyExecute = 0b001_001_001;

    public bool IsDirectory => (Mode & FileTypeMask) == DirectoryType;

    public bool IsRegularFile => (Mode & FileTypeMask) == RegularFileType;

    public bool IsSymbolicLink => (Mode & FileTypeMask) == SymbolicLinkType;

    /// <summary>
    ///     <see langword="true" /> for a directory carrying the sticky bit — the property that makes a world-writable
    ///     shared directory such as <c>/tmp</c> safe to have as an ancestor: entries in it can only be renamed or
    ///     removed by their own owner.
    /// </summary>
    public bool IsStickyDirectory => IsDirectory && (Mode & StickyBit) != 0;

    public bool IsGroupOrWorldWritable => (Mode & GroupAndWorldWrite) != 0;

    public bool HasAnyExecuteBit => (Mode & AnyExecute) != 0;

    /// <summary>The permission bits alone (the low twelve, so setuid/setgid/sticky are included).</summary>
    public uint PermissionBits => Mode & 0xFFF;
}
