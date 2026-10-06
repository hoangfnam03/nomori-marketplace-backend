using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nomori.Marketplace.Api.Modules.Catalog;
using Nomori.Marketplace.Core.Jobs;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Web.Framework.Security;

namespace Nomori.Marketplace.Api.Modules.Jobs;

public sealed record JobResponse(
    string Name, string Description, bool Enabled, int IntervalMinutes, int DefaultIntervalMinutes, DateTime? NextRunUtc,
    DateTime? LastStartedUtc, DateTime? LastFinishedUtc, string? LastStatus, string? LastMessage, bool Running)
{
    public static JobResponse From(JobView job) => new(
        job.Name, job.Description, job.Enabled, job.IntervalMinutes, job.DefaultIntervalMinutes, job.NextRunUtc,
        job.LastStartedUtc, job.LastFinishedUtc, job.LastStatus, job.LastMessage, job.Running);
}

public sealed record JobRunResponse(
    long Id, string Job, string Trigger, DateTime StartedUtc, DateTime FinishedUtc, string Status, int Processed, int Failed, string? Message)
{
    public static JobRunResponse From(ScheduledTaskRun run) =>
        new(run.Id, run.TaskName, run.Trigger, run.StartedUtc, run.FinishedUtc, run.Status, run.Processed, run.Failed, run.Message);
}

public sealed record UpdateJobRequest(bool Enabled, int IntervalMinutes);

[ApiController]
[Route("api/v1/admin/jobs")]
[Authorize]
[HasPermission(PermissionCodes.JobsManage)]
public sealed class AdminJobsController(IJobService jobService, ICurrentUser currentUser) : ControllerBase
{
    private int ActorId() => int.TryParse(currentUser.Subject, out var customerId) ? customerId : 0;

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        Ok((await jobService.GetJobsAsync(cancellationToken)).Select(JobResponse.From));

    [HttpPut("{name}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(string name, UpdateJobRequest request, CancellationToken cancellationToken)
    {
        var result = await jobService.UpdateAsync(name, request.Enabled, request.IntervalMinutes, ActorId(), cancellationToken);
        return result.Succeeded ? Ok(JobResponse.From(result.Value!)) : this.ToFailure(result);
    }

    [HttpPost("{name}/run")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Run(string name, CancellationToken cancellationToken)
    {
        var result = await jobService.RunNowAsync(name, ActorId(), cancellationToken);
        return result.Succeeded ? Ok(JobRunResponse.From(result.Value!)) : this.ToFailure(result);
    }

    [HttpGet("{name}/runs")]
    public async Task<IActionResult> Runs(string name, CancellationToken cancellationToken)
    {
        var result = await jobService.GetRunsAsync(name, cancellationToken);
        return result.Succeeded ? Ok(result.Value!.Select(JobRunResponse.From)) : this.ToFailure(result);
    }
}
