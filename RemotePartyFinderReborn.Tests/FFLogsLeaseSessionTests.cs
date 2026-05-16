using System.Collections.Generic;
using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class FFLogsLeaseSessionTests
{
    static FFLogsLeaseSessionTests()
    {
        FFLogsTestAssemblyResolver.Register();
    }

    [Fact]
    public void Lease_session_retains_the_chosen_upload_url_and_leased_jobs_together()
    {
        var uploadUrl = new UploadUrl("https://ingest-a.example/");
        var jobs = new List<ParseJob>
        {
            CreateJob(contentId: 1001, leaseToken: "lease-a"),
            CreateJob(contentId: 1002, leaseToken: "lease-b"),
        };

        var session = new FFLogsLeaseSession(uploadUrl, jobs);

        Assert.Same(uploadUrl, session.UploadUrl);
        Assert.Collection(
            session.Jobs,
            job => Assert.Equal("lease-a", job.LeaseToken),
            job => Assert.Equal("lease-b", job.LeaseToken));
    }

    [Fact]
    public void Lease_session_retains_base_delay_choice_for_empty_work_poll()
    {
        var uploadUrl = new UploadUrl("https://ingest-a.example/");
        var session = new FFLogsLeaseSession(uploadUrl, [], useBaseDelayWhenNoWork: true);

        Assert.Same(uploadUrl, session.UploadUrl);
        Assert.False(session.HasJobs);
        Assert.True(session.UseBaseDelayWhenNoWork);
    }

    private static ParseJob CreateJob(ulong contentId, string leaseToken)
    {
        return new ParseJob
        {
            ContentId = contentId,
            ZoneId = 77,
            DifficultyId = 5,
            LeaseToken = leaseToken,
            Name = $"Player {contentId}",
            Server = "Alpha",
        };
    }
}
