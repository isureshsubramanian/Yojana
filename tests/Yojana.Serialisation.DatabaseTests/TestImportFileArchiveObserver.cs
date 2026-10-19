using Yojana.Serialisation.Imports;

namespace Yojana.Serialisation.DatabaseTests;

public sealed class TestImportFileArchiveObserver : IImportFileArchiveObserver
{
    private Guid? jobId;
    private string? stage;

    public void FailOnce(Guid job, string atStage)
    {
        jobId = job;
        stage = atStage;
    }

    public Task CopiedAsync(Guid jobId, string copyKind, CancellationToken cancellationToken) => Check(jobId, "copied");
    public Task RepointedAsync(Guid jobId, string copyKind, CancellationToken cancellationToken) => Check(jobId, "repointed");

    private Task Check(Guid job, string currentStage)
    {
        if (jobId == job && stage == currentStage)
        {
            jobId = null; stage = null;
            throw new InvalidOperationException("simulated archive interruption");
        }
        return Task.CompletedTask;
    }
}
