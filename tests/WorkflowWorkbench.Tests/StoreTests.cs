using Microsoft.Data.Sqlite;
using WorkflowWorkbench;
using Xunit;

namespace WorkflowWorkbench.Tests;

public class StoreTests : IDisposable
{
    private readonly string path = Path.Combine(AppContext.BaseDirectory, $"workbench-{Guid.NewGuid():N}.db");
    private WorkbenchStore Store => new(path);
    private static DemoActor Actor(string role) => Engine.Actors.Single(actor => actor.Role == role);
    private RunView NewRun() => Store.Create("Example printer", Actor("requester"));
    private static ActionCommand Complete(RunView run, string step) => new(run.Revision, Guid.NewGuid().ToString("D"), step, "complete", "", true);
    private RunView Act(RunView run, string step, string role) => Store.Apply(run.Id, Complete(run, step), Actor(role)).Run;

    [Fact]
    public void RequestsRemainPinnedWhenANewDefinitionIsPublished()
    {
        var old = NewRun();
        var definition = Engine.Example() with { Name = "Equipment request revised" };
        definition.Steps[3] = definition.Steps[3] with { Name = "Release with new policy" };
        var version = Store.Publish(new(1, definition), Actor("coordinator"));
        Assert.Equal(2, version.Version);
        var current = NewRun();
        Assert.Equal(1, Store.Get(old.Id).DefinitionVersion);
        Assert.Equal("Ready to order", Store.Get(old.Id).Definition.Steps[3].Name);
        Assert.Equal(2, current.DefinitionVersion);
        Assert.Equal("Release with new policy", current.Definition.Steps[3].Name);
        Assert.Equal(2, Store.Versions().Length);
    }

    [Fact]
    public void StateAndHistorySurviveReopeningTheDatabase()
    {
        var run = Act(NewRun(), "intake", "requester");
        var reloaded = new WorkbenchStore(path).Get(run.Id);
        Assert.Equal(1, reloaded.Revision);
        Assert.Equal(2, reloaded.History.Length);
        Assert.Equal("active", reloaded.Steps["finance"]);
        Assert.True(reloaded.History[^1].Confirmed);
    }

    [Fact]
    public void IdenticalCommandIsReplaySafeAndChangedIntentIsRejected()
    {
        var run = NewRun();
        var command = Complete(run, "intake");
        Assert.False(Store.Apply(run.Id, command, Actor("requester")).Replayed);
        Assert.True(Store.Apply(run.Id, command, Actor("requester")).Replayed);
        Assert.Equal(409, Assert.Throws<WorkbenchException>(() => Store.Apply(run.Id, command with { Comment = "Different" }, Actor("requester"))).Status);
        Assert.Equal(403, Assert.Throws<WorkbenchException>(() => Store.Apply(run.Id, command, Actor("finance"))).Status);
        Assert.Equal(1, Store.Get(run.Id).Revision);
        Assert.Equal(2, Store.Get(run.Id).History.Length);
    }

    [Fact]
    public void WrongRoleMissingConfirmationAndStaleRevisionLeaveNoHistory()
    {
        var run = NewRun();
        Assert.Throws<WorkbenchException>(() => Store.Apply(run.Id, Complete(run, "intake"), Actor("finance")));
        Assert.Throws<WorkbenchException>(() => Store.Apply(run.Id, Complete(run, "intake") with { Confirmed = null }, Actor("requester")));
        Assert.Throws<WorkbenchException>(() => Store.Apply(run.Id, Complete(run, "intake") with { ExpectedRevision = 99 }, Actor("requester")));
        Assert.Equal(0, Store.Get(run.Id).Revision);
        Assert.Single(Store.Get(run.Id).History);
    }

    [Fact]
    public void HistoryFailureRollsBackTheAlreadyWrittenStateUpdate()
    {
        var run = NewRun();
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            db.Open();
            using var trigger = db.CreateCommand();
            trigger.CommandText = "CREATE TRIGGER fail_history BEFORE INSERT ON History WHEN NEW.action='complete' BEGIN SELECT RAISE(ABORT,'test failure'); END;";
            trigger.ExecuteNonQuery();
        }
        Assert.Throws<SqliteException>(() => Store.Apply(run.Id, Complete(run, "intake"), Actor("requester")));
        var unchanged = Store.Get(run.Id);
        Assert.Equal(0, unchanged.Revision);
        Assert.Single(unchanged.History);
        Assert.Equal("active", unchanged.Steps["intake"]);
        Assert.Equal("waiting", unchanged.Steps["operations"]);
    }

    [Fact]
    public void ReturnCreatesANewCycleAndPreservesOldApprovalsOnlyInHistory()
    {
        var run = Act(NewRun(), "intake", "requester");
        run = Act(run, "operations", "operations");
        var returned = Store.Apply(run.Id, Complete(run, "finance") with { Action = "return", Comment = "Need delivery quote", Confirmed = null }, Actor("finance")).Run;
        Assert.Equal(2, returned.Cycle);
        Assert.Equal("waiting", returned.Steps["operations"]);
        Assert.Contains(returned.History, entry => entry.Action == "complete" && entry.StepId == "operations" && entry.Cycle == 1);
        var resubmitted = Act(returned, "intake", "requester");
        Assert.Equal("active", resubmitted.Steps["operations"]);
        Assert.Equal("waiting", resubmitted.Steps["release"]);
    }

    [Fact]
    public async Task ConcurrentActionsCannotBothCommitFromTheSameRevision()
    {
        var run = Act(NewRun(), "intake", "requester");
        var commands = new[] { (Step: "operations", Role: "operations"), (Step: "finance", Role: "finance") };
        var results = await Task.WhenAll(commands.Select(item => Task.Run(() =>
        {
            try { Store.Apply(run.Id, Complete(run, item.Step), Actor(item.Role)); return 200; }
            catch (WorkbenchException error) { return error.Status; }
        })));
        Assert.Equal(new[] { 200, 409 }, results.Order().ToArray());
        Assert.Equal(2, Store.Get(run.Id).Revision);
        Assert.Equal(3, Store.Get(run.Id).History.Length);
    }

    [Fact]
    public void PublishRequiresCoordinatorAndCurrentVersion()
    {
        Assert.Equal(403, Assert.Throws<WorkbenchException>(() => Store.Publish(new(1, Engine.Example()), Actor("finance"))).Status);
        Store.Publish(new(1, Engine.Example()), Actor("coordinator"));
        Assert.Equal(409, Assert.Throws<WorkbenchException>(() => Store.Publish(new(1, Engine.Example()), Actor("coordinator"))).Status);
        Assert.Equal(2, Store.Versions().Length);
    }

    [Fact]
    public void ForeignDatabaseIsRejectedWithoutChangingItsContents()
    {
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "CREATE TABLE PrivateData(value TEXT); INSERT INTO PrivateData VALUES('keep');";
            command.ExecuteNonQuery();
        }
        Assert.Throws<InvalidOperationException>(() => new WorkbenchStore(path));
        using var check = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        check.Open();
        using var query = check.CreateCommand(); query.CommandText = "SELECT value FROM PrivateData;";
        Assert.Equal("keep", query.ExecuteScalar());
    }

    public void Dispose() { if (File.Exists(path)) File.Delete(path); }
}
