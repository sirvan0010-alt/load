using System.Diagnostics;

namespace MailLoadTester;

public sealed record AttachmentSource(string FileName, string FullPath, byte[]? PreloadedContent, long Length);

public sealed record AttachmentPlan(
    IReadOnlyList<AttachmentSource> Sources,
    long TotalBytes,
    long AvailableMemoryBytes,
    long PreloadBudgetBytes,
    bool Preloaded,
    string Description);

public static class AttachmentPlanner
{
    private const long MaxPreloadBytes = 512L * 1024 * 1024;
    private const long MinPreloadBudget = 64L * 1024 * 1024;
    private const long RandomAttachmentSafetyHardCap = 1L * 1024 * 1024 * 1024; // 1 GiB total transient payload
    private const double RandomAttachmentSafetyFraction = 0.15; // keep 85% of available memory for OS/app/other work


    public sealed record RandomAttachmentSafetyEstimate(
        long AvailableMemoryBytes,
        long SafeBudgetBytes,
        long RequestedPerAttachmentBytes,
        long WorstCaseBytes,
        long EstimatedMimeBytes,
        long EstimatedTransientBytes,
        bool IsSafe,
        bool WasAutoClamped,
        string Explanation);

    public static long GetAvailableMemoryBytes() => GetAvailableMemory();

    /// <summary>
    /// Checks a one-time byte[] preload (for example inline/CID attachments) against
    /// the same conservative RAM policy used by the attachment planner. This is
    /// deliberately called before File.ReadAllBytes so a large inline file cannot
    /// bypass the normal memory guard.
    /// </summary>
    public static void EnsurePreloadSafe(long bytes, string description = "přednačtený obsah")
    {
        if (bytes < 0)
            throw new ArgumentOutOfRangeException(nameof(bytes));

        const long hardCap = 512L * 1024 * 1024;
        var available = GetAvailableMemory();
        var budget = Math.Min(hardCap, Math.Max(1L, available / 10));

        if (bytes > hardCap || bytes > budget)
        {
            throw new InvalidOperationException(
                $"RAM safety: {description} ({FormatBytes(bytes)}) by překročil bezpečný preload budget " +
                $"{FormatBytes(budget)} při aktuálně dostupné RAM {FormatBytes(available)}. " +
                "Načtení bylo zablokováno před alokací.");
        }
    }

    public static RandomAttachmentSafetyEstimate EstimateRandomAttachments(
        int requestedSizeMb, int maxAttachments, int maxConcurrency)
    {
        var available = GetAvailableMemory();
        var concurrency = Math.Clamp(maxConcurrency, 1, 20);
        var count = Math.Clamp(maxAttachments, 1, 5);
        var requested = Math.Max(0, requestedSizeMb) * 1024L * 1024L;
        var safeBudget = Math.Min(RandomAttachmentSafetyHardCap,
            Math.Max(1L * 1024 * 1024, (long)(available * RandomAttachmentSafetyFraction)));
        safeBudget = Math.Min(safeBudget, Math.Max(1L, available));

        if (requestedSizeMb == 0)
        {
            // Auto keeps the estimated MIME-expanded in-flight payload within the computed safety budget,
            // with a 128 MiB per-attachment cap. The budget is bounded by available physical RAM.
            const double transientFactor = 2.37; // raw byte[] + MIME/base64 working copy
            var divisor = Math.Max(1.0, concurrency * count * transientFactor);
            var per = Math.Max(1L * 1024 * 1024,
                Math.Min(128L * 1024 * 1024, (long)(safeBudget / divisor)));
            var worst = checked(per * count * concurrency);
            var mime = (long)Math.Ceiling(worst * 1.37);
            var transient = checked(worst + mime);
            var safeAuto = transient <= safeBudget;
            // If even the 1 MiB minimum cannot fit, fail closed rather than silently
            // returning an unsafe auto value.
            if (!safeAuto)
            {
                return new RandomAttachmentSafetyEstimate(available, safeBudget, per, worst, mime, transient,
                    false, true,
                    $"Auto: ani minimální bezpečná konfigurace se nevejde do rozpočtu {FormatBytes(safeBudget)}. " +
                    $"Odhad zahrnuje raw přílohy + MIME/base64 pracovní kopii.");
            }
            return new RandomAttachmentSafetyEstimate(available, safeBudget, per, worst, mime, transient,
                true, false,
                $"Auto: cca {FormatBytes(per)} na jednu přílohu; výpočet počítá {count} příloh × {concurrency} workerů + raw data a ~37 % MIME/base64 pracovní kopii. " +
                $"Celkový odhad špičky {FormatBytes(transient)}, bezpečný rozpočet {FormatBytes(safeBudget)}.");
        }

        var worstCase = checked(requested * count * concurrency);
        var estimatedMime = (long)Math.Ceiling(worstCase * 1.37);
        var estimatedTransient = checked(worstCase + estimatedMime);
        var safe = estimatedTransient <= safeBudget && requested <= 128L * 1024 * 1024;
        var reason = safe
            ? $"Bezpečné: {FormatBytes(requested)} × {count} příloh × {concurrency} workerů = raw {FormatBytes(worstCase)} + ~37 % MIME/base64 pracovní kopie = celkem ~{FormatBytes(estimatedTransient)}; limit {FormatBytes(safeBudget)}."
            : $"BLOKOVÁNO: odhad špičky raw {FormatBytes(worstCase)} + MIME/base64 {FormatBytes(estimatedMime)} = ~{FormatBytes(estimatedTransient)} překračuje bezpečný rozpočet {FormatBytes(safeBudget)} nebo 128 MiB na jednu generovanou přílohu.";
        return new RandomAttachmentSafetyEstimate(available, safeBudget, requested, worstCase, estimatedMime, estimatedTransient, safe, false, reason);
    }

    public static int ResolveRandomAttachmentSizeMb(int requestedSizeMb, int maxAttachments, int maxConcurrency)
    {
        var estimate = EstimateRandomAttachments(requestedSizeMb, maxAttachments, maxConcurrency);
        if (!estimate.IsSafe)
            throw new InvalidOperationException(estimate.Explanation);
        if (requestedSizeMb > 0) return requestedSizeMb;
        return Math.Max(1, (int)(estimate.RequestedPerAttachmentBytes / (1024L * 1024L)));
    }

    public static string ExplainRandomAttachmentSafety(int requestedSizeMb, int maxAttachments, int maxConcurrency)
        => EstimateRandomAttachments(requestedSizeMb, maxAttachments, maxConcurrency).Explanation;

    public static AttachmentPlan Prepare(IReadOnlyList<string> paths, int maxConcurrency = 1)
    {
        var files = new List<(string Name, string Path, long Length)>();
        long total = 0;

        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                throw new FileNotFoundException($"Příloha neexistuje: {path}", path);
            if (info.Length < 0)
                throw new IOException($"Nelze zjistit velikost přílohy: {path}");

            checked { total += info.Length; }
            files.Add((info.Name, info.FullName, info.Length));
        }

        if (files.Count == 0)
        {
            var emptyAvailable = GetAvailableMemory();
            var emptyBudget = Math.Min(MaxPreloadBytes, Math.Max(MinPreloadBudget, emptyAvailable / 10));
            return new AttachmentPlan(Array.Empty<AttachmentSource>(), 0, emptyAvailable, emptyBudget, true, "Bez příloh.");
        }

        var available = GetAvailableMemory();
        var concurrency = Math.Clamp(maxConcurrency, 1, 20);
        var budget = Math.Min(MaxPreloadBytes, Math.Max(MinPreloadBudget, available / 10));

        var encodingFactor = 1.5;
        // Do not allow a huge user-supplied attachment set to overflow the
        // peak-footprint estimate into a negative/small value. Such an overflow
        // could incorrectly make `preload` true and lead to a large RAM allocation
        // despite the safety check. Saturate at long.MaxValue instead.
        var workerFactor = Math.Min(concurrency, 4);
        var estimatedPeak = SaturatingMultiply(total, workerFactor, encodingFactor);
        var preload = total <= budget && estimatedPeak <= available / 4;
        var sources = new List<AttachmentSource>(files.Count);

        if (preload)
        {
            foreach (var file in files)
            {
                // Use async read for large files to avoid blocking thread pool.
                if (file.Length > int.MaxValue)
                    throw new IOException($"Příloha je příliš velká pro bezpečné přednačtení do jednoho byte[]: {file.Path}");

                byte[] data;
                if (file.Length > 10 * 1024 * 1024)
                {
                    using var fs = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                    data = new byte[(int)file.Length];
                    fs.ReadExactly(data);
                }
                else
                {
                    data = File.ReadAllBytes(file.Path);
                }
                sources.Add(new AttachmentSource(file.Name, file.Path, data, file.Length));
            }

            return new AttachmentPlan(sources, total, available, budget, true,
                $"Přílohy přednačteny do RAM: {FormatBytes(total)} / rozpočet {FormatBytes(budget)}, " +
                $"odhadovaný špičkový footprint {FormatBytes((long)Math.Min(long.MaxValue, estimatedPeak))}, dostupná RAM {FormatBytes(available)}.");
        }

        foreach (var file in files)
            sources.Add(new AttachmentSource(file.Name, file.Path, null, file.Length));

        return new AttachmentPlan(sources, total, available, budget, false,
            $"Přílohy budou čteny ze souborů průběžně: {FormatBytes(total)} celkem, " +
            $"odhadovaný špičkový footprint {FormatBytes((long)Math.Min(long.MaxValue, estimatedPeak))}, " +
            $"dostupná RAM {FormatBytes(available)}, rozpočet pro preload {FormatBytes(budget)}.");
    }


    private static long SaturatingMultiply(long value, int multiplier, double factor)
    {
        if (value <= 0 || multiplier <= 0 || factor <= 0) return 0;
        var scaled = value * (double)multiplier * factor;
        if (double.IsNaN(scaled) || scaled >= long.MaxValue) return long.MaxValue;
        return (long)Math.Ceiling(scaled);
    }

    private static long GetAvailableMemory()
    {
        // On Windows use the actual currently available physical RAM. This is much safer
        // for a desktop load tester than relying only on the .NET GC heap limit.
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var status = new NativeMemoryStatus();
                status.Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMemoryStatus>();
                if (GlobalMemoryStatusEx(ref status) && status.AvailablePhysicalMemory > 0)
                    return (long)status.AvailablePhysicalMemory;
            }
        }
        catch { }

        try
        {
            var gc = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            if (gc > 0) return gc;
        }
        catch { }

        return Math.Min(2L * 1024 * 1024 * 1024, Math.Max(512L * 1024 * 1024, GC.GetTotalMemory(false) * 2));
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeMemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysicalMemory;
        public ulong AvailablePhysicalMemory;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref NativeMemoryStatus lpBuffer);

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024d:F1} KiB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024d / 1024d:F1} MiB";
        return $"{bytes / 1024d / 1024d / 1024d:F2} GiB";
    }
}
