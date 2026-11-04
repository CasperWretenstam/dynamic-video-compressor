using System.Globalization;

namespace Dvc.Core.Compression;

internal static class ByteFormatting
{
    // An exact byte count with thousands separators, e.g. "10,000,000 bytes",
    // the same on every locale.
    public static string Format(long bytes) =>
        $"{bytes.ToString("N0", CultureInfo.InvariantCulture)} bytes";
}
