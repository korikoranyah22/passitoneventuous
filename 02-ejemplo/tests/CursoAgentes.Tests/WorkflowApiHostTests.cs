using CursoAgentes.Api;
using CursoAgentes.Domain.Workflow;
using CursoAgentes.Engine.Coordination;
using CursoAgentes.Engine.Workflow;
using CursoAgentes.Infrastructure.Projections;
using Microsoft.Extensions.Logging.Abstractions;

namespace CursoAgentes.Tests;

public sealed class WorkflowApiHostTests
{
    [Fact]
    public async Task Queue_deduplicates_a_run_until_it_finishes()
    {
        var queue = new WorkflowRunQueue();

        Assert.True(await queue.ScheduleAsync("run-1"));
        Assert.False(await queue.ScheduleAsync("run-1"));
        Assert.True(queue.IsScheduled("run-1"));

        queue.MarkFinished("run-1");
        Assert.True(await queue.ScheduleAsync("run-1"));
    }

    [Fact]
    public async Task Worker_executes_queued_runs_and_releases_the_deduplication_key()
    {
        var queue = new WorkflowRunQueue();
        var executor = new RecordingExecutor();
        var worker = new WorkflowRunWorker(
            queue,
            executor,
            NullLogger<WorkflowRunWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await queue.ScheduleAsync("run-2");
            Assert.Equal("run-2", await executor.NextCall.Task.WaitAsync(TimeSpan.FromSeconds(2)));
            await WaitUntilAsync(() => !queue.IsScheduled("run-2"));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Worker_continues_with_the_next_run_after_one_execution_fails()
    {
        var queue = new WorkflowRunQueue();
        var executor = new FailFirstExecutor();
        var worker = new WorkflowRunWorker(
            queue,
            executor,
            NullLogger<WorkflowRunWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await queue.ScheduleAsync("run-fails");
            await queue.ScheduleAsync("run-succeeds");

            Assert.Equal(
                "run-succeeds",
                await executor.Success.Task.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(["run-fails", "run-succeeds"], executor.Calls);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void Tree_builder_restores_hierarchy_and_sibling_order()
    {
        WorkflowNodeReadModel[] nodes =
        [
            Node("child-b", "root", depth: 1, order: 1),
            Node("root", parent: null, depth: 0, order: 0),
            Node("grandchild", "child-a", depth: 2, order: 0),
            Node("child-a", "root", depth: 1, order: 0),
        ];

        var tree = WorkflowTreeBuilder.Build("root", nodes);

        Assert.NotNull(tree);
        Assert.Equal(["child-a", "child-b"], tree.Children.Select(child => child.NodeId));
        Assert.Equal("grandchild", tree.Children[0].Children.Single().NodeId);
    }

    [Fact]
    public void Tree_builder_returns_null_while_the_root_projection_is_not_available()
    {
        Assert.Null(WorkflowTreeBuilder.Build("missing", []));
    }

    [Fact]
    public async Task Acceptance_with_same_idempotency_key_returns_the_same_run()
    {
        var store = new FakeAcceptanceStore();
        var service = new WorkflowAcceptanceService(store, new WorkflowRunQueue());

        var first = await service.AcceptAsync(
            "same goal", startImmediately: false, "client-request-1", CancellationToken.None);
        var replay = await service.AcceptAsync(
            "same goal", startImmediately: false, "client-request-1", CancellationToken.None);

        Assert.True(first.WasCreated);
        Assert.False(replay.WasCreated);
        Assert.Equal(first.Run.RunId, replay.Run.RunId);
        Assert.Equal(1, store.StartCalls);
    }

    [Fact]
    public async Task Acceptance_rejects_reusing_a_key_with_a_different_goal()
    {
        var service = new WorkflowAcceptanceService(
            new FakeAcceptanceStore(),
            new WorkflowRunQueue());
        await service.AcceptAsync(
            "first goal", startImmediately: false, "client-request-2", CancellationToken.None);

        var error = await Assert.ThrowsAsync<WorkflowAcceptanceConflictException>(() =>
            service.AcceptAsync(
                "different goal",
                startImmediately: false,
                "client-request-2",
                CancellationToken.None));

        Assert.Contains("different goal", error.Message);
    }

    [Fact]
    public async Task Immediate_acceptance_persists_request_before_local_scheduling()
    {
        var store = new FakeAcceptanceStore();
        var queue = new WorkflowRunQueue();
        var service = new WorkflowAcceptanceService(store, queue);

        var accepted = await service.AcceptAsync(
            "execute me", startImmediately: true, "client-request-3", CancellationToken.None);

        Assert.True(accepted.Run.ExecutionRequested);
        Assert.Equal(1, store.RequestCalls);
        Assert.True(accepted.Scheduled);
        Assert.True(queue.IsScheduled(accepted.Run.RunId));
    }

    [Fact]
    public async Task Recovery_scanner_enqueues_only_candidates_reported_as_pending()
    {
        var queue = new WorkflowRunQueue();
        var scanner = new WorkflowRecoveryScanner(
            new FixedRecoverySource(["run-recover"]),
            queue,
            new WorkflowRecoveryOptions { ScanInterval = TimeSpan.FromMilliseconds(10) },
            NullLogger<WorkflowRecoveryScanner>.Instance);

        await scanner.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => queue.IsScheduled("run-recover"));
        }
        finally
        {
            await scanner.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Two_executors_sharing_a_lease_store_only_run_once()
    {
        var leases = new InMemoryExecutionLeaseStore();
        var resumer = new BlockingResumer();
        var options = LeaseOptions();
        var first = LeaseExecutor("instance-a", leases, resumer, options);
        var second = LeaseExecutor("instance-b", leases, resumer, options);

        var firstExecution = first.TryExecuteAsync("run-shared", CancellationToken.None);
        await resumer.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(await second.TryExecuteAsync("run-shared", CancellationToken.None));

        resumer.AllowCompletion.TrySetResult();
        Assert.True(await firstExecution);
        Assert.Equal(1, resumer.Calls);
    }

    [Fact]
    public async Task Expired_lease_can_be_claimed_with_a_higher_fencing_token()
    {
        var leases = new InMemoryExecutionLeaseStore();
        var first = await leases.TryAcquireAsync(
            "resource-1", "instance-a", TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.NotNull(first);
        Assert.Equal(first, await leases.GetActiveAsync("resource-1", CancellationToken.None));
        Assert.Null(await leases.TryAcquireAsync(
            "resource-1", "instance-b", TimeSpan.FromSeconds(10), CancellationToken.None));

        leases.Advance(TimeSpan.FromSeconds(11));
        var second = await leases.TryAcquireAsync(
            "resource-1", "instance-b", TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.NotNull(second);
        Assert.True(second.LeaseToken > first.LeaseToken);
        Assert.Equal(second, await leases.GetActiveAsync("resource-1", CancellationToken.None));
        Assert.Null(await leases.RenewAsync(
            first, TimeSpan.FromSeconds(10), CancellationToken.None));
        Assert.False(await leases.ReleaseAsync(first, CancellationToken.None));
    }

    [Fact]
    public async Task Losing_a_lease_cancels_the_local_execution()
    {
        var leases = new InMemoryExecutionLeaseStore { RejectRenewals = true };
        var resumer = new CancellationAwareResumer();
        var executor = LeaseExecutor(
            "instance-a",
            leases,
            resumer,
            new WorkflowExecutionLeaseOptions
            {
                Duration = TimeSpan.FromMilliseconds(100),
                RenewInterval = TimeSpan.FromMilliseconds(10)
            });

        await Assert.ThrowsAsync<WorkflowExecutionLeaseLostException>(() =>
            executor.TryExecuteAsync("run-lost", CancellationToken.None));
        Assert.True(await resumer.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    private static WorkflowNodeReadModel Node(
        string id,
        string? parent,
        int depth,
        int order) => new(
            id,
            "run-1",
            parent,
            depth,
            order,
            $"goal-{id}",
            "Completed",
            IsLeaf: depth == 2,
            "test",
            $"answer-{id}");

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException();
            await Task.Delay(10);
        }
    }

    private static WorkflowExecutionLeaseOptions LeaseOptions() => new()
    {
        Duration = TimeSpan.FromSeconds(10),
        RenewInterval = TimeSpan.FromSeconds(1)
    };

    private static WorkflowRunLeaseExecutor LeaseExecutor(
        string ownerId,
        IExecutionLeaseStore leases,
        IWorkflowRunResumer resumer,
        WorkflowExecutionLeaseOptions options) => new(
            resumer,
            leases,
            new ExecutionLeaseOwner(ownerId),
            options,
            NullLogger<WorkflowRunLeaseExecutor>.Instance);

    private sealed class RecordingExecutor : IWorkflowRunExecutor
    {
        public TaskCompletionSource<string> NextCall { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> TryExecuteAsync(string runId, CancellationToken ct)
        {
            NextCall.TrySetResult(runId);
            return Task.FromResult(true);
        }
    }

    private sealed class FailFirstExecutor : IWorkflowRunExecutor
    {
        public List<string> Calls { get; } = [];
        public TaskCompletionSource<string> Success { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> TryExecuteAsync(string runId, CancellationToken ct)
        {
            Calls.Add(runId);
            if (Calls.Count == 1) throw new InvalidOperationException("simulated");
            Success.TrySetResult(runId);
            return Task.FromResult(true);
        }
    }

    private sealed class BlockingResumer : IWorkflowRunResumer
    {
        public int Calls { get; private set; }
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ResumeAsync(string runId, CancellationToken ct)
        {
            Calls++;
            Started.TrySetResult();
            await AllowCompletion.Task.WaitAsync(ct);
        }
    }

    private sealed class CancellationAwareResumer : IWorkflowRunResumer
    {
        public TaskCompletionSource<bool> Cancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ResumeAsync(string runId, CancellationToken ct)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult(true);
                throw;
            }
        }
    }

    private sealed class InMemoryExecutionLeaseStore : IExecutionLeaseStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, ExecutionLease> _leases = [];
        private DateTimeOffset _now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

        public bool RejectRenewals { get; init; }

        public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<ExecutionLease?> TryAcquireAsync(
            string resourceId,
            string ownerId,
            TimeSpan duration,
            CancellationToken ct)
        {
            lock (_gate)
            {
                _leases.TryGetValue(resourceId, out var current);
                if (current is not null && current.ExpiresAt > _now && current.OwnerId != ownerId)
                    return Task.FromResult<ExecutionLease?>(null);

                var token = current is null
                    ? 1
                    : current.OwnerId == ownerId
                        ? current.LeaseToken
                        : current.LeaseToken + 1;
                var acquired = new ExecutionLease(resourceId, ownerId, token, _now + duration);
                _leases[resourceId] = acquired;
                return Task.FromResult<ExecutionLease?>(acquired);
            }
        }

        public Task<ExecutionLease?> RenewAsync(
            ExecutionLease lease,
            TimeSpan duration,
            CancellationToken ct)
        {
            lock (_gate)
            {
                if (RejectRenewals ||
                    !_leases.TryGetValue(lease.ResourceId, out var current) ||
                    current.OwnerId != lease.OwnerId ||
                    current.LeaseToken != lease.LeaseToken ||
                    current.ExpiresAt <= _now)
                {
                    return Task.FromResult<ExecutionLease?>(null);
                }

                var renewed = current with { ExpiresAt = _now + duration };
                _leases[lease.ResourceId] = renewed;
                return Task.FromResult<ExecutionLease?>(renewed);
            }
        }

        public Task<ExecutionLease?> GetActiveAsync(string resourceId, CancellationToken ct)
        {
            lock (_gate)
            {
                return Task.FromResult(
                    _leases.TryGetValue(resourceId, out var current) && current.ExpiresAt > _now
                        ? current
                        : null);
            }
        }

        public Task<bool> ReleaseAsync(ExecutionLease lease, CancellationToken ct)
        {
            lock (_gate)
            {
                if (!_leases.TryGetValue(lease.ResourceId, out var current) ||
                    current.OwnerId != lease.OwnerId ||
                    current.LeaseToken != lease.LeaseToken)
                {
                    return Task.FromResult(false);
                }

                _leases[lease.ResourceId] = current with { ExpiresAt = _now };
                return Task.FromResult(true);
            }
        }

        public void Advance(TimeSpan duration)
        {
            lock (_gate) _now += duration;
        }
    }

    private sealed class FakeAcceptanceStore : IWorkflowAcceptanceStore
    {
        private readonly Dictionary<string, WorkflowRunState> _runs = [];

        public int StartCalls { get; private set; }
        public int RequestCalls { get; private set; }

        public Task<WorkflowRunState?> ReadAsync(string runId, CancellationToken ct) =>
            Task.FromResult(_runs.GetValueOrDefault(runId));

        public Task<WorkflowRunHandle> StartAsync(
            string runId,
            string goal,
            CancellationToken ct)
        {
            StartCalls++;
            var rootNodeId = $"root-{runId}";
            var created = new WorkflowRunEvents.V1.WorkflowRunCreated(
                runId,
                goal,
                rootNodeId,
                DateTime.UtcNow.ToString("O"));
            _runs.Add(runId, new WorkflowRunState().When(created));
            return Task.FromResult(new WorkflowRunHandle(runId, rootNodeId, goal));
        }

        public Task<WorkflowRunState> RequestExecutionAsync(
            string runId,
            string requestId,
            CancellationToken ct)
        {
            RequestCalls++;
            var state = _runs[runId];
            if (!state.ExecutionRequested)
            {
                state = state.When(new WorkflowRunEvents.V1.WorkflowRunExecutionRequested(
                    runId,
                    requestId,
                    DateTime.UtcNow.ToString("O")));
                _runs[runId] = state;
            }
            return Task.FromResult(state);
        }
    }

    private sealed class FixedRecoverySource(IReadOnlyList<string> runIds)
        : IWorkflowRecoverySource
    {
        public Task<IReadOnlyList<string>> FindPendingAsync(CancellationToken ct) =>
            Task.FromResult(runIds);
    }
}
