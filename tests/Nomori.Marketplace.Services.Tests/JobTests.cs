using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Jobs;
using Nomori.Marketplace.Core.Time;
using Nomori.Marketplace.Services.Jobs;

namespace Nomori.Marketplace.Services.Tests;

public sealed class JobTests
{
    private static readonly DateTime Start = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
    private const int Admin = 1;

    private sealed class JobClock : IClock
    {
        public DateTime UtcNow { get; set; } = Start;
    }

    /// <summary>The lease works as the SQL statement does: the same rules, in memory.</summary>
    private sealed class FakeJobStore : IJobStore
    {
        public Dictionary<string, ScheduledTask> Tasks { get; } = new();
        public Dictionary<string, string?> Owners { get; } = new();
        public List<ScheduledTaskRun> Runs { get; } = [];

        public Task EnsureAsync(IReadOnlyCollection<JobDefinition> definitions, DateTime nowUtc, CancellationToken cancellationToken)
        {
            foreach (var d in definitions)
                Tasks.TryAdd(d.Name, new ScheduledTask { SystemName = d.Name, Enabled = true, IntervalMinutes = d.DefaultIntervalMinutes });
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ScheduledTask>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ScheduledTask>>(Tasks.Values.OrderBy(t => t.SystemName).ToList());

        public Task<ScheduledTask?> GetAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult(Tasks.GetValueOrDefault(name));

        public Task<bool> UpdateAsync(string name, bool enabled, int intervalMinutes, DateTime nowUtc, CancellationToken cancellationToken)
        {
            if (!Tasks.TryGetValue(name, out var task)) return Task.FromResult(false);
            (task.Enabled, task.IntervalMinutes) = (enabled, intervalMinutes);
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<string>> DueNamesAsync(DateTime nowUtc, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(Tasks.Values
                .Where(t => t.Enabled && (t.NextRunUtc is null || t.NextRunUtc <= nowUtc) && (t.LockedUntilUtc is null || t.LockedUntilUtc <= nowUtc))
                .Select(t => t.SystemName).OrderBy(n => n).ToList());

        public Task<bool> TryClaimAsync(string name, string owner, DateTime nowUtc, DateTime leaseUntilUtc, bool force, CancellationToken cancellationToken)
        {
            if (!Tasks.TryGetValue(name, out var task)) return Task.FromResult(false);
            if (task.LockedUntilUtc is not null && task.LockedUntilUtc > nowUtc) return Task.FromResult(false);
            if (!force && !(task.Enabled && (task.NextRunUtc is null || task.NextRunUtc <= nowUtc))) return Task.FromResult(false);
            (task.LockedUntilUtc, task.LastStartedUtc, task.LastStatus) = (leaseUntilUtc, nowUtc, JobStatuses.Running);
            Owners[name] = owner;
            return Task.FromResult(true);
        }

        public Task CompleteAsync(string name, string owner, JobFinish finish, CancellationToken cancellationToken)
        {
            var task = Tasks[name];
            if (Owners.GetValueOrDefault(name) != owner) return Task.CompletedTask;
            (task.LockedUntilUtc, task.LastFinishedUtc, task.NextRunUtc, task.LastStatus, task.LastMessage) =
                (null, finish.FinishedUtc, finish.NextRunUtc, finish.Status, finish.Message);
            Owners[name] = null;
            return Task.CompletedTask;
        }

        public Task AddRunAsync(ScheduledTaskRun run, CancellationToken cancellationToken)
        {
            Runs.Add(run);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ScheduledTaskRun>> GetRunsAsync(string name, int take, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ScheduledTaskRun>>(Runs.Where(r => r.TaskName == name).OrderByDescending(r => r.StartedUtc).Take(take).ToList());
    }

    private sealed class ScriptedJob(string name, Func<CancellationToken, Task<JobResult>> body, int interval = 10) : IScheduledJob
    {
        public int Runs { get; private set; }
        public string Name => name;
        public string Description => "A job for tests.";
        public int DefaultIntervalMinutes => interval;

        public Task<JobResult> RunAsync(CancellationToken cancellationToken)
        {
            Runs++;
            return body(cancellationToken);
        }
    }

    private sealed class Fixture
    {
        public FakeJobStore Store { get; } = new();
        public RecordingAuditLog Audit { get; } = new();
        public JobClock Clock { get; } = new();
        public List<ScriptedJob> Jobs { get; } = [];

        public ScriptedJob Add(string name, Func<CancellationToken, Task<JobResult>>? body = null, int interval = 10)
        {
            var job = new ScriptedJob(name, body ?? (_ => Task.FromResult(new JobResult(1))), interval);
            Jobs.Add(job);
            return job;
        }

        public JobService Service() =>
            new(Store, Jobs, Audit, Clock, Options.Create(new JobOptions { LeaseMinutes = 10 }), NullLogger<JobService>.Instance);
    }

    // ---- Rules ----

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(10_080, true)]
    [InlineData(10_081, false)]
    [InlineData(-5, false)]
    public void IntervalMustBeBetweenOneMinuteAndOneWeek(int minutes, bool valid) =>
        Assert.Equal(valid, JobRules.IsValidInterval(minutes));

    [Fact]
    public void StatusSaysWhetherEverythingWorkedSomethingWorkedOrNothingDid()
    {
        Assert.Equal(JobStatuses.Succeeded, JobRules.StatusOf(new JobResult(5)));
        Assert.Equal(JobStatuses.Succeeded, JobRules.StatusOf(new JobResult(0)));
        Assert.Equal(JobStatuses.Partial, JobRules.StatusOf(new JobResult(3, 1)));
        Assert.Equal(JobStatuses.Failed, JobRules.StatusOf(new JobResult(0, 2)));
    }

    [Fact]
    public void NotesAreOneShortLineAndEmptyNotesAreNull()
    {
        Assert.Null(JobRules.Trim(null));
        Assert.Null(JobRules.Trim("  \r\n "));
        Assert.Equal("a b", JobRules.Trim("a\r\nb"));
        var trimmed = JobRules.Trim(new string('x', 900))!;
        Assert.Equal(JobRules.MaxMessageLength, trimmed.Length);
        Assert.EndsWith("…", trimmed);
    }

    [Fact]
    public void AFailureNoteHasTheExceptionTypeAndMessageButNoStackTrace()
    {
        Exception thrown;
        try { throw new InvalidOperationException("boom"); } catch (Exception ex) { thrown = ex; }
        var note = JobRules.DescribeFailure(thrown);
        Assert.Equal("InvalidOperationException: boom", note);
        Assert.DoesNotContain("   at ", note);
    }

    [Fact]
    public void NextRunIsTheEndOfTheRunPlusTheInterval() =>
        Assert.Equal(Start.AddMinutes(15), JobRules.NextRun(Start, 15));

    // ---- Runner ----

    [Fact]
    public async Task ANewJobIsDueAtOnceAndThenWaitsForItsInterval()
    {
        var fixture = new Fixture();
        var job = fixture.Add("a.job", interval: 10);
        var service = fixture.Service();

        Assert.Equal(1, await service.RunDueAsync(default));
        Assert.Equal(1, job.Runs);
        Assert.Equal(Start.AddMinutes(10), fixture.Store.Tasks["a.job"].NextRunUtc);

        fixture.Clock.UtcNow = Start.AddMinutes(9);
        Assert.Equal(0, await service.RunDueAsync(default));

        fixture.Clock.UtcNow = Start.AddMinutes(10);
        Assert.Equal(1, await service.RunDueAsync(default));
        Assert.Equal(2, job.Runs);
    }

    [Fact]
    public async Task OnlyEnabledJobsRunOnSchedule()
    {
        var fixture = new Fixture();
        var on = fixture.Add("a.on");
        var off = fixture.Add("b.off");
        var service = fixture.Service();
        await service.GetJobsAsync(default);
        fixture.Store.Tasks["b.off"].Enabled = false;

        Assert.Equal(1, await service.RunDueAsync(default));
        Assert.Equal(1, on.Runs);
        Assert.Equal(0, off.Runs);
    }

    [Fact]
    public async Task AJobThatIsLockedBySomeoneElseIsSkippedAndAManualRunIsRefused()
    {
        var fixture = new Fixture();
        var job = fixture.Add("a.job");
        var service = fixture.Service();
        await service.GetJobsAsync(default);
        fixture.Store.Tasks["a.job"].LockedUntilUtc = Start.AddMinutes(5);

        Assert.Equal(0, await service.RunDueAsync(default));
        var manual = await service.RunNowAsync("a.job", Admin, default);

        Assert.Equal(JobErrors.AlreadyRunning, manual.ErrorCode);
        Assert.Equal(0, job.Runs);
        Assert.Empty(fixture.Store.Runs);
        Assert.Empty(fixture.Audit.Entries);
    }

    [Fact]
    public async Task ALeaseThatHasExpiredFreesTheJobForTheNextRun()
    {
        var fixture = new Fixture();
        var job = fixture.Add("a.job");
        var service = fixture.Service();
        await service.GetJobsAsync(default);
        // A node died while running the job: its lease ends in the past.
        fixture.Store.Tasks["a.job"].LockedUntilUtc = Start.AddMinutes(-1);

        Assert.Equal(1, await service.RunDueAsync(default));
        Assert.Equal(1, job.Runs);
    }

    [Fact]
    public async Task AManualRunIgnoresOffAndNotDueButIsRecordedAndAudited()
    {
        var fixture = new Fixture();
        var job = fixture.Add("a.job", _ => Task.FromResult(new JobResult(7, 0, "Done seven.")));
        var service = fixture.Service();
        await service.GetJobsAsync(default);
        fixture.Store.Tasks["a.job"].Enabled = false;
        fixture.Store.Tasks["a.job"].NextRunUtc = Start.AddDays(1);

        var result = await service.RunNowAsync("a.job", Admin, default);

        Assert.True(result.Succeeded);
        Assert.Equal(1, job.Runs);
        Assert.Equal((JobTriggers.Manual, JobStatuses.Succeeded, 7), (result.Value!.Trigger, result.Value.Status, result.Value.Processed));
        Assert.Equal("Done seven.", result.Value.Message);
        Assert.Single(fixture.Store.Runs);
        var entry = Assert.Single(fixture.Audit.Entries);
        Assert.Equal(("job.run_manual", (int?)Admin), (entry.Event, entry.CustomerId));
    }

    [Fact]
    public async Task AScheduledRunIsRecordedAsScheduledAndWritesNoAuditRow()
    {
        var fixture = new Fixture();
        fixture.Add("a.job");

        await fixture.Service().RunDueAsync(default);

        Assert.Equal(JobTriggers.Schedule, Assert.Single(fixture.Store.Runs).Trigger);
        Assert.Empty(fixture.Audit.Entries);
    }

    [Fact]
    public async Task AJobThatThrowsIsRecordedAsFailedFreesItsLockAndDoesNotStopTheOthers()
    {
        var fixture = new Fixture();
        fixture.Add("a.bad", _ => throw new InvalidOperationException("boom"));
        var good = fixture.Add("b.good");
        var service = fixture.Service();

        var ran = await service.RunDueAsync(default);

        Assert.Equal(2, ran);
        Assert.Equal(1, good.Runs);
        var bad = fixture.Store.Tasks["a.bad"];
        Assert.Null(bad.LockedUntilUtc);
        Assert.Equal(JobStatuses.Failed, bad.LastStatus);
        Assert.Equal("InvalidOperationException: boom", bad.LastMessage);
        Assert.Equal(1, fixture.Store.Runs.Single(r => r.TaskName == "a.bad").Failed);
        // A failed job waits for its interval like any other: no tight retry loop.
        Assert.Equal(Start.AddMinutes(10), bad.NextRunUtc);
    }

    [Theory]
    [InlineData(3, 1, JobStatuses.Partial)]
    [InlineData(0, 2, JobStatuses.Failed)]
    [InlineData(4, 0, JobStatuses.Succeeded)]
    public async Task TheRunStatusFollowsTheCountsTheJobReports(int processed, int failed, string status)
    {
        var fixture = new Fixture();
        fixture.Add("a.job", _ => Task.FromResult(new JobResult(processed, failed)));

        await fixture.Service().RunDueAsync(default);

        var run = Assert.Single(fixture.Store.Runs);
        Assert.Equal((status, processed, failed), (run.Status, run.Processed, run.Failed));
        Assert.Equal(status, fixture.Store.Tasks["a.job"].LastStatus);
    }

    [Fact]
    public async Task StoppingTheApplicationGivesTheJobBackAndLetsTheStopContinue()
    {
        var fixture = new Fixture();
        using var source = new CancellationTokenSource();
        fixture.Add("a.job", token =>
        {
            source.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new JobResult(0));
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service().RunDueAsync(source.Token));

        var task = fixture.Store.Tasks["a.job"];
        Assert.Null(task.LockedUntilUtc);
        Assert.Equal(JobStatuses.Failed, task.LastStatus);
        Assert.Single(fixture.Store.Runs);
    }

    [Fact]
    public async Task TheIntervalSetByTheAdministratorDecidesTheNextRun()
    {
        var fixture = new Fixture();
        fixture.Add("a.job", interval: 10);
        var service = fixture.Service();
        await service.GetJobsAsync(default);
        await service.UpdateAsync("a.job", true, 45, Admin, default);

        await service.RunDueAsync(default);

        Assert.Equal(Start.AddMinutes(45), fixture.Store.Tasks["a.job"].NextRunUtc);
    }

    // ---- Administration ----

    [Fact]
    public async Task TheListShowsEveryJobWithItsScheduleAndWhetherItIsRunning()
    {
        var fixture = new Fixture();
        fixture.Add("b.second", interval: 60);
        fixture.Add("a.first", interval: 5);
        var service = fixture.Service();
        await service.GetJobsAsync(default);
        fixture.Store.Tasks["b.second"].LockedUntilUtc = Start.AddMinutes(3);

        var jobs = await service.GetJobsAsync(default);

        Assert.Equal(["a.first", "b.second"], jobs.Select(j => j.Name));
        Assert.Equal((5, 5, true, false), (jobs[0].IntervalMinutes, jobs[0].DefaultIntervalMinutes, jobs[0].Enabled, jobs[0].Running));
        Assert.True(jobs[1].Running);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10_081)]
    public async Task AnIntervalOutOfRangeIsAFieldErrorAndChangesNothing(int minutes)
    {
        var fixture = new Fixture();
        fixture.Add("a.job");
        var service = fixture.Service();
        await service.GetJobsAsync(default);

        var result = await service.UpdateAsync("a.job", false, minutes, Admin, default);

        Assert.True(result.Errors.ContainsKey("intervalMinutes"));
        Assert.Equal(10, fixture.Store.Tasks["a.job"].IntervalMinutes);
        Assert.True(fixture.Store.Tasks["a.job"].Enabled);
        Assert.Empty(fixture.Audit.Entries);
    }

    [Fact]
    public async Task UpdatingAJobSavesItAndAuditsIt()
    {
        var fixture = new Fixture();
        fixture.Add("a.job");
        var service = fixture.Service();

        var result = await service.UpdateAsync("a.job", false, 30, Admin, default);

        Assert.True(result.Succeeded);
        Assert.Equal((false, 30), (result.Value!.Enabled, result.Value.IntervalMinutes));
        var entry = Assert.Single(fixture.Audit.Entries);
        Assert.Equal(("job.updated", (int?)Admin), (entry.Event, entry.CustomerId));
    }

    [Fact]
    public async Task AnUnknownJobIsNotFoundEverywhere()
    {
        var service = new Fixture().Service();

        Assert.Equal(CatalogErrors.NotFound, (await service.UpdateAsync("nope", true, 5, Admin, default)).ErrorCode);
        Assert.Equal(CatalogErrors.NotFound, (await service.RunNowAsync("nope", Admin, default)).ErrorCode);
        Assert.Equal(CatalogErrors.NotFound, (await service.GetRunsAsync("nope", default)).ErrorCode);
    }

    [Fact]
    public async Task TheRunsOfAJobAreItsOwnNewestFirst()
    {
        var fixture = new Fixture();
        fixture.Add("a.job");
        fixture.Add("b.job");
        var service = fixture.Service();
        await service.RunNowAsync("a.job", Admin, default);
        fixture.Clock.UtcNow = Start.AddMinutes(1);
        await service.RunNowAsync("a.job", Admin, default);
        await service.RunNowAsync("b.job", Admin, default);

        var runs = (await service.GetRunsAsync("a.job", default)).Value!;

        Assert.Equal(2, runs.Count);
        Assert.All(runs, r => Assert.Equal("a.job", r.TaskName));
        Assert.True(runs[0].StartedUtc > runs[1].StartedUtc);
    }

    [Fact]
    public async Task ARowWhoseJobWasRemovedFromTheCodeIsIgnored()
    {
        var fixture = new Fixture();
        fixture.Add("a.job");
        var service = fixture.Service();
        await service.GetJobsAsync(default);
        fixture.Store.Tasks["z.gone"] = new ScheduledTask { SystemName = "z.gone", Enabled = true, IntervalMinutes = 5 };

        Assert.Equal(1, await service.RunDueAsync(default));
        Assert.Equal(["a.job"], (await service.GetJobsAsync(default)).Select(j => j.Name));
    }
}
