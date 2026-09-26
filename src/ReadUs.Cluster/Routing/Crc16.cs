namespace ReadUs.Cluster.Routing;

/// <summary>
/// CRC-16/XMODEM (poly 0x1021, init 0x0000, no reflection) — the exact variant Redis's
/// own <c>src/crc16.c</c> uses for cluster key hashing (project spec §5). Verified
/// against the standard XMODEM check value in tests: CRC16("123456789") = 0x31C3.
/// </summary>
internal static class Crc16
{
    private static readonly ushort[] Table = BuildTable();

    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (byte b in data)
        {
            crc = (ushort)((crc << 8) ^ Table[((crc >> 8) ^ b) & 0xFF]);
        }

        return crc;
    }

    private static ushort[] BuildTable()
    {
        var table = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            ushort crc = (ushort)(i << 8);
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
            }

            table[i] = crc;
        }

        return table;
    }
}
