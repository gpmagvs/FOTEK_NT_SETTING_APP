namespace FOTEK_NT_SETTING_APP.Services;

/// <summary>
/// Groups scattered holding-register addresses into consecutive ranges
/// and reads them with fewer Modbus transactions.
/// </summary>
public static class RegisterBatchReader
{
    public const ushort MaxRegistersPerRequest = 50;

    public static Dictionary<ushort, ushort> ReadMany(
        Func<ushort, ushort, ushort[]> readBlock,
        IEnumerable<ushort> addresses)
    {
        var unique = addresses.Distinct().OrderBy(a => a).ToArray();
        var map = new Dictionary<ushort, ushort>(unique.Length);
        if (unique.Length == 0)
            return map;

        var rangeStart = unique[0];
        var prev = unique[0];

        void flush(ushort start, ushort endInclusive)
        {
            var count = (ushort)(endInclusive - start + 1);
            while (count > 0)
            {
                var chunk = (ushort)Math.Min(count, MaxRegistersPerRequest);
                var values = readBlock(start, chunk);
                for (var i = 0; i < chunk; i++)
                    map[(ushort)(start + i)] = values[i];
                start = (ushort)(start + chunk);
                count = (ushort)(count - chunk);
            }
        }

        for (var i = 1; i < unique.Length; i++)
        {
            var addr = unique[i];
            // allow small gaps (<=2) to still batch; larger gaps start new range
            if (addr <= prev + 2 && addr - rangeStart + 1 <= MaxRegistersPerRequest)
            {
                prev = addr;
                continue;
            }

            flush(rangeStart, prev);
            rangeStart = addr;
            prev = addr;
        }

        flush(rangeStart, prev);

        // Only return requested addresses (drop gap fillers)
        var requested = unique.ToHashSet();
        return map.Where(kv => requested.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
    }
}
