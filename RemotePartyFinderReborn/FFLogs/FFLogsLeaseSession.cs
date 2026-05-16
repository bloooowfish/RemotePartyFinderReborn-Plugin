using System;
using System.Collections.Generic;
using System.Linq;

namespace RemotePartyFinderReborn;

internal sealed class FFLogsLeaseSession
{
    public FFLogsLeaseSession(
        UploadUrl uploadUrl,
        IEnumerable<ParseJob> jobs,
        bool useBaseDelayWhenNoWork = false)
    {
        UploadUrl = uploadUrl ?? throw new ArgumentNullException(nameof(uploadUrl));
        ArgumentNullException.ThrowIfNull(jobs);
        Jobs = jobs.ToArray();
        UseBaseDelayWhenNoWork = useBaseDelayWhenNoWork;
    }

    public UploadUrl UploadUrl { get; }

    public IReadOnlyList<ParseJob> Jobs { get; }

    public bool UseBaseDelayWhenNoWork { get; }

    public bool HasJobs
        => Jobs.Count > 0;

}
